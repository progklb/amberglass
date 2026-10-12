using Amberglass.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One editable row of the label library.
///
/// The name is the part that matters: it is what goes into the file and what another application
/// reads. The colour never leaves this machine.
/// </summary>
public sealed partial class LabelRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public LabelRowViewModel(string name, string colour, Action changed)
    {
        _name = name;
        _colour = colour;
        _changed = changed;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _colour;

    /// <summary>Set while the colour is following the name, so that does not count as a choice.</summary>
    private bool _colourFollowsName;

    /// <summary>
    /// A name that says a colour takes it, as it is typed: "Yellow" turns yellow on the "w".
    ///
    /// This is the interoperable case and the one worth helping with. The file stores the word and
    /// never a colour, so matching Lightroom means naming labels Red, Yellow, Green, Blue, Purple —
    /// and having to then pick each colour by hand from a list of near-identical swatches is busy
    /// work the name has already answered.
    ///
    /// <para>Choosing a colour by hand afterwards keeps it. The name only speaks when it changes, so
    /// a deliberate choice survives everything except renaming the label again.</para>
    /// </summary>
    partial void OnNameChanged(string value)
    {
        if (LabelColours.NamesAColour(value) &&
            LabelColours.Resolve(null, value) is { } suggested &&
            !string.Equals(suggested, Colour, StringComparison.OrdinalIgnoreCase))
        {
            _colourFollowsName = true;
            try
            {
                Colour = suggested;
            }
            finally
            {
                _colourFollowsName = false;
            }
        }

        _changed();
    }

    // Not saved twice when the name has just set it: the save below reads the whole row anyway.
    partial void OnColourChanged(string value)
    {
        if (!_colourFollowsName)
        {
            _changed();
        }
    }

    /// <summary>
    /// The presets offered, so a colour can be chosen without typing a hex value.
    ///
    /// The neutral grey is <see cref="LabelColours.Unrecognised"/> rather than a shade of its own.
    /// A new row and an imported label both start on it, and if it were not in this list their
    /// dropdown would open with nothing selected — the control saying it does not recognise a colour
    /// the application had just chosen for it.
    /// </summary>
    public static IReadOnlyList<string> Swatches { get; } =
    [
        "#E8574A", "#E8934A", "#E8C84A", "#6ABF52",
        "#4AA3E8", "#B77BD8", LabelColours.Unrecognised, "#D8D8D8"
    ];
}
