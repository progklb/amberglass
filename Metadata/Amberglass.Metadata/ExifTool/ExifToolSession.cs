using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Amberglass.Metadata.ExifTool;

/// <summary>
/// A long-lived ExifTool process driven through its <c>-stay_open</c> protocol.
///
/// ExifTool is a Perl script; starting it costs a few hundred milliseconds. Paying that once per
/// file would make batch operations unusable, so one process is kept alive and fed arguments on
/// stdin. Each request ends with <c>-execute{n}</c> and ExifTool answers with <c>{ready{n}}</c>,
/// which is how a response is matched to its request.
///
/// Requests are serialised: a single process has a single stdin, so concurrent callers queue.
/// </summary>
public sealed class ExifToolSession : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly string _exifToolPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private Process? _process;
    private int _sequence;
    private bool _disposed;

    public ExifToolSession(string exifToolPath, ILogger logger)
    {
        _exifToolPath = exifToolPath;
        _logger = logger;
    }

    /// <summary>
    /// Runs one ExifTool command and returns everything it wrote to stdout.
    /// </summary>
    public async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = EnsureStarted();
            var sequence = ++_sequence;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            try
            {
                foreach (var argument in arguments)
                {
                    await process.StandardInput.WriteLineAsync(argument.AsMemory(), timeout.Token).ConfigureAwait(false);
                }

                await process.StandardInput.WriteLineAsync($"-execute{sequence}".AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

                return await ReadUntilReadyAsync(process, sequence, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The protocol is stateful: a timeout or broken pipe leaves the stream out of step
                // with the sequence numbers, so the process is discarded rather than reused.
                _logger.LogWarning(ex, "ExifTool request failed; restarting the session");
                KillProcess();
                throw;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private Process EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }

        KillProcess();

        var startInfo = new ProcessStartInfo
        {
            FileName = _exifToolPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        startInfo.ArgumentList.Add("-stay_open");
        startInfo.ArgumentList.Add("True");
        startInfo.ArgumentList.Add("-@");
        startInfo.ArgumentList.Add("-");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start ExifTool at {_exifToolPath}");

        // ExifTool reports warnings on stderr. Draining it continuously stops a full pipe buffer
        // from deadlocking the process mid-request.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _logger.LogDebug("ExifTool: {Message}", line);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The process is going away; nothing to report.
            }
        });

        _process = process;
        _sequence = 0;
        return process;
    }

    private static async Task<string> ReadUntilReadyAsync(Process process, int sequence, CancellationToken cancellationToken)
    {
        var readyMarker = $"{{ready{sequence}}}";
        var output = new StringBuilder();

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("ExifTool closed its output stream unexpectedly.");
            }

            var trimmed = line.TrimEnd();

            // The marker normally arrives on its own line, but if the preceding output did not end
            // with a newline it shares one. Matching only whole lines would hang until the timeout.
            if (trimmed.EndsWith(readyMarker, StringComparison.Ordinal))
            {
                var remainder = trimmed[..^readyMarker.Length];
                if (remainder.Length > 0)
                {
                    output.Append(remainder);
                }

                return output.ToString();
            }

            output.AppendLine(line);
        }
    }

    private void KillProcess()
    {
        if (_process is not { } process)
        {
            return;
        }

        _process = null;

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
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Ask ExifTool to exit cleanly before resorting to killing it.
        if (_process is { HasExited: false } process)
        {
            try
            {
                await process.StandardInput.WriteLineAsync("-stay_open").ConfigureAwait(false);
                await process.StandardInput.WriteLineAsync("False").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // Fall through to the kill below.
            }
        }

        KillProcess();
        _mutex.Dispose();
    }
}
