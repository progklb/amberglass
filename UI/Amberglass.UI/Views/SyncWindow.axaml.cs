using Avalonia.Controls;
using Avalonia.Interactivity;
using Amberglass.UI.ViewModels;

namespace Amberglass.UI.Views;

public partial class SyncWindow : Window
{
    public SyncWindow()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is SyncViewModel viewModel)
            {
                await viewModel.PrepareAsync();
            }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
