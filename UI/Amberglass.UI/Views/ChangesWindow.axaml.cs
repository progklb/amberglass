using Avalonia.Controls;
using Avalonia.Interactivity;
using Amberglass.UI.ViewModels;

namespace Amberglass.UI.Views;

public partial class ChangesWindow : Window
{
    public ChangesWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (DataContext is ChangesViewModel viewModel)
            {
                viewModel.Load();
            }
        };

        // The ViewModel listens to the pending store, which outlives this window.
        Closed += (_, _) => (DataContext as ChangesViewModel)?.Detach();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>The answer the close guard is waiting for.</summary>
    private void OnQuitAnyway(object? sender, RoutedEventArgs e) => Close(true);
}
