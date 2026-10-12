using Avalonia.Controls;
using Avalonia.Interactivity;
using Amberglass.UI.ViewModels;

namespace Amberglass.UI.Views;

public partial class PrepareWorkspaceWindow : Window
{
    public PrepareWorkspaceWindow()
    {
        InitializeComponent();

        // Counting happens once the dialog is up, so a large tree shows a window that is sizing
        // itself up rather than nothing at all.
        Opened += async (_, _) =>
        {
            if (DataContext is PrepareWorkspaceViewModel viewModel)
            {
                await viewModel.EstimateAsync();
            }
        };

        // Closing the window stops the work it was driving.
        //
        // Only the Stop button cancelled before, so shutting the window with its own close button
        // left the preparation running with nothing on screen reporting it and no way to reach it —
        // several thousand RAW develops continuing invisibly, which shows up as the whole
        // application feeling slow for no apparent reason.
        //
        // Stopping rather than refusing to close: preparation writes only to the cache and keeps
        // what it has finished, so there is nothing to lose and nothing to confirm.
        Closing += (_, _) =>
        {
            if (DataContext is PrepareWorkspaceViewModel { IsRunning: true } viewModel)
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
