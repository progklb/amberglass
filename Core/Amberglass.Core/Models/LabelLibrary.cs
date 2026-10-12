using System.Collections.Immutable;

namespace Amberglass.Core.Models;

/// <summary>
/// One colour label: the word written to the file, and the colour this application draws it in.
/// </summary>
/// <param name="Name">
/// What actually goes into <c>xmp:Label</c>, and therefore the only part other applications see.
/// </param>
/// <param name="Colour">
/// How Amberglass draws it. Local decoration: no colour is stored in the file, because the XMP label
/// is a string and nothing else.
/// </param>
public sealed record LabelDefinition(string Name, string Colour);

/// <summary>
/// The colour labels this application offers, and what they are called.
///
/// <b>The file stores a name, not a colour.</b> That is the whole difficulty of label compatibility
/// and it is worth stating plainly: <c>xmp:Label</c> is a string, and every application decides for
/// itself which colour to draw a given string in. Adobe Bridge ships "Select, Second, Approved,
/// Review, To Do"; Lightroom ships "Red, Yellow, Green, Blue, Purple". A file labelled in one shows
/// up in the other with the label intact but no colour, because the word does not match anything in
/// its own list. Lightroom offers a "Bridge compatible" label set for exactly this reason.
///
/// So the names are the interoperable part and they are editable here. Matching them to whatever
/// Bridge or Lightroom is set to is what makes labels travel; the colours never travel and are only
/// ever a local convenience.
/// </summary>
public sealed record LabelLibrary
{
    /// <summary>
    /// Adobe Bridge's defaults, names and colours both, because that is the most common thing to be
    /// compatible with. Anyone using Lightroom's defaults can rename these to the colour words.
    /// </summary>
    public static readonly LabelLibrary Default = new()
    {
        Labels =
        [
            new LabelDefinition("Select", "#E8574A"),
            new LabelDefinition("Second", "#E8C84A"),
            new LabelDefinition("Approved", "#6ABF52"),
            new LabelDefinition("Review", "#4AA3E8"),
            new LabelDefinition("To Do", "#B77BD8")
        ]
    };

    public ImmutableArray<LabelDefinition> Labels { get; init; } = [];

    public bool IsEmpty => Labels.IsDefaultOrEmpty;

    /// <summary>
    /// The definition for a label written on a file, or null when it is not one of ours.
    ///
    /// A file may carry any string at all — written by another application, or by this one before
    /// the library was edited. Those are shown as they are rather than discarded, so a label set
    /// elsewhere is never silently dropped.
    /// </summary>
    public LabelDefinition? Find(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : Labels.FirstOrDefault(label => string.Equals(label.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The 1-based slot a label occupies, or 0 when it is not in the library.
    ///
    /// Slots are what the numeric conventions use — digiKam's <c>ColorLabel</c> and Photo Mechanic's
    /// <c>ColorClass</c> are both indices rather than words. They are written alongside the name so
    /// those applications see something, but they are the lesser half: their scales disagree with
    /// each other about which index is which colour, so only the name is dependable.
    /// </summary>
    public int SlotOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        for (var i = 0; i < Labels.Length; i++)
        {
            if (string.Equals(Labels[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    public string? NameInSlot(int slot)
        => slot >= 1 && slot <= Labels.Length ? Labels[slot - 1].Name : null;
}
