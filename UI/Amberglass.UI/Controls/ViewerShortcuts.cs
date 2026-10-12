using Avalonia.Input;

namespace Amberglass.UI.Controls;

/// <summary>What a key press in the viewer should do.</summary>
public enum ViewerAction
{
    None,
    Close,
    Fit,
    ActualSize,
    Previous,
    Next,
    ToggleRawDevelopment,
    TogglePlayback,
    ToggleBlackAndWhite
}

/// <summary>
/// The viewer's keyboard map, kept apart from the window so the rules can be stated and tested
/// rather than inferred from a switch buried in an event handler.
/// </summary>
public static class ViewerShortcuts
{
    /// <summary>
    /// Resolves a key press for whatever is on screen.
    ///
    /// The one rule worth spelling out is <b>Space</b>: it does the obvious thing for the medium —
    /// play or pause a video, reset the view of a still. That reads as inconsistent written down and
    /// is the opposite in practice. Space is the universal play/pause; a video that ignored it in
    /// favour of re-fitting a frame that already fits would be the surprising thing, and it is what
    /// the space bar does in the inline preview too. A still has nothing to play, so the key is free
    /// to mean what Lightroom's space bar means there.
    ///
    /// Fit is never only on Space: <c>0</c> and <c>Enter</c> both do it whatever is on screen, and the
    /// Fit button is always on the chrome.
    /// </summary>
    public static ViewerAction Resolve(Key key, bool isVideo) => key switch
    {
        Key.Escape or Key.F => ViewerAction.Close,

        Key.Space => isVideo ? ViewerAction.TogglePlayback : ViewerAction.Fit,

        // Always fitting, whatever the medium — so the gesture exists for video as well, where Space
        // is spoken for.
        Key.D0 or Key.NumPad0 or Key.Enter => ViewerAction.Fit,

        Key.D1 or Key.NumPad1 => ViewerAction.ActualSize,

        Key.Left => ViewerAction.Previous,
        Key.Right => ViewerAction.Next,

        // Backslash flips between the developed RAW and the embedded JPEG, the way Lightroom uses it
        // to compare two renderings. Reported under several names depending on layout.
        Key.OemBackslash or Key.OemPipe or Key.Oem5 => ViewerAction.ToggleRawDevelopment,

        // The video player's own convention, kept so the habit works here too.
        Key.K when isVideo => ViewerAction.TogglePlayback,

        // Black and white, for stills only — greying video would mean converting every frame as it
        // arrives, which is a different job from converting one photograph once.
        Key.B when !isVideo => ViewerAction.ToggleBlackAndWhite,

        _ => ViewerAction.None
    };

    /// <summary>
    /// The hint shown on arrival, which has to describe the keys that actually apply.
    ///
    /// A list of pairs rather than a sentence. As one run of prose it was six instructions with the
    /// keys buried inside them, and at the size and opacity this is drawn at that is a paragraph to
    /// read rather than a strip to glance at. Split, the keys can be set apart from what they do.
    /// </summary>
    public static IReadOnlyList<ShortcutHint> Hint(bool isVideo) => isVideo
        ?
        [
            new("scroll", "zoom"),
            new("drag", "pan"),
            new("space", "play"),
            new("0", "fit"),
            new("← →", "browse"),
            new("esc", "close")
        ]
        :
        [
            new("scroll", "zoom"),
            new("drag", "pan"),
            new("space", "fit"),
            new("← →", "browse"),
            new("\\", "RAW / JPEG"),
            new("b", "b&w"),
            new("esc", "close")
        ];
}

/// <param name="Key">What to press, drawn as a key.</param>
/// <param name="Action">What it does, in as few words as it takes.</param>
public sealed record ShortcutHint(string Key, string Action);
