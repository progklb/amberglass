using System.Collections.ObjectModel;
using Amberglass.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One keyword in the tick list on the metadata panel.
///
/// Separate from the editor's node type on purpose: this one is read-only vocabulary with a tick,
/// where that one is an editable name. Sharing a type would mean one class doing both jobs and every
/// binding having to know which mode it was in.
/// </summary>
public sealed partial class KeywordPickerNodeViewModel : ObservableObject
{
    private readonly Action<KeywordPickerNodeViewModel, bool> _toggled;

    /// <summary>
    /// Set while the tick is being brought in line with the file rather than clicked. Without it,
    /// loading a photograph would look exactly like the user ticking every keyword it already has.
    /// </summary>
    private bool _syncing;

    public KeywordPickerNodeViewModel(string name, Action<KeywordPickerNodeViewModel, bool> toggled)
    {
        Name = name;
        _toggled = toggled;
    }

    public string Name { get; }

    public ObservableCollection<KeywordPickerNodeViewModel> Children { get; } = [];

    public bool HasChildren => Children.Count > 0;

    /// <summary>
    /// Groups start open. The library is a prompt as much as a filter — it is there to remind you
    /// what you decided to tag by, which it cannot do while everything is folded away.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_syncing)
        {
            _toggled(this, value);
        }
    }

    /// <summary>Sets the tick without reporting it as a click.</summary>
    public void SyncChecked(bool value)
    {
        _syncing = true;

        try
        {
            IsChecked = value;
        }
        finally
        {
            _syncing = false;
        }
    }

    public IEnumerable<KeywordPickerNodeViewModel> SelfAndDescendants()
    {
        yield return this;

        foreach (var descendant in Children.SelectMany(child => child.SelfAndDescendants()))
        {
            yield return descendant;
        }
    }

    public static KeywordPickerNodeViewModel FromModel(
        KeywordNode node,
        Action<KeywordPickerNodeViewModel, bool> toggled)
    {
        var viewModel = new KeywordPickerNodeViewModel(node.Name, toggled);

        foreach (var child in node.Children)
        {
            viewModel.Children.Add(FromModel(child, toggled));
        }

        return viewModel;
    }
}
