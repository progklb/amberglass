using System.Diagnostics;
using System.Text.Json;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Metadata.ExifTool;

/// <summary>
/// Extracts the embedded preview that cameras store inside RAW files.
///
/// Starting ExifTool costs far more than the extraction itself — measured at about 107ms of process
/// start against 31ms of actual decoding, and identical for a 20KB thumbnail and a 4.4MB preview — so
/// the work is batched. Requests arriving close together are collected and served by one ExifTool
/// command instead of one process each: measured over 24 files, 1789ms of separate processes against
/// 322ms batched.
///
/// The batch runs inside the shared <c>-stay_open</c> session, which normally cannot carry binary at
/// all: it reads stdout as text, line by line, watching for the <c>{ready}</c> marker, and JPEG bytes
/// pushed through it would be corrupted. <c>-W</c> is what makes it possible — ExifTool writes each
/// extracted image to its own file and reports only a line of text, so nothing binary passes through
/// stdout and no new process is needed.
///
/// The media files are only ever read.
/// </summary>
public sealed class ExifToolPreviewExtractor : IEmbeddedPreviewExtractor
{
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Most files one ExifTool command will be given at once. Past this the rest wait for the next
    /// one, so a folder-wide request cannot build a single command of thousands of paths.
    /// </summary>
    private const int MaxBatch = 32;

    /// <summary>
    /// Preview tags in descending order of usefulness. <c>PreviewImage</c> is typically a
    /// full-resolution JPEG; <c>ThumbnailImage</c> is a last resort at around 160px.
    /// </summary>
    private static readonly string[] PreviewTags =
    [
        "-PreviewImage",
        "-JpgFromRaw",
        "-OtherImage",
        "-ThumbnailImage"
    ];

    private readonly IExifToolLocator _locator;
    private readonly ExifToolHost _host;
    private readonly ILogger<ExifToolPreviewExtractor> _logger;

    private readonly Lock _batchGate = new();
    private List<Request> _pending = [];
    private bool _running;

    private sealed record Request(MediaFile File, TaskCompletionSource<EmbeddedPreview?> Completion);

    public ExifToolPreviewExtractor(
        IExifToolLocator locator,
        ExifToolHost host,
        ILogger<ExifToolPreviewExtractor> logger)
    {
        _locator = locator;
        _host = host;
        _logger = logger;
    }

    public bool IsAvailable => _locator.IsAvailable;

    public Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        if (_locator.ExifToolPath is null)
        {
            return Task.FromResult<EmbeddedPreview?>(null);
        }

        // Without the shared session there is nothing to batch into, so each file goes on its own.
        if (_host.Session is null)
        {
            return ExtractAloneAsync(file, cancellationToken);
        }

        var request = new Request(file, new TaskCompletionSource<EmbeddedPreview?>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        bool start;
        lock (_batchGate)
        {
            _pending.Add(request);

            // No waiting around to collect company. The session runs one command at a time, so when
            // it is idle a delay buys nothing and costs the whole wait; when it is busy the queue
            // fills by itself and the next command carries everything that arrived meanwhile. That
            // makes the batch size follow the load rather than a number guessed in advance.
            start = !_running;
            if (start)
            {
                _running = true;
            }
        }

        if (start)
        {
            _ = Task.Run(DrainAsync);
        }

        // The caller's cancellation detaches it from the batch; the batch itself runs on, because
        // its other members are still waiting and it is one command either way.
        return cancellationToken.CanBeCanceled
            ? request.Completion.Task.WaitAsync(cancellationToken)
            : request.Completion.Task;
    }

