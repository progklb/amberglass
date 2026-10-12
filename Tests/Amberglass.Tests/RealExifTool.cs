using Amberglass.Core.Interfaces;

namespace Amberglass.Tests;

/// <summary>
/// Locates a real ExifTool for integration tests. When it is not installed the tests that need it
/// return early rather than failing — the suite still has to pass on a machine without it.
/// </summary>
internal static class RealExifTool
{
    private static readonly string[] Candidates =
    [
        "/opt/homebrew/bin/exiftool",
        "/usr/local/bin/exiftool",
        "/usr/bin/exiftool"
    ];

    public static string? Path { get; } = FindPath();

    public static bool IsAvailable => Path is not null;

    private static string? FindPath()
    {
        var configured = Environment.GetEnvironmentVariable("AMBERGLASS_EXIFTOOL_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = System.IO.Path.Combine(configured, "exiftool");
            return File.Exists(candidate) ? candidate : null;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = pathVariable
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d, "exiftool"))
            .Concat(Candidates);

        return directories.FirstOrDefault(File.Exists);
    }

    public sealed class Locator : IExifToolLocator
    {
        public string? ExifToolPath => Path;

        public bool IsAvailable => Path is not null;
    }
}
