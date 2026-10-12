using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

public sealed record SidecarWriteOptions
{
    /// <summary>
    /// Read the sidecar back after writing and confirm it says what we asked for. Cheap next to the
    /// write itself, and it is the difference between "we ran a command" and "the data is there".
    /// </summary>
    public bool ValidateAfterWrite { get; init; } = true;
}

public sealed record SidecarWriteResult(
    string FilePath,
    bool Success,
    string? SidecarPath = null,
    string? Error = null)
{
    public static SidecarWriteResult Failed(string filePath, string error)
        => new(filePath, false, Error: error);
}

public sealed record EmbedWriteOptions
{
    /// <summary>Keep a copy of the original alongside it before modifying.</summary>
    public bool BackupOriginal { get; init; } = true;

    /// <summary>
    /// Leave the filesystem modification time alone. Bridge changing timestamps on every keyword
    /// edit is the complaint this whole project started from.
    /// </summary>
    public bool PreserveTimestamps { get; init; } = true;

    public bool ValidateAfterWrite { get; init; } = true;
}

public sealed record EmbedWriteResult(
    string FilePath,
    bool Success,
    string? BackupPath = null,
    string? Error = null)
{
    public static EmbedWriteResult Failed(string filePath, string error) => new(filePath, false, Error: error);
}

/// <summary>
/// Writes user-editable metadata to an XMP sidecar.
///
/// The contract that matters: <b>this never modifies the media file.</b> Ordinary metadata editing
/// writes only to <c>&lt;name&gt;.xmp</c>, leaving the original bytes and its modification time
/// untouched. Embedding into the media file is a separate, explicit operation (Phase 6 Sync).
///
/// Updating an existing sidecar preserves tags this application does not understand — only the
/// fields it manages are touched.
/// </summary>
public interface IMetadataWriter
{
    bool IsAvailable { get; }

    Task<SidecarWriteResult> WriteSidecarAsync(
        MediaFile file,
        EditableMetadata metadata,
        SidecarWriteOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes metadata <b>into the media file itself</b>.
    ///
    /// This is the only operation in the application that modifies a user's original media, and it
    /// exists solely to serve Sync. Everything else writes sidecars. Callers are expected to have
    /// obtained explicit consent before calling it.
    /// </summary>
    Task<EmbedWriteResult> WriteEmbeddedAsync(
        MediaFile file,
        EditableMetadata metadata,
        EmbedWriteOptions options,
        CancellationToken cancellationToken = default);
}
