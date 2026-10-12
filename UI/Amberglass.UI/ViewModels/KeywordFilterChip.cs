using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One keyword in the filter panel's list, with how many files carry it.
///
/// The count is the reason the list is worth having: it says whether a filter will find anything
/// before it is applied, which a bare list of words cannot.
/// </summary>
public sealed partial class KeywordFilterChip : ObservableObject
{
    private readonly Action _toggled;

    public KeywordFilterChip(string name, int count, bool isSelected, Action toggled)
    {
        Name = name;
        Count = count;
        _isSelected = isSelected;
        _toggled = toggled;
    }

    public string Name { get; }

    public int Count { get; }

    public string CountDisplay => Count.ToString("N0");

    private bool _isSelected;

    /// <summary>
    /// Written by hand rather than generated: setting it has to be distinguishable from clicking it,
    /// or reading the query back would look like the user ticking every keyword in turn.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public void SetSelected(bool value) => IsSelected = value;

    [RelayCommand]
    private void Toggle()
    {
        IsSelected = !IsSelected;
        _toggled();
    }
}
