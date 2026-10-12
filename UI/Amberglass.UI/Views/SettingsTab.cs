namespace Amberglass.UI.Views;

/// <summary>
/// A tab Settings can be opened straight onto, for the places that ask for one thing in particular.
///
/// Only the tabs something links to. Opening Settings with no tab in mind leaves it wherever it
/// normally starts, which is what the menu item and the keyboard shortcut want.
/// </summary>
public enum SettingsTab
{
    Keywords,
    Labels
}
