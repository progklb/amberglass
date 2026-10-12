using System.Collections.ObjectModel;
using Amberglass.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amberglass.UI.ViewModels;

/// <summary>
/// One keyword in the editable tree.
///
/// A mutable mirror of the immutable <see cref="KeywordNode"/>: the model is a value that can be
/// saved and compared, while editing needs something a TreeView can bind two ways against and mutate
/// in place. The tree is converted back to the model on save.
/// </summary>
public sealed partial class KeywordNodeViewModel : ObservableObject
{
    public KeywordNodeViewModel(string name, KeywordNodeViewModel? parent = null)
    {
        _name = name;
        Parent = parent;
    }

    /// <summary>
    /// Null for a root. Needed so a node can remove itself from the right collection, and reassigned
    /// when it is moved somewhere else.
    /// </summary>
    public KeywordNodeViewModel? Parent { get; set; }

    /// <summary>
    /// True when <paramref name="other"/> is this node or sits beneath it. A node cannot be moved
    /// into its own subtree — the tree would be detached from the roots and simply vanish.
    /// </summary>
    public bool Contains(KeywordNodeViewModel other)
        => ReferenceEquals(this, other) || Children.Any(child => child.Contains(other));

    [ObservableProperty]
    private string _name;

    /// <summary>Expanded while being edited, so a newly added child is visible rather than hidden.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    public ObservableCollection<KeywordNodeViewModel> Children { get; } = [];

    /// <summary>
    /// The editor this node belongs to, set as it is attached to the tree.
    ///
    /// Needed because the move list lives in a flyout, and a flyout's content sits in its own popup
    /// root — a <c>$parent[TreeView]</c> binding cannot walk out of it to find the editor, and
    /// silently resolves to nothing. Reaching the editor through the node works from anywhere.
    /// </summary>
    public KeywordLibraryEditorViewModel? Owner { get; set; }

    /// <summary>Where this keyword could be filed instead. Recomputed each time the flyout opens.</summary>
    public IReadOnlyList<KeywordMoveTarget> MoveTargets => Owner?.GetMoveTargetsFor(this) ?? [];

    public static KeywordNodeViewModel FromModel(KeywordNode node, KeywordNodeViewModel? parent = null)
    {
        var viewModel = new KeywordNodeViewModel(node.Name, parent);

        foreach (var child in node.Children)
        {
            viewModel.Children.Add(FromModel(child, viewModel));
        }

        return viewModel;
    }

    /// <summary>
    /// Back to the model, dropping anything left blank. Empty names come from a row added and then
    /// abandoned, and saving them would fill the library with anonymous entries.
    /// </summary>
    public KeywordNode? ToModel()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return new KeywordNode
        {
            Name = name,
            Children = [.. Children.Select(child => child.ToModel()).OfType<KeywordNode>()]
        };
    }
}
