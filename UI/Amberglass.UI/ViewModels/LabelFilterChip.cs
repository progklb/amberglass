using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One clickable label in the filter popup.
///
/// A small ViewModel rather than five bound properties because the labels are user-defined: there
/// may be three of them or eight, and their names change. The chips have to be built from the
/// library rather than written into the markup.
/// </summary>
public sealed partial class LabelFilterChip : ObservableObject
{
    private readonly Action<LabelFilterChip> _toggled;

    public LabelFilterChip(string name, string? colour, Action<LabelFilterChip> toggled)
    {
        Name = name;
        Colour = colour;
        _toggled = toggled;
    }

    public string Name { get; }

    /// <summary>Null for "No label", which is drawn as an outline rather than a colour.</summary>
    public string? Colour { get; }

    public bool HasColour => Colour is not null;

    /// <summary>
    /// What goes into the query. "No label" is written as <c>none</c>, which reads plainly in the
    /// box and cannot collide with a real label unless somebody names one "none".
    /// </summary>
    public string Term => Colour is null ? "none" : Name;

    private bool _isSelected;

    /// <summary>
    /// Written by hand rather than generated: setting it has to be distinguishable from clicking it,
    /// or reading the query back would look like the user toggling every chip in turn.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Sets the state without reporting it as a click, for when the query is read back.</summary>
    public void SetSelected(bool value) => IsSelected = value;

    [RelayCommand]
    private void Toggle()
    {
        IsSelected = !IsSelected;
        _toggled(this);
    }
}
