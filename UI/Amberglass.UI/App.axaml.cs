using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Amberglass.Core.Interfaces;
using Amberglass.UI.Services;
using Amberglass.UI.ViewModels;
using Amberglass.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Amberglass.UI;

public partial class App : Application
{
    /// <summary>
    /// Set by <see cref="Program"/> before the Avalonia app starts, so logging can be configured
    /// against the same cache location the rest of the application uses.
    /// </summary>
    internal static IAppPaths? Paths { get; set; }

    internal static ISettingsService? Settings { get; set; }

    /// <summary>Optional folder passed on the command line, opened once the window is up.</summary>
    internal static string? StartupFolder { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Settings sits in the macOS application menu, which is application-scoped and so cannot bind
    /// to the window's ViewModel. Forward to the window, which owns the dialog.
    /// </summary>
    private void OnOpenSettings(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
        {
            _ = window.OpenSettingsAsync();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Settings ?? new Core.Services.JsonSettingsService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Services.JsonSettingsService>.Instance);

            // Before any window exists, so the application never shows in one theme and repaints
            // into another. Changed fires from whichever thread called SaveAsync, so the repaint is
            // posted rather than applied where it lands.
            AppThemes.Apply(this, settings.Current);
            settings.Changed += (_, updated) =>
                Dispatcher.UIThread.Post(() => AppThemes.Apply(this, updated));

            var services = new ServiceCollection()
                .AddAmberglass(Paths ?? new Core.Services.AppPaths(settings), settings)
                .BuildServiceProvider();

            // Apply any rolling-cache limit from a previous session before browsing adds more.
            var maintenance = services.GetRequiredService<ICacheMaintenance>();
            _ = Task.Run(() => maintenance.TrimAsync());

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
                SettingsViewModelFactory = services.GetRequiredService<SettingsViewModel>,
                PrepareWorkspaceViewModelFactory = services.GetRequiredService<PrepareWorkspaceViewModel>,
                SyncViewModelFactory = services.GetRequiredService<SyncViewModel>,
                ChangesViewModelFactory = services.GetRequiredService<ChangesViewModel>
            };

            // A folder on the command line wins over the remembered one; otherwise reopen the last
            // workspace so the application starts where it was left.
            if ((StartupFolder ?? settings.Current.LastWorkspacePath) is { } folder)
            {
                _ = viewModel.OpenPathAsync(folder);
            }

            // Torn down on Exit rather than ShutdownRequested, and asynchronously. ShutdownRequested
            // fires before any window is asked whether it may close, so disposing there both ran
            // ahead of the main window's close guard and — because the ExifTool host is async-only
            // to dispose — threw, which is what "terminated unexpectedly" on every quit was.
            // Exit fires once the windows have gone and there is nothing left to cancel.
            desktop.Exit += (_, _) => services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
