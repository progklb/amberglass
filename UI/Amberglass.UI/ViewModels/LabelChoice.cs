using Amberglass.Core.Services;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One entry in the metadata panel's label dropdown.
/// </summary>
/// <param name="Name">The word written to the file, or empty for "No label".</param>
/// <param name="Colour">How it is drawn, or null when there is nothing to draw.</param>
public sealed record LabelChoice(string Name, string? Colour)
{
    public static readonly LabelChoice None = new(string.Empty, null);

    /// <summary>
    /// A label the file carries that the library does not define — set in another application, or
    /// before the library was edited. Offered so it can be seen and kept rather than replaced.
    ///
    /// Coloured by the same rule the grid uses, so a Lightroom "Yellow" is yellow in both places.
    /// An empty swatch here beside a coloured bar on the tile would look like two different labels.
    /// </summary>
    public static LabelChoice Unknown(string name)
        => new(name, LabelColours.Resolve(null, name));

    public bool IsNone => string.IsNullOrEmpty(Name);

    public string Display => IsNone ? "No label" : Name;

    public bool HasColour => Colour is not null;
}
