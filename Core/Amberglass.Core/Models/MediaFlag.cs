namespace Amberglass.Core.Models;

/// <summary>
/// A cull decision: keep this one, throw it away, or not yet looked at.
///
/// Stored as <c>XMP-digiKam:PickLabel</c>, and the numbers here are that property's, not ours —
/// which is why they are pinned. That tag was chosen over the alternatives after checking what
/// actually exists:
///
/// <list type="bullet">
/// <item>
/// <c>lr:PickStatus</c> is not a tag ExifTool knows. Lightroom keeps its flags in its own catalog
/// rather than in XMP, so there is nothing to write.
/// </item>
/// <item>
/// <c>xmp:Rating = -1</c> is the Adobe convention for rejected, and Bridge honours it — but it
/// occupies the rating, so a rejected photograph could not also be a three-star one, and un-rejecting
/// would have to guess what the rating had been.
/// </item>
/// <item>
/// <c>XMP-digiKam:PickLabel</c> expresses accepted and rejected separately from the rating, and
/// ExifTool writes it natively with no config file. digiKam reads it back.
/// </item>
/// </list>
///
/// The cost is that Bridge and Lightroom will not show these flags. That is a real limitation and
/// not one this application can fix, because the thing to be compatible with does not exist.
/// </summary>
public enum MediaFlag
{
    /// <summary>Not yet judged. digiKam calls this "none".</summary>
    None = 0,

    Rejected = 1,

    /// <summary>digiKam's "pending", which it treats as the middle of three.</summary>
    Pending = 2,

    Accepted = 3
}
