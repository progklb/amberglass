using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>
/// What a workspace contains, and what preparing it would cost.
///
/// The figures are estimates from measured averages rather than predictions, and the dialog says so.
/// They exist to answer one question — "is this minutes or is this hours?" — because the honest answer
/// for a large library is hours, and that is worth knowing before starting rather than after.
/// </summary>
public sealed record WorkspaceEstimate(int Images, int RawImages, int Videos, long VideoBytes)
{
    public static readonly WorkspaceEstimate Empty = new(0, 0, 0, 0);

    /// <summary>
    /// Measured: a 26MP RAF develops to a 6.6 MB rendition at quality 95 without chroma subsampling.
    /// </summary>
    public const long RenditionBytes = 6_600_000;

    /// <summary>Measured through the real chain: 3.8–4.2 s for a 26MP RAF or a 31MP DNG.</summary>
    public const double RawDevelopSeconds = 3.9;

    /// <summary>
    /// The two thumbnail tiers together, 320px and 1600px. Small enough that the exact figure hardly
    /// matters against the renditions, but it is the part that makes first browsing instant.
    /// </summary>
    public const long ThumbnailBytes = 150_000;

    public const double ThumbnailSeconds = 0.2;

    /// <summary>
    /// Rough proportion of a source file a 720p proxy occupies. Deliberately crude — it varies with
    /// the source bitrate far more than with anything measurable up front, which is why the dialog
    /// presents video as a range and not a number.
    /// </summary>
    public const double ProxyBytesPerSourceByte = 0.12;

    public int Files => Images + Videos;

    public bool IsEmpty => Files == 0;

    /// <summary>Disk the photographs would take: a rendition each for the RAWs, thumbnails for all.</summary>
    public long ImageBytes => ((long)RawImages * RenditionBytes) + ((long)Images * ThumbnailBytes);

    public long ProxyBytes => (long)(VideoBytes * ProxyBytesPerSourceByte);

    /// <summary>
    /// Wall-clock estimate for the photographs, given how many develops run at once. Videos are left
    /// out: their cost depends on running time, which is not known without probing every file.
    /// </summary>
    public TimeSpan EstimateImageTime(int parallelism)
    {
        var lanes = Math.Max(1, parallelism);
        var seconds = ((RawImages * RawDevelopSeconds) + (Images * ThumbnailSeconds)) / lanes;

        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>What to prepare. Photographs always; video is opt-in because it costs far more.</summary>
public sealed record PreparationOptions(bool IncludeVideoProxies = false, VideoQuality ProxyQuality = VideoQuality.P720);

public sealed record PreparationProgress(int Completed, int Total, string Stage, string? CurrentFile)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

public sealed record PreparationResult(int Prepared, int Failed, int Skipped, long BytesWritten, bool Cancelled);

/// <summary>
/// Warms the caches for a whole workspace up front, so that browsing and inspecting it later cost
/// nothing.
///
/// Everything here is work the application would otherwise do on demand; doing it in advance only
/// moves when it happens. That means it is always safe to cancel, and always safe to skip.
/// </summary>
public interface IWorkspacePreparer
{
    /// <summary>Counts what is there. A directory walk — no decoding, so it is quick even on a large tree.</summary>
    Task<WorkspaceEstimate> EstimateAsync(string workspacePath, CancellationToken cancellationToken = default);

    Task<PreparationResult> PrepareAsync(
        string workspacePath,
        PreparationOptions options,
        IProgress<PreparationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>How many photographs are worked on at once, which the estimate needs to know.</summary>
    int Parallelism { get; }
}
