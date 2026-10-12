using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Metadata.ExifTool;
using Microsoft.Extensions.Logging;

using Amberglass.Core.Services;

namespace Amberglass.Metadata.Xmp;

/// <summary>
/// Writes editable metadata to an XMP sidecar with ExifTool.
///
/// Safety properties this class is responsible for:
/// <list type="bullet">
/// <item>The media file is never opened for writing — the target is always a <c>.xmp</c> path, and
/// that is asserted before any command runs.</item>
/// <item>Updating an existing sidecar only touches the fields we manage, so vendor or
/// application-specific XMP written by other tools survives.</item>
/// <item>Writes are optionally read back and verified.</item>
/// </list>
/// </summary>
public sealed class ExifToolSidecarWriter : IMetadataWriter
{
    private readonly ExifToolHost _host;
    private readonly IMetadataProvider _reader;
    private readonly ILogger<ExifToolSidecarWriter> _logger;

    public ExifToolSidecarWriter(ExifToolHost host, IMetadataProvider reader, ILogger<ExifToolSidecarWriter> logger)
    {
        _host = host;
        _reader = reader;
        _logger = logger;
    }

    public bool IsAvailable => _host.IsAvailable;

    public async Task<SidecarWriteResult> WriteSidecarAsync(
        MediaFile file,
        EditableMetadata metadata,
        SidecarWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_host.Session is not { } session)
        {
            return SidecarWriteResult.Failed(file.FullPath, "ExifTool is not available.");
        }

        // A rejected file has no stars, because Adobe expresses rejection as a rating of -1 and the
        // two share one property. Applied before writing and before validating, so what is asked for
        // is what can be read back.
        metadata = metadata.Normalised();

        // Update the sidecar that already exists (either naming convention), otherwise create the
        // Adobe-style one.
        var sidecarPath = XmpSidecar.Find(file.FullPath) ?? XmpSidecar.GetPreferredPath(file.FullPath);

        if (!IsSafeSidecarTarget(file.FullPath, sidecarPath))
        {
            // Belt and braces: refuse rather than risk writing into the user's original media.
            var error = $"Refusing to write metadata to a non-sidecar path: {sidecarPath}";
            _logger.LogError("{Error}", error);
            return SidecarWriteResult.Failed(file.FullPath, error);
        }

        var temporaryValueFiles = new List<string>();

        try
        {
            var arguments = BuildArguments(metadata, sidecarPath, temporaryValueFiles);
            var output = await session.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);

            if (!IndicatesSuccess(output))
            {
                _logger.LogWarning("ExifTool did not confirm the sidecar write for {File}: {Output}", file.FullPath, output.Trim());
                return SidecarWriteResult.Failed(file.FullPath, Summarise(output));
            }

            if (options.ValidateAfterWrite)
            {
                var validationError = await ValidateAsync(file, metadata, cancellationToken).ConfigureAwait(false);
                if (validationError is not null)
                {
                    return SidecarWriteResult.Failed(file.FullPath, validationError);
                }
            }

