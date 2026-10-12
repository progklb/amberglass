namespace Amberglass.Core.Models;

/// <summary>
/// The editable fields on the metadata panel, so a user can hide the ones they never fill in.
///
/// Everyone uses a different subset. Someone who tags and rates but never writes a headline is paying
/// for three large text boxes with the screen space that would otherwise show their keywords.
/// </summary>
public enum MetadataField
{
    Rating,
    Title,
    Headline,
    Description,
    Keywords,
    Label,
    Creator,
    Copyright
}
