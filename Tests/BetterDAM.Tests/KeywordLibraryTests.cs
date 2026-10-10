using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using BetterDAM.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterDAM.Tests;

public class KeywordLibraryTests
{
    // ---- Building from flat keywords -------------------------------------------------------------

    [Fact]
    public void Flat_keywords_become_roots()
    {
        var library = KeywordLibrary.FromFlat(["animal", "plant"]);

        Assert.Equal(2, library.Roots.Length);
        Assert.All(library.Roots, root => Assert.False(root.HasChildren));
    }

    /// <summary>
    /// Lightroom writes hierarchical keywords with a pipe. Filing them under the right parent is what
    /// makes importing an existing library produce a structure rather than a flat wall of names.
    /// </summary>
    [Theory]
    [InlineData("Subject|animal")]
    [InlineData("Subject/animal")]
    public void A_path_is_filed_under_its_parent(string keyword)
    {
        var library = KeywordLibrary.FromFlat([keyword]);

        var root = Assert.Single(library.Roots);
        Assert.Equal("Subject", root.Name);

        var child = Assert.Single(root.Children);
        Assert.Equal("animal", child.Name);
    }

    [Fact]
    public void Siblings_share_one_parent()
    {
        var library = KeywordLibrary.FromFlat(["Subject|animal", "Subject|plant", "Mood|warm"]);

        Assert.Equal(2, library.Roots.Length);
        Assert.Equal(2, library.Roots.Single(r => r.Name == "Subject").Children.Length);
        Assert.Single(library.Roots.Single(r => r.Name == "Mood").Children);
    }

    [Fact]
    public void Nesting_goes_as_deep_as_the_path()
    {
        var library = KeywordLibrary.FromFlat(["Subject|animal|mammal|elephant"]);

        Assert.Equal(4, library.Flatten().Count());
        Assert.Equal("elephant", library.Flatten().Last().Name);
    }

    /// <summary>Case differences are the same keyword; a library with both would be a mess to tick.</summary>
    [Fact]
    public void The_same_keyword_in_another_case_is_not_duplicated()
    {
        var library = KeywordLibrary.FromFlat(["Subject|Animal", "subject|animal"]);

        var root = Assert.Single(library.Roots);
        Assert.Single(root.Children);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("||")]
    public void Empty_input_produces_nothing(string keyword)
        => Assert.True(KeywordLibrary.FromFlat([keyword]).IsEmpty);

    [Fact]
    public void Surrounding_space_is_trimmed()
    {
        var library = KeywordLibrary.FromFlat([" Subject | animal "]);

        Assert.Equal("Subject", library.Roots.Single().Name);
        Assert.Equal("animal", library.Roots.Single().Children.Single().Name);
    }

    // ---- Round tripping and merging ---------------------------------------------------------------

    /// <summary>
    /// Parents are emitted as paths in their own right. A group is a keyword too, and dropping it
    /// here would quietly delete it the next time anything merged.
    /// </summary>
    [Fact]
    public void Paths_include_parents_as_well_as_leaves()
    {
        var library = KeywordLibrary.FromFlat(["Subject|animal"]);

        Assert.Equal(["Subject", "Subject|animal"], library.ToPaths().Order().ToArray());
    }

    [Fact]
    public void A_library_survives_a_round_trip_through_paths()
    {
        var original = KeywordLibrary.FromFlat(["Subject|animal", "Subject|plant", "Mood|warm", "loose"]);

        var rebuilt = KeywordLibrary.FromFlat(original.ToPaths());

        Assert.Equal(original.ToPaths().Order(), rebuilt.ToPaths().Order());
    }

    /// <summary>
    /// The point of merging: someone who has arranged their groups by hand and then imports should
    /// gain what they were missing, not have their arrangement flattened.
    /// </summary>
    [Fact]
    public void Merging_keeps_the_existing_arrangement()
    {
        var existing = KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]);

        var merged = existing.MergedWith(["Subject|plant", "golden hour"]);

        Assert.Equal(2, merged.Roots.Single(r => r.Name == "Subject").Children.Length);
        Assert.Single(merged.Roots.Single(r => r.Name == "Mood").Children);
        Assert.Contains(merged.Roots, r => r.Name == "golden hour");
    }

    [Fact]
    public void Merging_the_same_keywords_changes_nothing()
    {
        var existing = KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]);

        var merged = existing.MergedWith(["Subject|animal"]);

        Assert.Equal(existing.ToPaths().Order(), merged.ToPaths().Order());
    }

    /// <summary>A flat keyword matching an existing group must not become a second copy of it.</summary>
    [Fact]
    public void Merging_a_flat_name_that_already_exists_as_a_group_does_not_duplicate_it()
    {
        var existing = KeywordLibrary.FromFlat(["Subject|animal"]);

        var merged = existing.MergedWith(["Subject"]);

        Assert.Single(merged.Roots);
        Assert.Single(merged.Roots.Single().Children);
    }

    // ---- Lookups ----------------------------------------------------------------------------------

    [Fact]
    public void All_names_covers_every_level()
    {
        var names = KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]).AllNames();

        Assert.Equal(4, names.Count);
        Assert.Contains("Subject", names);
        Assert.Contains("animal", names);
    }

    [Fact]
    public void All_names_ignores_case_when_asked()
        => Assert.Contains("ANIMAL", KeywordLibrary.FromFlat(["animal"]).AllNames());

    [Fact]
    public void An_empty_library_reports_itself_as_empty()
    {
        Assert.True(KeywordLibrary.Empty.IsEmpty);
        Assert.Equal(0, KeywordLibrary.Empty.Count);
        Assert.Empty(KeywordLibrary.Empty.ToPaths());
    }

    [Fact]
    public void Count_includes_every_level()
        => Assert.Equal(4, KeywordLibrary.FromFlat(["Subject|animal", "Mood|warm"]).Count);
}

