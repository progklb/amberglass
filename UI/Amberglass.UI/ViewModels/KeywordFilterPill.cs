using CommunityToolkit.Mvvm.Input;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One keyword shown in the filter panel while the keyword list is shut.
///
/// It carries its own command rather than reaching for the panel's: <c>$parent[ItemsControl]</c>
/// does not resolve inside a Flyout, so every item template in this popup has to be self-contained.
/// </summary>
public sealed partial class KeywordFilterPill(string name, Action<string> remove)
{
    private readonly Action<string> _remove = remove;

    public string Name { get; } = name;

    [RelayCommand]
    private void Remove() => _remove(Name);
}