    /// <summary>Runs batches back to back for as long as requests keep arriving.</summary>
    private async Task DrainAsync()
    {
        while (true)
        {
            List<Request> batch;
            lock (_batchGate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    return;
                }

                if (_pending.Count <= MaxBatch)
                {
                    batch = _pending;
                    _pending = [];
                }
                else
                {
                    batch = _pending.GetRange(0, MaxBatch);
                    _pending.RemoveRange(0, MaxBatch);
                }
            }

            await FlushAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(List<Request> batch)
    {
        try
        {
            var previews = await RunBatchAsync(batch).ConfigureAwait(false);
            foreach (var request in batch)
            {
                request.Completion.TrySetResult(previews.GetValueOrDefault(request.File.FullPath));
            }
        }
        catch (Exception ex)
        {
            // A failed batch falls back to the per-file path rather than reporting no preview,
            // which would show as a blank tile for a file that has one.
            _logger.LogWarning(ex, "Batched preview extraction failed; falling back to one file at a time");

            foreach (var request in batch)
            {
                try
                {
                    request.Completion.TrySetResult(
                        await ExtractAloneAsync(request.File, CancellationToken.None).ConfigureAwait(false));
                }
                catch (Exception single)
                {
                    request.Completion.TrySetException(single);
                }
            }
        }
    }

    /// <summary>
    /// One ExifTool command for every file in the batch, then a second for their orientations.
    /// Two round trips through a session that is already open, in place of one process per file.
    /// </summary>
    private async Task<Dictionary<string, EmbeddedPreview>> RunBatchAsync(List<Request> batch)
    {
        var session = _host.Session!;
        var paths = batch.Select(r => r.File.FullPath).Distinct(StringComparer.Ordinal).ToList();
        var root = Path.Combine(Path.GetTempPath(), "betterdam-preview-" + Guid.NewGuid().ToString("N"));
        var results = new Dictionary<string, EmbeddedPreview>(StringComparer.Ordinal);

        try
        {
            Directory.CreateDirectory(root);

            // -W writes each extracted image beside a mirror of its source path, so two files with
            // the same name in different folders cannot overwrite one another, and %t keeps the four
            // candidate tags apart. A tag a file does not carry simply produces no output.
            var extract = new List<string> { "-b" };
            extract.AddRange(PreviewTags);
            extract.Add("-W");
            extract.Add(Path.Combine(root, "%d%f__%t.jpg"));
            extract.AddRange(paths);

            await session.ExecuteAsync(extract, CancellationToken.None).ConfigureAwait(false);

            var orientations = await ReadOrientationsAsync(session, paths).ConfigureAwait(false);

            foreach (var path in paths)
            {
                if (ReadBestPreview(root, path) is not { } jpeg)
                {
                    _logger.LogDebug("No embedded preview found in {File}", path);
                    continue;
                }

                var facts = orientations.GetValueOrDefault(path);
                results[path] = new EmbeddedPreview(jpeg, facts.Orientation, facts.Width, facts.Height);
            }

            return results;
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// The first tag that actually produced a file, in the order the tags are worth having.
    /// </summary>
    private byte[]? ReadBestPreview(string root, string path)
    {
        foreach (var tag in PreviewTags)
        {
            var written = OutputPathFor(root, path, tag.TrimStart('-'));

            try
            {
                if (!File.Exists(written))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(written);
                if (LooksLikeJpeg(bytes))
                {
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the extracted {Tag} of {File}", tag, path);
            }
        }

        return null;
    }

    /// <summary>
    /// Where <c>-W</c> puts the extraction for one file and tag: the source path rebuilt underneath
    /// <paramref name="root"/>, which is what <c>%d%f__%t</c> produces.
    /// </summary>
    internal static string OutputPathFor(string root, string sourcePath, string tag)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(sourcePath);

        // The source directory is absolute, so it is appended rather than combined — Path.Combine
        // would discard the root and hand back the original location.
        return Path.GetFullPath(
            root + Path.DirectorySeparatorChar + directory + Path.DirectorySeparatorChar + $"{name}__{tag}.jpg");
    }

    private async Task<Dictionary<string, (int? Orientation, int? Width, int? Height)>> ReadOrientationsAsync(
        ExifToolSession session,
        List<string> paths)
    {
        var found = new Dictionary<string, (int?, int?, int?)>(StringComparer.Ordinal);

        try
        {
            // -n for the orientation as its number rather than ExifTool's wording. The stored size
            // comes along because it is what tells us whether the preview has already been rotated.
            var arguments = new List<string> { "-json", "-n", "-EXIF:Orientation", "-ImageWidth", "-ImageHeight" };
            arguments.AddRange(paths);

            var json = await session.ExecuteAsync(arguments, CancellationToken.None).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return found;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("SourceFile", out var source) && source.GetString() is { } file)
                {
                    found[file] = (Number(entry, "Orientation"), Number(entry, "ImageWidth"), Number(entry, "ImageHeight"));
                }
            }
        }
        catch (Exception ex)
        {
            // A preview the right way up is worth more than no preview.
            _logger.LogDebug(ex, "Could not read orientations for a batch of {Count} file(s)", paths.Count);
        }

        return found;
    }

    private void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear the extraction folder {Folder}", root);
        }
    }

    /// <summary>
    /// The original path: one process, one file. Kept for when there is no session to batch into and
    /// as the fallback when a batch fails.
    /// </summary>
    private async Task<EmbeddedPreview?> ExtractAloneAsync(MediaFile file, CancellationToken cancellationToken)
    {
        if (_locator.ExifToolPath is not { } exifTool)
        {
            return null;
        }

        foreach (var tag in PreviewTags)
        {
            var preview = await TryExtractAsync(exifTool, tag, file, cancellationToken).ConfigureAwait(false);
            if (preview is not null)
            {
                if (_host.Session is not { } session)
                {
                    return new EmbeddedPreview(preview);
                }

                var facts = (await ReadOrientationsAsync(session, [file.FullPath]).ConfigureAwait(false))
                    .GetValueOrDefault(file.FullPath);

                return new EmbeddedPreview(preview, facts.Orientation, facts.Width, facts.Height);
            }
        }

        _logger.LogDebug("No embedded preview found in {File}", file.FullPath);
        return null;
    }

    private static int? Number(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private async Task<byte[]?> TryExtractAsync(
        string exifTool,
        string tag,
        MediaFile file,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exifTool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add(tag);
        startInfo.ArgumentList.Add(file.FullPath);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExtractionTimeout);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (buffer.Length == 0)
            {
                return null;
            }

            var bytes = buffer.ToArray();
            return LooksLikeJpeg(bytes) ? bytes : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ExifTool timed out extracting {Tag} from {File}", tag, file.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExifTool failed extracting {Tag} from {File}", tag, file.FullPath);
            return null;
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    /// <summary>
    /// Guards against ExifTool writing a diagnostic to stdout instead of image data — decoding
    /// that would fail later and much less clearly.
    /// </summary>
    private static bool LooksLikeJpeg(byte[] bytes)
        => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            _logger.LogDebug(ex, "Unable to terminate the ExifTool process");
        }
    }
}
