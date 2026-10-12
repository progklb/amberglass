using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Images;

/// <summary>
/// Finds LibRaw's <c>dcraw_emu</c>, the third optional external tool after ExifTool and FFmpeg.
///
/// Optional on purpose: without it RAW files still display, from their embedded preview. Only the
/// developed rendering is lost.
/// </summary>
public sealed class LibRawLocator : ILibRawLocator
{
    private static readonly string[] CommonInstallDirectories =
    [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/opt/local/bin"
    ];

    public LibRawLocator(ILogger<LibRawLocator> logger)
    {
        DcrawPath = Resolve("dcraw_emu");

        if (DcrawPath is null)
        {
            logger.LogInformation(
                "LibRaw was not found. RAW files will display from their embedded preview rather than being developed.");
        }
        else
        {
            logger.LogInformation("Using LibRaw at {Path}", DcrawPath);
        }
    }

    public string? DcrawPath { get; }

    public bool IsAvailable => DcrawPath is not null;

    private static string? Resolve(string toolName)
    {
        var executable = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

        // An explicit override is authoritative: if set and the tool is not there, LibRaw counts as
        // missing rather than silently falling back to another copy on the system.
        var configured = Environment.GetEnvironmentVariable("AMBERGLASS_LIBRAW_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = Path.Combine(configured, executable);
            return File.Exists(candidate) ? candidate : null;
        }

        // A GUI-launched app on macOS inherits a minimal PATH that usually excludes Homebrew.
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
