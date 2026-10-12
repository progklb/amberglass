using Avalonia;
using Amberglass.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Amberglass.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Settings are read before anything else so the cache location is known from the start.
        // The settings service deliberately does not depend on AppPaths, which would be circular.
        var settings = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance);
        var paths = new AppPaths(settings);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(paths.LogRoot, "amberglass-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            App.Paths = paths;
            App.Settings = settings;
            App.StartupFolder = args.FirstOrDefault(a => !a.StartsWith('-'));
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Amberglass terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Referenced by the Avalonia XAML previewer/designer as well as Main.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