/// <summary>
/// The contract that the grouping never reaches the files: a keyword is written as its own bare name,
/// so what is tagged stays readable by Bridge, Lightroom or exiftool.
/// </summary>
public class KeywordIdentityTests
{
    [Fact]
    public void A_keyword_is_its_own_name_regardless_of_where_it_is_filed()
    {
        var library = KeywordLibrary.FromFlat(["Shot type|wide", "Subject|animal"]);

        var names = library.AllNames();

        Assert.Contains("wide", names);
        Assert.Contains("animal", names);

        // Never the path.
        Assert.DoesNotContain("Shot type|wide", names);
    }

    /// <summary>
    /// The same word in two groups is one keyword. Files carry names, so there is nothing to tell the
    /// two apart — the picker must tick both, and this pins the fact it follows from.
    /// </summary>
    [Fact]
    public void The_same_name_in_two_groups_is_one_keyword()
    {
        var library = KeywordLibrary.FromFlat(["Shot type|wide", "Landscape|wide"]);

        Assert.Equal(2, library.Roots.Length);
        Assert.Equal(2, library.Flatten().Count(node => node.Name == "wide"));

        // One keyword, though, because names are the identity.
        Assert.Single(library.AllNames().Where(name => name == "wide"));
    }

    [Fact]
    public void Groups_are_offered_as_keywords_too()
    {
        var names = KeywordLibrary.FromFlat(["Mood|warm"]).AllNames();

        Assert.Contains("Mood", names);
        Assert.Contains("warm", names);
    }
}

/// <summary>Refiling a keyword under another one, which is how the tree is arranged.</summary>
public class KeywordMoveTests
{
    private static KeywordLibraryEditorViewModel Editor(params string[] keywords)
    {
        var service = new StubLibraryService(KeywordLibrary.FromFlat(keywords));
        return new KeywordLibraryEditorViewModel(service, new StubCatalog(), NullLogger<KeywordLibraryEditorViewModel>.Instance);
    }

    private static KeywordNodeViewModel Find(KeywordLibraryEditorViewModel editor, string name)
        => Flatten(editor.Roots).First(node => node.Name == name);

    private static IEnumerable<KeywordNodeViewModel> Flatten(IEnumerable<KeywordNodeViewModel> nodes)
        => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    [Fact]
    public void A_root_can_be_filed_under_another_root()
    {
        var editor = Editor("wide", "Shot type");
        editor.Selected = Find(editor, "wide");

        var target = editor.MoveTargets.Single(t => t.Label.Trim() == "Shot type");
        editor.MoveSelectedCommand.Execute(target);

        Assert.Single(editor.Roots);
        Assert.Equal("wide", editor.Roots.Single().Children.Single().Name);
    }

    [Fact]
    public void A_child_can_be_promoted_to_the_top_level()
    {
        var editor = Editor("Shot type|wide");
        editor.Selected = Find(editor, "wide");

        editor.MoveSelectedCommand.Execute(editor.MoveTargets.Single(t => t.Label == "Top level"));

        Assert.Equal(2, editor.Roots.Count);
        Assert.Empty(Find(editor, "Shot type").Children);
    }

    /// <summary>
    /// The move that would lose the subtree: filing a group under one of its own children detaches it
    /// from the roots entirely. It must not even be offered.
    /// </summary>
    [Fact]
    public void A_keyword_is_never_offered_its_own_subtree()
    {
        var editor = Editor("Subject|animal|mammal");
        editor.Selected = Find(editor, "Subject");

        var labels = editor.MoveTargets.Select(t => t.Label.Trim()).ToList();

        Assert.DoesNotContain("Subject", labels);
        Assert.DoesNotContain("animal", labels);
        Assert.DoesNotContain("mammal", labels);
    }

    /// <summary>
    /// The parent it is already under is listed but cannot be picked.
    ///
    /// It used to be left out, which took a row out of the middle of an indented tree: moving
    /// Mood/SlowMo offered "Top level" and then an indented Blue Hour, Chaos and Harsh with no Mood
    /// above them, reading as though those had lost their group. Showing it keeps the shape of the
    /// tree; disabling it keeps the move that would do nothing out of reach.
    /// </summary>
    [Fact]
    public void The_current_parent_is_shown_but_cannot_be_chosen()
    {
        var editor = Editor("Shot type|wide", "Mood|warm");
        editor.Selected = Find(editor, "wide");

        var current = editor.MoveTargets.Single(t => t.Label.Trim() == "Shot type");

        Assert.False(current.CanChoose);
        Assert.True(editor.MoveTargets.Single(t => t.Label.Trim() == "Mood").CanChoose);
        Assert.True(editor.MoveTargets.Single(t => t.Label == "Top level").CanChoose);
    }

