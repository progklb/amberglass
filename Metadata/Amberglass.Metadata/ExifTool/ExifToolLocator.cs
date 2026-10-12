using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Metadata.ExifTool;

public sealed class ExifToolLocator : IExifToolLocator
{
    private static readonly string[] CommonInstallDirectories =
    [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/opt/local/bin"
    ];

    public ExifToolLocator(ILogger<ExifToolLocator> logger)
    {
        ExifToolPath = Resolve();

        if (ExifToolPath is null)
        {
            logger.LogInformation("ExifTool was not found. Metadata reading will be unavailable.");
        }
        else
        {
            logger.LogInformation("Using ExifTool at {Path}", ExifToolPath);
        }
    }

    public string? ExifToolPath { get; }

    public bool IsAvailable => ExifToolPath is not null;

    private static string? Resolve()
    {
        var executable = OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";

        // An explicit override is authoritative: if it is set and the tool is not there, ExifTool is
        // treated as unavailable rather than silently falling back to some other copy on the system.
        var configured = Environment.GetEnvironmentVariable("AMBERGLASS_EXIFTOOL_DIR");
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
