using System.Collections.ObjectModel;
using Amberglass.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One collapsible section of the raw tag list — an ExifTool group such as EXIF, MakerNotes or
/// Sidecar:XMP, holding the tags belonging to it.
/// </summary>
public sealed partial class RawMetadataGroupViewModel : ObservableObject
{
    public const string SidecarPrefix = "Sidecar:";

    public RawMetadataGroupViewModel(string name)
    {
        Name = name;
        IsFromSidecar = name.StartsWith(SidecarPrefix, StringComparison.Ordinal);
        Label = IsFromSidecar ? name[SidecarPrefix.Length..] : name;
    }

    /// <summary>The group as ExifTool and the expanded-sections memory know it, e.g. "Sidecar:XMP".</summary>
    public string Name { get; }

    /// <summary>
    /// The name shown on the section. The "Sidecar:" prefix is dropped because the heading above the
    /// sidecar sections already says where they came from.
    /// </summary>
    public string Label { get; }

    public bool IsFromSidecar { get; }

    /// <summary>
    /// "Sidecar" or "Embedded" on the first section of each half of the list, null on the rest. Set
    /// only when the file has a sidecar: with nothing to tell apart, a lone "Embedded" heading would
    /// just be one more line to read past.
    /// </summary>
    [ObservableProperty]
    private string? _sectionHeading;

    /// <summary>The tags to show, which is fewer than the group holds while a filter is running.</summary>
    public ObservableCollection<RawMetadataTag> Tags { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// How many tags are in here: "68" normally, "3 of 68" when a filter is hiding some. Worth the
    /// extra words — a section that says only "3" looks like a small section rather than a filtered
    /// one, and the difference is what tells you whether to widen the search.
    /// </summary>
    [ObservableProperty]
    private string _summary = string.Empty;
}
