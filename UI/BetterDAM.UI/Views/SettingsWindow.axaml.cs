using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using BetterDAM.Core.Services;
using BetterDAM.UI.ViewModels;

namespace BetterDAM.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        Opened += async (_, _) =>
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.StorageProvider = StorageProvider;

                // Shared by both imports: the label command reaches it through the keyword editor
                // rather than holding a second copy of the same hook.
                viewModel.Keywords.ConfirmImport = ConfirmImportAsync;

                await viewModel.RefreshAsync();
            }
        };
    }

    /// <summary>
    /// Opens on a particular tab rather than wherever Settings normally starts. Used by the
    /// inspector's "Manage keywords" and "Manage labels", each of which is a request to edit one
    /// thing — landing elsewhere and leaving the user to find the tab would answer a question they
    /// did not ask.
    ///
    /// Named parts rather than header strings, so renaming a tab cannot quietly break this.
    /// </summary>
    public void ShowTab(SettingsTab tab) => Tabs.SelectedItem = tab switch
    {
        SettingsTab.Keywords => KeywordsTab,
        SettingsTab.Labels => LabelsTab,
        _ => Tabs.SelectedItem
    };

    /// <summary>
    /// Shows what an import would do and waits for an answer. The ViewModels decide what would
    /// happen; only the window can ask.
    /// </summary>
    private async Task<bool> ConfirmImportAsync(ImportPlan plan, string noun)
        => await new ImportPreviewWindow(plan, noun).ShowDialog<bool>(this);

    /// <summary>
    /// Clears the status line when the tab changes.
    ///
    /// It reports what just happened — "Imported 3 new label(s)", "Cleared 1.2 GB" — and that is an
    /// answer about the tab it happened on. Carried to another tab it reads as a report about that
    /// one instead, which is worse than saying nothing.
    /// </summary>
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.StatusMessage = null;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Puts the caret in a keyword that has just been added, with its placeholder name selected.
    ///
    /// Hung off the row appearing rather than done in the command, because the row does not exist
    /// when the command runs — the tree builds it on the next layout pass. Focus is set one pass
    /// later again: a control that has only just joined the tree has not been arranged yet, and
    /// focusing it before it has a position does not take.
    /// </summary>
    private void OnKeywordNameShown(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box ||
            box.DataContext is not KeywordNodeViewModel node ||
            DataContext is not SettingsViewModel viewModel ||
            !viewModel.Keywords.ClaimNameEdit(node))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    /// <summary>
    /// Enter finishes the name, and goes no further.
    ///
    /// A single-line TextBox does not use the key, so it carried on up to the tree, which took it
    /// as a key for the row and shut the group the keyword had just been added to — naming a new
    /// keyword made it disappear from view at the very moment it was named. Nothing else in this
    /// list wants Enter, so the box keeps it, and does on Enter what it does on losing focus.
    /// </summary>
    private void OnKeywordNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            sender is not Control { DataContext: KeywordNodeViewModel node } ||
            DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        viewModel.Keywords.SortSiblingsOf(node);
        e.Handled = true;
    }

    /// <summary>Re-sorts a keyword's level once its name has finished being edited.</summary>
    private void OnKeywordNameCommitted(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: KeywordNodeViewModel node } &&
            DataContext is SettingsViewModel viewModel)
        {
            viewModel.Keywords.SortSiblingsOf(node);
        }
    }

    /// <summary>
    /// The move a click in the "move under" list has chosen, waiting for that click to finish.
    /// </summary>
    private (KeywordNodeViewModel Node, KeywordMoveTarget Target, FlyoutBase? Flyout)? _pendingMove;

    /// <summary>
    /// Records the destination picked in the flyout. The move itself waits for the button to come
    /// back up — see <see cref="OnMoveTargetReleased"/>.
    /// </summary>
    private void OnMoveTargetChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list ||
            list.SelectedItem is not KeywordMoveTarget target ||
            list.DataContext is not KeywordNodeViewModel node)
        {
            return;
        }

        // The flyout is declared as Button.Flyout, so FlyoutBase.GetAttachedFlyout does not find it.
        // Reached through the LOGICAL tree instead, which runs ListBox → FlyoutPresenter → Popup →
        // Button; the visual chain from inside a popup stops at its PopupRoot and never passes the
        // Popup at all, which is why the FindAncestorOfType<Popup> this used to rely on was always
        // null and the close it guarded never once ran.
        _pendingMove = (node, target, list.FindLogicalAncestorOfType<Button>()?.Flyout);

        // Re-enters this handler with a null selection, which falls out at the check above.
        list.SelectedItem = null;
    }

    /// <summary>
    /// Carries out the move once the click that chose it has finished.
    ///
    /// A ListBox selects on pointer <i>press</i>, so doing this from SelectionChanged closed the
    /// flyout half way through a click and the release then landed on a window that had gone. The
    /// symptom was that the wheel stopped reaching this window entirely — measured with a handler
    /// on the window, which logged nothing at all afterwards — so the keyword list would not scroll
    /// until the user clicked it again to end the abandoned gesture. Moving a keyword and then
    /// scrolling to see where it landed is one gesture and should not need a click in the middle.
    /// </summary>
    private void OnMoveTargetReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pendingMove is not { } move || DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        _pendingMove = null;

        move.Flyout?.Hide();
        viewModel.Keywords.Move(move.Node, move.Target);
        KeywordTree.Focus();
    }

    /// <summary>
    /// Flushes the keyword library on the way out. Edits are saved on a short delay, so closing the
    /// window immediately after typing would otherwise lose the last one.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is SettingsViewModel viewModel)
        {
            await viewModel.Keywords.SaveAsync();
        }
    }
}