    /// <summary>The row that made the list read wrongly: children under a parent that was missing.</summary>
    [Fact]
    public void A_sibling_is_still_offered_and_its_parent_sits_above_it()
    {
        var editor = Editor("Mood|SlowMo", "Mood|Harsh");
        editor.Selected = Find(editor, "SlowMo");

        var labels = editor.MoveTargets.Select(t => t.Label).ToList();

        // Mood immediately precedes its remaining child, and is the shallower of the two.
        var mood = labels.FindIndex(l => l.Trim() == "Mood");
        var harsh = labels.FindIndex(l => l.Trim() == "Harsh");

        Assert.True(mood >= 0 && harsh == mood + 1);
        Assert.True(labels[harsh].Length - labels[harsh].TrimStart().Length
                    > labels[mood].Length - labels[mood].TrimStart().Length);
    }

    /// <summary>Selecting it anyway changes nothing — the list is not the only guard.</summary>
    [Fact]
    public void Moving_to_the_current_parent_does_nothing()
    {
        var editor = Editor("Shot type|wide", "Mood|warm");
        var node = Find(editor, "wide");
        editor.Selected = node;

        editor.MoveSelectedCommand.Execute(
            editor.MoveTargets.Single(t => t.Label.Trim() == "Shot type"));

        Assert.Equal("Shot type", node.Parent?.Name);
        Assert.Equal(["wide"], Find(editor, "Shot type").Children.Select(c => c.Name).ToArray());
    }

    /// <summary>A parent that is only listed for reading is not somewhere to go.</summary>
    [Fact]
    public void A_keyword_with_only_its_own_parent_listed_cannot_be_moved()
    {
        var editor = Editor("Mood|warm");
        editor.Selected = Find(editor, "warm");

        // "Top level" is a real destination, so this one can move.
        Assert.True(editor.CanMoveSelected);
        Assert.Contains(editor.MoveTargets, t => !t.CanChoose);
    }

    [Fact]
    public void A_lone_root_has_nowhere_to_go()
    {
        var editor = Editor("wide");
        editor.Selected = Find(editor, "wide");

        Assert.Empty(editor.MoveTargets);
        Assert.False(editor.CanMoveSelected);
    }

    [Fact]
    public void Moving_takes_the_children_with_it()
    {
        var editor = Editor("Subject|animal|mammal", "Mood");
        editor.Selected = Find(editor, "animal");

        editor.MoveSelectedCommand.Execute(editor.MoveTargets.Single(t => t.Label.Trim() == "Mood"));

        var moved = Find(editor, "Mood").Children.Single();
        Assert.Equal("animal", moved.Name);
        Assert.Equal("mammal", moved.Children.Single().Name);
    }

    private sealed class StubLibraryService(KeywordLibrary library) : IKeywordLibraryService
    {
        public KeywordLibrary Current { get; private set; } = library;

        public event EventHandler<KeywordLibrary>? Changed { add { } remove { } }

        public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
        {
            Current = library;
            return Task.CompletedTask;
        }
    }

    private sealed class StubCatalog : ICatalog
    {
        public Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CatalogStatistics.Empty);

