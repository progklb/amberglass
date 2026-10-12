using Amberglass.Preview.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// These tests mutate a process-wide environment variable, so they must not run alongside each
/// other.
/// </summary>
[Collection(nameof(FfmpegLocatorTests))]
[CollectionDefinition(nameof(FfmpegLocatorTests), DisableParallelization = true)]
public class FfmpegLocatorTests
{
    private const string OverrideVariable = "AMBERGLASS_FFMPEG_DIR";

    private static FfmpegLocator CreateWithOverride(string? directory)
    {
        var previous = Environment.GetEnvironmentVariable(OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(OverrideVariable, directory);
            return new FfmpegLocator(NullLogger<FfmpegLocator>.Instance);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OverrideVariable, previous);
        }
    }

    [Fact]
    public void Override_pointing_at_a_missing_directory_reports_unavailable()
    {
        var locator = CreateWithOverride(Path.Combine(Path.GetTempPath(), "amberglass-no-ffmpeg-" + Guid.NewGuid().ToString("N")));

        Assert.False(locator.IsAvailable);
        Assert.Null(locator.FfmpegPath);
    }

    [Fact]
    public void Override_wins_over_anything_installed_on_the_system()
    {
        using var temp = new TempFolder();
        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var planted = temp.CreateFile(executable);

        var locator = CreateWithOverride(temp.Path);

        Assert.True(locator.IsAvailable);
        Assert.Equal(planted, locator.FfmpegPath);
    }
}
