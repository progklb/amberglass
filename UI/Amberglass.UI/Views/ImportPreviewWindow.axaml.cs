using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Amberglass.Core.Services;

namespace Amberglass.UI.Views;

/// <summary>
/// Shows what an import would add, and asks before it does it.
///
/// The button has always said "Import from workspace…", and the ellipsis is a promise: something
/// will be asked before anything happens. It used to import on the spot, which made the button a
/// trap for the one thing people most often do with an unfamiliar button, which is press it to find
/// out what it does — and what it does is change a vocabulary that took a while to arrange.
/// </summary>
public partial class ImportPreviewWindow : Window
{
    // InitializeComponent, not AvaloniaXamlLoader.Load: the generated method is what assigns the
    // x:Name fields, and loading the XAML directly leaves every one of them null.
    public ImportPreviewWindow() => InitializeComponent();

    /// <param name="noun">"keyword" or "label" — the summary reads as a sentence about them.</param>
    public ImportPreviewWindow(ImportPlan plan, string noun) : this()
    {
        Title = $"Import {noun}s from workspace";

        Summary.Text = plan.HasAnythingToAdd
            ? $"Found {plan.Considered:N0} {noun}(s) in this workspace. {plan.ToAdd.Length:N0} of them would be added to your library."
            : $"Found {plan.Considered:N0} {noun}(s) in this workspace, and your library already has every one.";

        AddHeading.Text = plan.HasAnythingToAdd
            ? $"To add ({plan.ToAdd.Length:N0})"
            : "Nothing to add";

        AddList.ItemsSource = plan.ToAdd;

        // Only worth the room when there is something in it, and only worth the reassurance when
        // something is actually going to happen.
        KnownSection.IsVisible = plan.AlreadyKnown.Length > 0;
        KnownHeading.Text = $"Already in your library ({plan.AlreadyKnown.Length:N0})";
        KnownList.ItemsSource = plan.AlreadyKnown;

        Reassurance.IsVisible = plan.HasAnythingToAdd;

        // Nothing to agree to, so the only honest button is the one that closes the window.
        ConfirmButton.IsVisible = plan.HasAnythingToAdd;

        // Hand the height to whichever list is carrying the answer. An empty bordered box taking up
        // half the window says nothing and looks like something failed to load.
        if (!plan.HasAnythingToAdd)
        {
            AddSection.IsVisible = false;
            Lists.RowDefinitions[0].Height = new GridLength(0);
            Lists.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            KnownBox.MaxHeight = double.PositiveInfinity;
            KnownSection.Margin = new Thickness(0);
        }
    }

    /// <summary>True when the user asked for the import to go ahead.</summary>
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
