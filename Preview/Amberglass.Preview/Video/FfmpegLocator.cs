using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Video;

public sealed class FfmpegLocator : IFfmpegLocator
{
    private static readonly string[] CommonInstallDirectories =
    [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/opt/local/bin"
    ];

    public FfmpegLocator(ILogger<FfmpegLocator> logger)
    {
        FfmpegPath = Resolve("ffmpeg");
        FfprobePath = Resolve("ffprobe");

        if (FfmpegPath is null)
        {
            logger.LogInformation("FFmpeg was not found. Video thumbnails and playback will be unavailable.");
        }
        else
        {
            logger.LogInformation("Using FFmpeg at {Path}", FfmpegPath);
        }
    }

    public string? FfmpegPath { get; }

    public string? FfprobePath { get; }

    public bool IsAvailable => FfmpegPath is not null;

    private static string? Resolve(string toolName)
    {
        var executable = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

        // An explicit override is authoritative: if it is set and the tool is not there, FFmpeg is
        // treated as unavailable rather than silently falling back to some other copy on the system.
        var configured = Environment.GetEnvironmentVariable("AMBERGLASS_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = Path.Combine(configured, executable);
            return File.Exists(candidate) ? candidate : null;
        }

        // A GUI-launched app on macOS inherits a minimal PATH that usually excludes Homebrew, so
        // the common install directories are checked as well as PATH.
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var searchDirectories = pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(CommonInstallDirectories);

        foreach (var directory in searchDirectories)
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry should not abort the search.
            }
        }

        return null;
    }
}
