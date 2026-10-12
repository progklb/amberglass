namespace Amberglass.Core.Models;

/// <summary>
/// A file discovered on disk during a library scan. This is intentionally lightweight —
/// it does not yet carry metadata; that is layered on top in later phases.
/// </summary>
public sealed record MediaFile
{
    public required string FullPath { get; init; }

    public required string FileName { get; init; }

    public required MediaType MediaType { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTimeOffset ModifiedUtc { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public static MediaFile FromFileInfo(FileInfo info)
    {
        return new MediaFile
        {
            FullPath = info.FullName,
            FileName = info.Name,
            MediaType = MediaTypeRegistry.GetMediaType(info.FullName),
            SizeBytes = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            CreatedUtc = info.CreationTimeUtc
        };
    }
}