            _logger.LogInformation("Wrote XMP sidecar {Sidecar}", sidecarPath);
            return new SidecarWriteResult(file.FullPath, true, sidecarPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed writing the sidecar for {File}", file.FullPath);
            return SidecarWriteResult.Failed(file.FullPath, ex.Message);
        }
        finally
        {
            foreach (var path in temporaryValueFiles)
            {
                TryDelete(path);
            }
        }
    }

    /// <summary>
    /// Writes metadata into the media file itself. Unlike every other write in the application,
    /// this modifies the user's original — see <see cref="IMetadataWriter.WriteEmbeddedAsync"/>.
    /// </summary>
    public async Task<EmbedWriteResult> WriteEmbeddedAsync(
        MediaFile file,
        EditableMetadata metadata,
        EmbedWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_host.Session is not { } session)
        {
            return EmbedWriteResult.Failed(file.FullPath, "ExifTool is not available.");
        }

        if (!File.Exists(file.FullPath))
        {
            return EmbedWriteResult.Failed(file.FullPath, "The file no longer exists.");
        }

        var temporaryValueFiles = new List<string>();

        try
        {
            var arguments = BuildEmbedArguments(metadata, file.FullPath, options, temporaryValueFiles);
            var output = await session.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);

            if (!IndicatesSuccess(output))
            {
                _logger.LogWarning("ExifTool did not confirm the embed for {File}: {Output}", file.FullPath, output.Trim());
                return EmbedWriteResult.Failed(file.FullPath, Summarise(output));
            }

            // ExifTool names its own backup "<file>_original" when -overwrite_original is omitted.
            var backupPath = options.BackupOriginal ? file.FullPath + "_original" : null;
            if (backupPath is not null && !File.Exists(backupPath))
            {
                backupPath = null;
            }

            if (options.ValidateAfterWrite)
            {
                var error = await ValidateEmbeddedAsync(file, metadata, cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    return new EmbedWriteResult(file.FullPath, false, backupPath, error);
                }
            }

            _logger.LogInformation("Embedded metadata into {File}", file.FullPath);
            return new EmbedWriteResult(file.FullPath, true, backupPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed embedding metadata into {File}", file.FullPath);
            return EmbedWriteResult.Failed(file.FullPath, ex.Message);
        }
        finally
        {
            foreach (var path in temporaryValueFiles)
            {
                TryDelete(path);
            }
        }
    }

    private static List<string> BuildEmbedArguments(
        EditableMetadata metadata,
        string mediaPath,
        EmbedWriteOptions options,
        List<string> temporaryValueFiles)
    {
        var arguments = new List<string>();

        // Omitting -overwrite_original is what makes ExifTool leave "<file>_original" behind, so the
        // backup uses its own well-tested path rather than a copy of our own devising.
        if (!options.BackupOriginal)
        {
            arguments.Add("-overwrite_original");
        }

        if (options.PreserveTimestamps)
        {
            arguments.Add("-P");
        }

        AddMetadataArguments(arguments, metadata, temporaryValueFiles);
        arguments.Add(mediaPath);
        return arguments;
    }

    /// <summary>Confirms the file now reports what was asked for.</summary>
    private async Task<string?> ValidateEmbeddedAsync(MediaFile file, EditableMetadata expected, CancellationToken cancellationToken)
    {
        var reread = await _reader.ReadAsync(file, cancellationToken).ConfigureAwait(false);
        if (reread is null)
        {
            return "The file could not be read back after writing.";
        }

        if (!reread.Embedded.ValueEquals(expected))
        {
            _logger.LogWarning(
                "Embed validation mismatch for {File}. Expected title={ExpectedTitle} rating={ExpectedRating}, found title={ActualTitle} rating={ActualRating}",
                file.FullPath, expected.Title, expected.Rating, reread.Embedded.Title, reread.Embedded.Rating);

            return "The file was written but does not match what was requested.";
        }

        return null;
    }

    /// <summary>The target must be a .xmp file and must not be the media file itself.</summary>
    internal static bool IsSafeSidecarTarget(string mediaPath, string sidecarPath)
        => Path.GetExtension(sidecarPath).Equals(".xmp", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(Path.GetFullPath(sidecarPath), Path.GetFullPath(mediaPath), StringComparison.OrdinalIgnoreCase);

    private static List<string> BuildArguments(
        EditableMetadata metadata,
        string sidecarPath,
        List<string> temporaryValueFiles)
    {
        // The sidecar is ours to manage, so no _original backup copies are left lying around.
        var arguments = new List<string> { "-overwrite_original" };

        AddMetadataArguments(arguments, metadata, temporaryValueFiles);

        arguments.Add(sidecarPath);
        return arguments;
    }

    /// <summary>
    /// The tag assignments themselves, shared by sidecar and embedded writes so the two cannot
    /// drift apart on what "the metadata Amberglass manages" means.
    /// </summary>
    private static void AddMetadataArguments(
        List<string> arguments,
        EditableMetadata metadata,
        List<string> temporaryValueFiles)
    {
        AddValue(arguments, "XMP:Title", metadata.Title, temporaryValueFiles);
        AddValue(arguments, "XMP:Description", metadata.Description, temporaryValueFiles);
        AddValue(arguments, "XMP:Headline", metadata.Headline, temporaryValueFiles);
        // Only the name. The numeric colour fields the other applications use — digiKam's
        // ColorLabel and Photo Mechanic's ColorClass — are indices into their own colour scales,
        // and those scales disagree with each other and with any slot order chosen here. Writing a
        // number into them would show a confident wrong colour elsewhere, which is worse than
        // showing none: xmp:Label alone is what Bridge and Lightroom read, and it round-trips exactly.
        AddValue(arguments, "XMP:Label", metadata.Label, temporaryValueFiles);
        AddValue(arguments, "XMP:Creator", metadata.Creator, temporaryValueFiles);
        AddValue(arguments, "XMP:Rights", metadata.Copyright, temporaryValueFiles);
        AddFlagArguments(arguments, metadata, temporaryValueFiles);

        // Keywords replace the existing list rather than adding to it.
        //
        // This must use repeated plain assignment (`-XMP:Subject=a -XMP:Subject=b`), which ExifTool
        // treats as "set the list to these values". The intuitive `-XMP:Subject=` followed by
        // `-XMP:Subject+=a` does NOT work: the empty assignment is ignored when append operations
        // follow in the same command, and the keywords are appended to the old list instead —
        // so removed keywords would silently survive and duplicates would accumulate.
        if (metadata.Keywords.IsDefaultOrEmpty)
        {
            arguments.Add("-XMP:Subject=");
        }
        else
        {
            foreach (var keyword in metadata.Keywords)
            {
                arguments.Add($"-XMP:Subject={keyword}");
            }
        }
    }

    /// <summary>
    /// An empty assignment deletes the tag, which is how a cleared field is represented.
    ///
    /// ExifTool argument files are line-based, so a value containing a newline — a multi-line
    /// description, typically — cannot be passed inline. Those are written to a temp file and
    /// referenced with ExifTool's <c>&lt;=</c> "read value from file" syntax.
    /// </summary>
    /// <summary>
    /// Writes the cull flag in every convention there is one for, and the rating alongside it.
    ///
    /// No single property is read by everything, so rather than pick a winner this writes all four
    /// and lets each application find the one it knows:
    ///
    /// <list type="bullet">
    /// <item><c>XMP-digiKam:PickLabel</c> — carries accepted and rejected; digiKam.</item>
    /// <item><c>XMP-photomech:Tagged</c> — carries picked; Photo Mechanic.</item>
    /// <item><c>XMP-xmpDM:Good</c> — carries both as a boolean; Lightroom.</item>
    /// <item><c>xmp:Rating = -1</c> — carries rejected; Bridge and Camera Raw.</item>
    /// </list>
    ///
    /// The rating is written here rather than beside the other fields because rejecting has to take
    /// it over: Adobe expresses rejection <i>as</i> a rating, so a rejected file's rating is -1 and
    /// its stars are not representable. Clearing the rejection puts the stars back, since this
    /// application keeps them separately and has not forgotten them.
    ///
    /// Tagged needs the <c>#</c> suffix. Without it ExifTool refuses the value with
    /// "not in PrintConv", which is a write that fails rather than one that quietly does nothing.
    /// </summary>
    private static void AddFlagArguments(
        List<string> arguments,
        EditableMetadata metadata,
        List<string> temporaryValueFiles)
    {
        var rejected = metadata.Flag == MediaFlag.Rejected;

        AddValue(
            arguments,
            "XMP:Rating",
            rejected ? "-1" : metadata.Rating?.ToString(),
            temporaryValueFiles);

        AddValue(
            arguments,
            "XMP-digiKam:PickLabel",
            metadata.Flag is { } flag ? ((int)flag).ToString() : null,
            temporaryValueFiles);

        var tagged = metadata.Flag switch
        {
            MediaFlag.Accepted => "True",
            MediaFlag.Rejected => "False",
            _ => null
        };

        AddValue(arguments, "XMP-photomech:Tagged#", tagged, temporaryValueFiles);

        // The same two words carry Lightroom's flag, which takes them without the suffix.
        AddValue(arguments, "XMP-xmpDM:Good", tagged, temporaryValueFiles);
    }

    private static void AddValue(List<string> arguments, string tag, string? value, List<string> temporaryValueFiles)
    {
        if (value is null)
        {
            arguments.Add($"-{tag}=");
            return;
        }

        if (!value.Contains('\n') && !value.Contains('\r'))
        {
            arguments.Add($"-{tag}={value}");
            return;
        }

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"amberglass-xmp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(temporaryPath, value);
        temporaryValueFiles.Add(temporaryPath);
        arguments.Add($"-{tag}<={temporaryPath}");
    }

    private static bool IndicatesSuccess(string output)
    {
        // ExifTool reports "1 image files created" or "1 image files updated" on success, and
        // anything containing "Error" or "0 image files" is a failure.
        var text = output.Trim();
        return text.Contains("files created", StringComparison.OrdinalIgnoreCase)
               || text.Contains("files updated", StringComparison.OrdinalIgnoreCase);
    }

    private static string Summarise(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.FirstOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase))
               ?? (lines.Length > 0 ? lines[^1] : "ExifTool reported no changes.");
    }

    /// <summary>Reads the sidecar back and confirms the values we asked for are the values present.</summary>
    private async Task<string?> ValidateAsync(MediaFile file, EditableMetadata expected, CancellationToken cancellationToken)
    {
        var reread = await _reader.ReadAsync(file, cancellationToken).ConfigureAwait(false);
        if (reread?.Sidecar is not { } actual)
        {
            return "The sidecar could not be read back after writing.";
        }

        if (!actual.ValueEquals(expected))
        {
            _logger.LogWarning(
                "Sidecar validation mismatch for {File}. Expected title={ExpectedTitle} rating={ExpectedRating}, found title={ActualTitle} rating={ActualRating}",
                file.FullPath, expected.Title, expected.Rating, actual.Title, actual.Rating);

            return "The sidecar was written but does not match what was requested.";
        }

        return null;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remove the temporary value file {Path}", path);
        }
    }
}