        public Task<IReadOnlyDictionary<string, MediaMarks>> GetMarksAsync(string? rootPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, MediaMarks>>(new Dictionary<string, MediaMarks>());

        public Task<IReadOnlyList<LabelUsage>> GetLabelsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LabelUsage>>([]);

    public Task<IReadOnlyList<KeywordUsage>> GetKeywordsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<KeywordUsage>>([]);

        public Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, IndexedStamp>> GetIndexedStampsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, IndexedStamp>>(new Dictionary<string, IndexedStamp>());

        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, string? rootPath = null, int limit = 5000, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchHit>>([]);

        public Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>Alphabetical order, at every level and after every kind of change.</summary>
public class KeywordSortingTests
{
    private static KeywordLibraryEditorViewModel Editor(params string[] keywords)
        => new(
            new StubLibrary(KeywordLibrary.FromFlat(keywords)),
            new StubKeywordCatalog(),
            NullLogger<KeywordLibraryEditorViewModel>.Instance);

    private static KeywordNodeViewModel Find(KeywordLibraryEditorViewModel editor, string name)
        => Flatten(editor.Roots).First(node => node.Name == name);

    private static IEnumerable<KeywordNodeViewModel> Flatten(IEnumerable<KeywordNodeViewModel> nodes)
        => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    /// <summary>
    /// Import order is catalog usage counts, which reads as no order at all. Roots were the one level
    /// that was never sorted.
    /// </summary>
    [Fact]
    public void Roots_are_alphabetical_however_they_arrived()
    {
        var library = KeywordLibrary.FromFlat(["Wide", "Bush", "Calm"]);

        Assert.Equal(["Bush", "Calm", "Wide"], library.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Children_are_alphabetical_too()
    {
        var library = KeywordLibrary.FromFlat(["Shot type|wide", "Shot type|close", "Shot type|medium"]);

        Assert.Equal(
            ["close", "medium", "wide"],
            library.Roots.Single().Children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Order_ignores_case()
    {
        var library = KeywordLibrary.FromFlat(["banana", "Apple", "cherry"]);

        Assert.Equal(["Apple", "banana", "cherry"], library.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_loaded_library_is_sorted_even_if_it_was_stored_out_of_order()
    {
        var editor = Editor("Wide", "Bush");

        Assert.Equal(["Bush", "Wide"], editor.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void A_moved_keyword_lands_in_order()
    {
        var editor = Editor("Shot type|close", "Shot type|wide", "medium");
        editor.Selected = Find(editor, "medium");

        editor.MoveSelectedCommand.Execute(
            editor.MoveTargets.Single(t => t.Label.Trim() == "Shot type"));

        Assert.Equal(
            ["close", "medium", "wide"],
            Find(editor, "Shot type").Children.Select(c => c.Name).ToArray());
    }

    /// <summary>
    /// Sorting is entirely Move operations. If those were treated like ordinary adds and removals the
    /// node would be attached and then immediately detached, and every later edit to it would go
    /// unnoticed — including, silently, never being saved.
    /// </summary>
    [Fact]
    public void Sorting_leaves_the_tree_still_listening()
    {
        var editor = Editor("Wide", "Bush");

        var node = Find(editor, "Wide");

        Assert.NotNull(node.Owner);
        Assert.Same(editor, node.Owner);
    }

    [Fact]
    public void Renaming_then_committing_reorders_the_level()
    {
        var editor = Editor("Bush", "Calm", "Wide");

        var node = Find(editor, "Wide");
        node.Name = "Aardvark";
        editor.SortSiblingsOf(node);

        Assert.Equal(["Aardvark", "Bush", "Calm"], editor.Roots.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Move_targets_are_offered_in_order()
    {
        var editor = Editor("Wide", "Bush", "Calm");
        editor.Selected = Find(editor, "Wide");

        Assert.Equal(
            ["Bush", "Calm"],
            editor.MoveTargets.Select(t => t.Label.Trim()).ToArray());
    }
}

internal sealed class StubLibrary(KeywordLibrary library) : IKeywordLibraryService
{
    public KeywordLibrary Current { get; private set; } = library;

    public event EventHandler<KeywordLibrary>? Changed { add { } remove { } }

    public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
    {
        Current = library;
        return Task.CompletedTask;
    }
}

internal sealed class StubKeywordCatalog : ICatalog
{
    public Task<CatalogStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CatalogStatistics.Empty);

    public Task<IReadOnlyDictionary<string, MediaMarks>> GetMarksAsync(string? rootPath = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, MediaMarks>>(new Dictionary<string, MediaMarks>());

    public Task<IReadOnlyList<LabelUsage>> GetLabelsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
    => Task.FromResult<IReadOnlyList<LabelUsage>>([]);

public Task<IReadOnlyList<KeywordUsage>> GetKeywordsAsync(string? rootPath = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<KeywordUsage>>([]);

    public Task UpsertAsync(IReadOnlyList<CatalogEntry> entries, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, IndexedStamp>> GetIndexedStampsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, IndexedStamp>>(new Dictionary<string, IndexedStamp>());

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, string? rootPath = null, int limit = 5000, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SearchHit>>([]);

    public Task<int> RemoveMissingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Adding a keyword, which is always the first half of naming one.
/// </summary>
public class KeywordAddingTests
{
    private static KeywordLibraryEditorViewModel Editor(params string[] keywords)
        => new(
            new StubLibrary(KeywordLibrary.FromFlat(keywords)),
            new StubKeywordCatalog(),
            NullLogger<KeywordLibraryEditorViewModel>.Instance);

    [Fact]
    public void A_new_group_is_waiting_for_its_name()
    {
        var editor = Editor("Mood");

        editor.AddGroupCommand.Execute(null);

        var added = editor.Roots.Single(root => root.Name == "New group");
        Assert.Same(added, editor.AwaitingName);
    }

    [Fact]
    public void A_new_keyword_is_waiting_for_its_name_too()
    {
        var editor = Editor("Mood");
        var mood = editor.Roots.Single();

        editor.AddChildCommand.Execute(mood);

        Assert.Same(mood.Children.Single(), editor.AwaitingName);
    }

    /// <summary>
    /// Claimed once and only once. A row is realised again whenever it is scrolled away and back or
    /// re-sorted, and a second claim would take the caret away from wherever it has got to by then.
    /// </summary>
    [Fact]
    public void The_name_edit_is_claimed_once()
    {
        var editor = Editor();
        editor.AddGroupCommand.Execute(null);

        var added = editor.Roots.Single();

        Assert.True(editor.ClaimNameEdit(added));
        Assert.False(editor.ClaimNameEdit(added));
        Assert.Null(editor.AwaitingName);
    }

    [Fact]
    public void Another_keyword_cannot_claim_it()
    {
        var editor = Editor("Mood");
        editor.AddGroupCommand.Execute(null);

        Assert.False(editor.ClaimNameEdit(editor.Roots.Single(root => root.Name == "Mood")));
        Assert.NotNull(editor.AwaitingName);
    }

    /// <summary>Nothing is waiting until something is added — the tree must not steal the caret.</summary>
    [Fact]
    public void A_loaded_library_is_waiting_for_nothing()
        => Assert.Null(Editor("Mood|warm", "Subject|animal").AwaitingName);
}

/// <summary>
/// Which metadata fields the panel shows. Everyone uses a different subset, and someone who never
/// writes a headline is paying for three large text boxes with the space their keywords would use.
/// </summary>
public class MetadataFieldVisibilityTests
{
    [Fact]
    public void Everything_is_visible_by_default()
    {
        var settings = AppSettings.Default;

        Assert.Empty(settings.HiddenMetadataFields);
        Assert.All(Enum.GetValues<MetadataField>(), field => Assert.True(settings.IsFieldVisible(field)));
    }

    [Fact]
    public void A_hidden_field_reports_itself_hidden()
    {
        var settings = AppSettings.Default with { HiddenMetadataFields = [MetadataField.Headline] };

        Assert.False(settings.IsFieldVisible(MetadataField.Headline));
        Assert.True(settings.IsFieldVisible(MetadataField.Title));
    }

    /// <summary>
    /// The reason this stores what to hide rather than what to show. A field added in a later version
    /// is absent from everyone's hidden list and so appears for everyone; an allow-list would hide
    /// every new field from every existing user, silently.
    /// </summary>
    [Fact]
    public void A_field_nobody_has_heard_of_is_visible()
    {
        var settings = AppSettings.Default with
        {
            HiddenMetadataFields = [MetadataField.Title, MetadataField.Headline]
        };

        // Stands in for a field introduced after these settings were written.
        Assert.True(settings.IsFieldVisible(MetadataField.Copyright));
    }

    [Fact]
    public void Hiding_several_fields_leaves_the_rest_alone()
    {
        var settings = AppSettings.Default with
        {
            HiddenMetadataFields = [MetadataField.Title, MetadataField.Headline, MetadataField.Description]
        };

        Assert.False(settings.IsFieldVisible(MetadataField.Description));
        Assert.True(settings.IsFieldVisible(MetadataField.Keywords));
        Assert.True(settings.IsFieldVisible(MetadataField.Rating));
    }

    /// <summary>Hiding a field is a display choice; nothing about it reaches the files.</summary>
    [Fact]
    public void Hiding_a_field_changes_nothing_else()
    {
        var before = AppSettings.Default;
        var after = before with { HiddenMetadataFields = [MetadataField.Title] };

        Assert.Equal(before.RestrictKeywordsToLibrary, after.RestrictKeywordsToLibrary);
        Assert.Equal(before.DevelopRawFiles, after.DevelopRawFiles);
        Assert.Equal(before.RenderCacheEnabled, after.RenderCacheEnabled);
    }
}

/// <summary>
/// The order of the filter panel's keyword checklist.
///
/// Alphabetical, not by count: at this list you are hunting for a particular word, and how many
/// files carry it says nothing about where to look for it.
/// </summary>
public class KeywordFilterListTests
{
    private static KeywordUsage[] ByUsage(params (string Value, int Count)[] keywords)
        => keywords.Select(k => new KeywordUsage(k.Value, k.Count)).ToArray();

    [Fact]
    public void Keywords_are_arranged_alphabetically_not_by_count()
    {
        var arranged = KeywordFilterList.Arrange(
            ByUsage(("Bush", 238), ("Calm", 93), ("Animal", 4)), cap: 100);

        Assert.Equal(["Animal", "Bush", "Calm"], arranged.Select(k => k.Value).ToArray());
    }

    [Fact]
    public void The_counts_travel_with_the_keywords()
    {
        var arranged = KeywordFilterList.Arrange(ByUsage(("Bush", 238), ("Animal", 4)), cap: 100);

        Assert.Equal(4, arranged[0].Count);
        Assert.Equal(238, arranged[1].Count);
    }

    [Fact]
    public void Sorting_ignores_case()
    {
        var arranged = KeywordFilterList.Arrange(
            ByUsage(("bush", 1), ("Animal", 1), ("calm", 1)), cap: 100);

        Assert.Equal(["Animal", "bush", "calm"], arranged.Select(k => k.Value).ToArray());
    }

    /// <summary>
    /// The cap runs first, while the input is still most-used first, so it keeps the ones worth
    /// showing. Sorted first, it would have meant "the first N alphabetically" and dropped the end
    /// of the alphabet — a Zebra on a thousand files would simply never appear.
    /// </summary>
    [Fact]
    public void The_cap_keeps_the_most_used_not_the_alphabetically_first()
    {
        var arranged = KeywordFilterList.Arrange(
            ByUsage(("Zebra", 900), ("Yak", 800), ("Ant", 1)), cap: 2);

        Assert.Equal(["Yak", "Zebra"], arranged.Select(k => k.Value).ToArray());
        Assert.DoesNotContain("Ant", arranged.Select(k => k.Value));
    }

    [Fact]
    public void An_empty_list_arranges_to_nothing()
        => Assert.Empty(KeywordFilterList.Arrange([], cap: 100));
}

/// <summary>
/// Importing the labels a workspace already uses.
///
/// xmp:Label stores a word and not a colour, so a workspace labelled elsewhere arrives with names
/// the library has never heard of. This is what gets them into it.
/// </summary>
public class LabelImportTests
{
    private static readonly LabelLibrary Bridge = LabelLibrary.Default;

    [Fact]
    public void A_label_the_library_does_not_have_is_added()
    {
        var merged = LabelImport.Merge(Bridge, ["Yellow"]);

        Assert.Equal("Yellow", merged.Labels[^1].Name);
        Assert.Equal(Bridge.Labels.Length + 1, merged.Labels.Length);
    }

    [Fact]
    public void One_already_in_the_library_is_not_added_twice()
    {
        var merged = LabelImport.Merge(Bridge, ["Select"]);

        Assert.Equal(Bridge.Labels.Length, merged.Labels.Length);
    }

    [Fact]
    public void Matching_an_existing_label_ignores_case()
    {
        var merged = LabelImport.Merge(Bridge, ["select", "SECOND"]);

        Assert.Equal(Bridge.Labels.Length, merged.Labels.Length);
    }

    [Fact]
    public void The_same_new_label_twice_is_added_once()
    {
        var merged = LabelImport.Merge(Bridge, ["Yellow", "yellow", " Yellow "]);

        Assert.Single(merged.Labels.Where(l => string.Equals(l.Name, "Yellow", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Blank_labels_are_ignored()
    {
        var merged = LabelImport.Merge(Bridge, ["", "   "]);

        Assert.Equal(Bridge.Labels.Length, merged.Labels.Length);
    }

    [Fact]
    public void Names_are_trimmed()
    {
        var merged = LabelImport.Merge(Bridge, ["  Yellow  "]);

        Assert.Equal("Yellow", merged.Labels[^1].Name);
    }

    /// <summary>
    /// A label's position is its slot, and the slot is the number digiKam and Photo Mechanic write.
    /// Inserting above an existing label would change what those numbers mean for every file already
    /// labelled, so imports can only ever go on the end.
    /// </summary>
    [Fact]
    public void Existing_labels_keep_their_slots()
    {
        var merged = LabelImport.Merge(Bridge, ["Aardvark", "Yellow"]);

        Assert.Equal(
            Bridge.Labels.Select(l => l.Name),
            merged.Labels.Take(Bridge.Labels.Length).Select(l => l.Name));

        foreach (var label in Bridge.Labels)
        {
            Assert.Equal(Bridge.SlotOf(label.Name), merged.SlotOf(label.Name));
        }
    }

    /// <summary>The catalog hands these over most-used first, so the commonest gets the first free slot.</summary>
    [Fact]
    public void Incoming_order_is_kept()
    {
        var merged = LabelImport.Merge(Bridge, ["Zebra", "Aardvark"]);

        Assert.Equal(["Zebra", "Aardvark"], merged.Labels.Skip(Bridge.Labels.Length).Select(l => l.Name));
    }

    [Fact]
    public void A_label_that_names_a_colour_arrives_in_that_colour()
    {
        var merged = LabelImport.Merge(Bridge, ["Yellow"]);

        Assert.Equal(LabelColours.Resolve(null, "Yellow"), merged.Labels[^1].Colour);
        Assert.NotEqual(LabelColours.Unrecognised, merged.Labels[^1].Colour);
    }

    [Fact]
    public void One_that_names_no_colour_arrives_grey()
    {
        var merged = LabelImport.Merge(Bridge, ["Portfolio"]);

        Assert.Equal(LabelColours.Unrecognised, merged.Labels[^1].Colour);
    }

    /// <summary>Nothing to add means the library is left exactly as it was.</summary>
    [Fact]
    public void Importing_nothing_new_changes_nothing()
    {
        var merged = LabelImport.Merge(Bridge, ["Select", "Approved"]);

        Assert.Same(Bridge, merged);
    }

    [Fact]
    public void An_empty_library_takes_everything()
    {
        var merged = LabelImport.Merge(new LabelLibrary(), ["Red", "Green"]);

        Assert.Equal(["Red", "Green"], merged.Labels.Select(l => l.Name));
    }
}

/// <summary>
/// Deciding what an import would add, before it adds it.
/// </summary>
public class KeywordImportTests
{
    private static KeywordLibrary Arranged()
        => KeywordLibrary.FromFlat(["Subject|Bush", "Subject|Sand", "Mood|Calm"]);

    /// <summary>
    /// The bug this fixes. This application writes leaf names to files — ticking "Bush" under
    /// "Subject" writes "Bush", never "Subject|Bush" — so the catalog hands it back flat. Matching on
    /// the whole path found nothing and filed a second, top-level "Bush" beside the one already under
    /// Subject, so importing twice built a flat shadow of an arranged vocabulary.
    /// </summary>
    [Fact]
    public void A_keyword_already_filed_under_a_group_is_not_offered_again()
    {
        var plan = KeywordImport.Plan(Arranged(), ["Bush"]);

        Assert.Empty(plan.ToAdd);
        Assert.Equal(["Bush"], plan.AlreadyKnown.ToArray());
    }

    [Fact]
    public void Applying_that_plan_leaves_the_arrangement_alone()
    {
        var library = Arranged();

        var merged = KeywordImport.Apply(library, KeywordImport.Plan(library, ["Bush", "Sand", "Calm"]));

        Assert.Equal(library.Count, merged.Count);
        Assert.DoesNotContain(merged.Roots, root => string.Equals(root.Name, "Bush", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_keyword_the_library_has_never_seen_is_offered()
    {
        var plan = KeywordImport.Plan(Arranged(), ["Zebra"]);

        Assert.Equal(["Zebra"], plan.ToAdd.ToArray());
        Assert.Empty(plan.AlreadyKnown);
    }

    [Fact]
    public void Matching_ignores_case_and_surrounding_space()
    {
        var plan = KeywordImport.Plan(Arranged(), ["  bUSh  "]);

        Assert.Empty(plan.ToAdd);
    }

    /// <summary>A group is a keyword too, so importing its name must not duplicate it.</summary>
    [Fact]
    public void A_group_name_counts_as_known()
    {
        var plan = KeywordImport.Plan(Arranged(), ["Subject"]);

        Assert.Empty(plan.ToAdd);
        Assert.Equal(["Subject"], plan.AlreadyKnown.ToArray());
    }

    /// <summary>
    /// Matched on the leaf wherever it sits: where a keyword is filed is the user's arrangement, and
    /// an import has no business second-guessing it.
    /// </summary>
    [Fact]
    public void A_hierarchical_keyword_whose_leaf_is_known_is_not_refiled()
    {
        var plan = KeywordImport.Plan(Arranged(), ["Mood|Bush"]);

        Assert.Empty(plan.ToAdd);
    }

    /// <summary>But a genuinely new one keeps its path, so another tool's grouping is honoured.</summary>
    [Fact]
    public void A_new_hierarchical_keyword_keeps_its_path()
    {
        var library = Arranged();
        var plan = KeywordImport.Plan(library, ["Subject|Zebra"]);

        Assert.Equal(["Subject|Zebra"], plan.ToAdd.ToArray());

        var merged = KeywordImport.Apply(library, plan);
        var subject = merged.Roots.Single(r => r.Name == "Subject");

        Assert.Contains(subject.Children, child => child.Name == "Zebra");
        Assert.DoesNotContain(merged.Roots, root => root.Name == "Zebra");
    }

    [Fact]
    public void The_same_keyword_twice_is_offered_once()
    {
        var plan = KeywordImport.Plan(Arranged(), ["Zebra", "zebra"]);

        Assert.Equal(["Zebra"], plan.ToAdd.ToArray());
    }

    [Fact]
    public void Blank_keywords_are_ignored()
    {
        var plan = KeywordImport.Plan(Arranged(), ["", "   ", "|"]);

        Assert.Empty(plan.ToAdd);
        Assert.Empty(plan.AlreadyKnown);
    }

    [Fact]
    public void The_plan_counts_everything_it_considered()
    {
        var plan = KeywordImport.Plan(Arranged(), ["Bush", "Sand", "Zebra"]);

        Assert.Equal(3, plan.Considered);
        Assert.Equal(1, plan.ToAdd.Length);
        Assert.Equal(2, plan.AlreadyKnown.Length);
    }

    [Fact]
    public void Applying_an_empty_plan_returns_the_same_library()
    {
        var library = Arranged();

        Assert.Same(library, KeywordImport.Apply(library, ImportPlan.Empty));
    }

    [Theory]
    [InlineData("Bush", "Bush")]
    [InlineData("Subject|Bush", "Bush")]
    [InlineData("Subject/Bush", "Bush")]
    [InlineData("A|B|C", "C")]
    public void The_leaf_is_the_last_segment(string keyword, string expected)
        => Assert.Equal(expected, KeywordImport.LeafOf(keyword));
}

public class LabelImportPlanTests
{
    [Fact]
    public void The_plan_separates_the_new_from_the_known()
    {
        var plan = LabelImport.Plan(LabelLibrary.Default, ["Select", "Yellow"]);

        Assert.Equal(["Yellow"], plan.ToAdd.ToArray());
        Assert.Equal(["Select"], plan.AlreadyKnown.ToArray());
        Assert.Equal(2, plan.Considered);
    }

    [Fact]
    public void Applying_the_plan_matches_merging_directly()
    {
        var found = (string[])["Select", "Yellow", "Portfolio"];

        var viaPlan = LabelImport.Apply(LabelLibrary.Default, LabelImport.Plan(LabelLibrary.Default, found));
        var direct = LabelImport.Merge(LabelLibrary.Default, found);

        Assert.Equal(direct.Labels.Select(l => l.Name), viaPlan.Labels.Select(l => l.Name));
    }
}

public class LabelSwatchTests
{
    /// <summary>
    /// A new row and an imported label both start on the neutral grey, so it has to be one of the
    /// presets — otherwise their dropdown opens with nothing selected, the control claiming not to
    /// recognise a colour the application chose for it a moment earlier.
    /// </summary>
    [Fact]
    public void The_neutral_grey_is_one_of_the_offered_swatches()
        => Assert.Contains(LabelColours.Unrecognised, LabelRowViewModel.Swatches);

    [Fact]
    public void An_imported_label_with_no_colour_word_lands_on_an_offered_swatch()
    {
        var merged = LabelImport.Merge(LabelLibrary.Default, ["Portfolio"]);

        Assert.Contains(merged.Labels[^1].Colour, LabelRowViewModel.Swatches);
    }

    [Fact]
    public void Every_swatch_is_a_hex_colour()
        => Assert.All(LabelRowViewModel.Swatches, swatch =>
        {
            Assert.StartsWith("#", swatch);
            Assert.Equal(7, swatch.Length);
        });
}

/// <summary>
/// The label row's colour following its name.
///
/// The file stores the word and never a colour, so matching Lightroom means naming labels Red,
/// Yellow, Green, Blue, Purple — and picking each colour by hand from a list of near-identical
/// swatches is busy work the name has already answered.
/// </summary>
public class LabelRowColourTests
{
    private static LabelRowViewModel Row(string name = "", string? colour = null, Action? changed = null)
        => new(name, colour ?? LabelColours.Unrecognised, changed ?? (() => { }));

    [Fact]
    public void A_name_that_says_a_colour_takes_it()
    {
        var row = Row();

        row.Name = "Yellow";

        Assert.Equal(LabelColours.Resolve(null, "Yellow"), row.Colour);
        Assert.NotEqual(LabelColours.Unrecognised, row.Colour);
    }

    [Fact]
    public void It_matches_whatever_the_casing()
    {
        var row = Row();

        row.Name = "pUrPlE";

        Assert.Equal(LabelColours.Resolve(null, "Purple"), row.Colour);
    }

    [Fact]
    public void Surrounding_space_does_not_stop_it()
    {
        var row = Row();

        row.Name = "  Green  ";

        Assert.Equal(LabelColours.Resolve(null, "Green"), row.Colour);
    }

    /// <summary>Most names are not colours, and those must leave the colour alone.</summary>
    [Fact]
    public void A_name_that_says_no_colour_leaves_the_colour_alone()
    {
        var row = Row(colour: "#E8574A");

        row.Name = "Portfolio";

        Assert.Equal("#E8574A", row.Colour);
    }

    /// <summary>
    /// The name only speaks when it changes, so a colour picked by hand survives everything short of
    /// renaming the label again.
    /// </summary>
    [Fact]
    public void A_colour_chosen_by_hand_is_kept()
    {
        var row = Row();
        row.Name = "Red";

        row.Colour = "#4AA3E8";

        Assert.Equal("#4AA3E8", row.Colour);
    }

    [Fact]
    public void Renaming_again_lets_the_name_speak_again()
    {
        var row = Row();
        row.Name = "Red";
        row.Colour = "#4AA3E8";

        row.Name = "Green";

        Assert.Equal(LabelColours.Resolve(null, "Green"), row.Colour);
    }

    /// <summary>
    /// One save per keystroke, not two. The colour change the name causes is part of the same edit,
    /// and the save reads the whole row anyway.
    /// </summary>
    [Fact]
    public void Setting_the_name_reports_one_change()
    {
        var changes = 0;
        var row = Row(changed: () => changes++);

        row.Name = "Yellow";

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Choosing_a_colour_still_reports_a_change()
    {
        var changes = 0;
        var row = Row(changed: () => changes++);

        row.Colour = "#4AA3E8";

        Assert.Equal(1, changes);
    }

    [Fact]
    public void The_colour_a_name_picks_is_one_of_the_offered_swatches()
    {
        foreach (var name in (string[])["Red", "Yellow", "Green", "Blue", "Purple"])
        {
            var row = Row();
            row.Name = name;

            Assert.Contains(row.Colour, LabelRowViewModel.Swatches);
        }
    }
}

public class LabelColourWordTests
{
    [Theory]
    [InlineData("Red", true)]
    [InlineData("yellow", true)]
    [InlineData("  Blue  ", true)]
    [InlineData("Portfolio", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NamesAColour_asks_only_whether_the_word_offers_one(string? label, bool expected)
        => Assert.Equal(expected, LabelColours.NamesAColour(label));

    /// <summary>
    /// The library still outranks the word: a label named "Yellow" and coloured blue on purpose is a
    /// deliberate choice, and asking where the colour came from must still say "the library".
    /// </summary>
    [Fact]
    public void A_library_definition_still_wins_over_the_word()
    {
        var library = new LabelLibrary { Labels = [new LabelDefinition("Yellow", "#4AA3E8")] };

        Assert.False(LabelColours.IsFromTheWord(library, "Yellow"));
        Assert.True(LabelColours.NamesAColour("Yellow"));
        Assert.Equal("#4AA3E8", LabelColours.Resolve(library, "Yellow"));
    }
}
