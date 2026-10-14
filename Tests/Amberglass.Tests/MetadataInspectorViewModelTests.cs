using System.Collections.Immutable;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Amberglass.UI.Services;
using Amberglass.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class MetadataInspectorViewModelTests
{
    private sealed class StubMetadataProvider(MediaMetadata? result) : IMetadataProvider
    {
        public bool IsAvailable => true;

        public Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(
                files.ToDictionary(f => f.FullPath, _ => result ?? MediaMetadata.Empty));
    }

    private sealed class StubThumbnailService : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file,
            int maxEdgePixels,
            ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private const string FilePath = "/library/IMG001.jpg";

    private static MediaItemViewModel Item() => new(
        new MediaFile
        {
            FullPath = FilePath,
            FileName = "IMG001.jpg",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        },
        new StubThumbnailService());

    /// <summary>Records what it was asked to write without touching the filesystem.</summary>
    private sealed class StubMetadataWriter : IMetadataWriter
    {
        public bool IsAvailable { get; set; } = true;

        public bool ShouldSucceed { get; set; } = true;

        public List<(MediaFile File, EditableMetadata Metadata)> Writes { get; } = [];

        public Task<SidecarWriteResult> WriteSidecarAsync(
            MediaFile file,
            EditableMetadata metadata,
            SidecarWriteOptions options,
            CancellationToken cancellationToken = default)
        {
            Writes.Add((file, metadata));

            return Task.FromResult(ShouldSucceed
                ? new SidecarWriteResult(file.FullPath, true, file.FullPath + ".xmp")
                : SidecarWriteResult.Failed(file.FullPath, "stub failure"));
        }

        public Task<EmbedWriteResult> WriteEmbeddedAsync(
            MediaFile file,
            EditableMetadata metadata,
            EmbedWriteOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbedWriteResult(file.FullPath, ShouldSucceed));
    }

    private static (MetadataInspectorViewModel Inspector, PendingChangeStore Store, StubMetadataWriter Writer)
        Create(MediaMetadata? metadata, KeywordLibrary? library = null)
    {
        var store = new PendingChangeStore();
        var writer = new StubMetadataWriter();
        var inspector = new MetadataInspectorViewModel(
            new StubMetadataProvider(metadata),
            writer,
            store,
            new StubKeywordLibrary(library ?? KeywordLibrary.Empty),
            new StubSettings(AppSettings.Default),
            new KeywordClipboard(),
            NullLogger<MetadataInspectorViewModel>.Instance);

        return (inspector, store, writer);
    }

    /// <summary>Settings held in memory, so a test can flip the keyword restriction.</summary>
    private sealed class StubSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = settings;

        public event EventHandler<AppSettings>? Changed;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    private sealed class StubKeywordLibrary(KeywordLibrary library) : IKeywordLibraryService
    {
        public KeywordLibrary Current { get; private set; } = library;

        public event EventHandler<KeywordLibrary>? Changed;

        public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
        {
            Current = library;
            Changed?.Invoke(this, library);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Loads_existing_metadata_into_the_fields()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata
            {
                Title = "Lioness at dawn",
                Rating = 4,
                Keywords = ["wildlife", "Namibia"]
            }
        };

        var (inspector, store, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        Assert.Equal("Lioness at dawn", inspector.Title);
        Assert.Equal(4, inspector.Rating);
        Assert.Equal(["wildlife", "Namibia"], inspector.Keywords.ToArray());

        // Populating the form is not an edit.
        Assert.Equal(0, store.Count);
        Assert.False(inspector.HasPendingChanges);
    }

    [Fact]
    public async Task Setting_a_rating_from_a_string_parameter_works()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        // XAML passes CommandParameter="4" as a string; an int-typed command would silently no-op.
        inspector.SetRatingCommand.Execute("4");

        Assert.Equal(4, inspector.Rating);
    }

    [Fact]
    public async Task Clicking_the_current_rating_clears_it()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.SetRatingCommand.Execute("3");
        inspector.SetRatingCommand.Execute("3");

        Assert.Equal(0, inspector.Rating);
    }

    [Fact]
    public async Task A_non_numeric_rating_parameter_is_ignored()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.SetRatingCommand.Execute("not a number");

        Assert.Equal(0, inspector.Rating);
    }

    [Fact]
    public async Task Editing_a_field_records_a_pending_change()
    {
        var (inspector, store, _) = Create(MediaMetadata.Empty);
        var item = Item();
        await inspector.LoadAsync(item);

        inspector.Title = "New title";

        Assert.True(store.HasChanges(FilePath));
        Assert.Equal("New title", store.GetEdited(FilePath)!.Title);
        Assert.True(inspector.HasPendingChanges);
        Assert.True(item.HasPendingChanges);
    }

    [Fact]
    public async Task Editing_back_to_the_original_drops_the_pending_change()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "Original" } };
        var (inspector, store, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.Title = "Changed";
        inspector.Title = "Original";

        Assert.False(store.HasChanges(FilePath));
        Assert.False(inspector.HasPendingChanges);
    }

    [Fact]
    public async Task Adding_a_keyword_splits_on_commas()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.NewKeyword = "wildlife, Namibia, lioness";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["wildlife", "Namibia", "lioness"], inspector.Keywords.ToArray());
        Assert.Equal(string.Empty, inspector.NewKeyword);
    }

    [Fact]
    public async Task Duplicate_keywords_are_ignored_case_insensitively()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());

        inspector.NewKeyword = "wildlife";
        inspector.AddKeywordCommand.Execute(null);
        inspector.NewKeyword = "WILDLIFE";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["wildlife"], inspector.Keywords.ToArray());
    }

    [Fact]
    public async Task Removing_a_keyword_records_a_pending_change()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Keywords = ["a", "b"] } };
        var (inspector, store, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.RemoveKeywordCommand.Execute("a");

        Assert.Equal(["b"], inspector.Keywords.ToArray());
        Assert.True(store.HasChanges(FilePath));
    }

    [Fact]
    public async Task Revert_restores_the_values_from_disk()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "Original", Rating = 2 } };
        var (inspector, store, _) = Create(metadata);
        var item = Item();
        await inspector.LoadAsync(item);

        inspector.Title = "Changed";
        inspector.SetRatingCommand.Execute("5");
        Assert.True(store.HasChanges(FilePath));

        inspector.RevertChangesCommand.Execute(null);

        Assert.Equal("Original", inspector.Title);
        Assert.Equal(2, inspector.Rating);
        Assert.False(store.HasChanges(FilePath));
        Assert.False(item.HasPendingChanges);
    }

    [Fact]
    public async Task Reselecting_a_file_shows_its_pending_edit_rather_than_the_value_on_disk()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "On disk" } };
        var (inspector, _, _) = Create(metadata);

        await inspector.LoadAsync(Item());
        inspector.Title = "Edited but unsaved";

        // Select something else, then come back.
        await inspector.LoadAsync(null);
        await inspector.LoadAsync(Item());

        Assert.Equal("Edited but unsaved", inspector.Title);
        Assert.True(inspector.HasPendingChanges);
    }

    [Fact]
    public async Task Writing_the_sidecar_clears_the_pending_change()
    {
        var (inspector, store, writer) = Create(MediaMetadata.Empty);
        var item = Item();
        await inspector.LoadAsync(item);

        inspector.Title = "New title";
        Assert.True(store.HasChanges(FilePath));

        await inspector.WriteSidecarCommand.ExecuteAsync(null);

        Assert.Equal("New title", Assert.Single(writer.Writes).Metadata.Title);
        Assert.False(store.HasChanges(FilePath));
        Assert.False(item.HasPendingChanges);
        Assert.False(inspector.WriteFailed);
    }

    /// <summary>
    /// The grid draws the pending change until the write, and the catalog's reading of the file is
    /// from before the edit — so what was written has to be announced, or a rated raw file loses its
    /// stars the moment it is saved and only gets them back when the folder is read again.
    /// </summary>
    [Fact]
    public async Task What_was_written_is_announced()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        var announced = new List<(MediaFile File, EditableMetadata Written)>();

        inspector.SidecarWritten += (file, written) => announced.Add((file, written));

        await inspector.LoadAsync(Item());
        inspector.Rating = 4;

        await inspector.WriteSidecarCommand.ExecuteAsync(null);

        var (file, metadata) = Assert.Single(announced);
        Assert.Equal(FilePath, file.FullPath);
        Assert.Equal(4, metadata.Rating);
    }

    [Fact]
    public async Task Nothing_is_announced_when_the_write_fails()
    {
        var (inspector, _, writer) = Create(MediaMetadata.Empty);
        writer.ShouldSucceed = false;
        var announced = 0;

        inspector.SidecarWritten += (_, _) => announced++;

        await inspector.LoadAsync(Item());
        inspector.Rating = 4;

        await inspector.WriteSidecarCommand.ExecuteAsync(null);

        // The grid is still right: the change is still pending, which is what it is drawing.
        Assert.Equal(0, announced);
    }

    [Fact]
    public async Task The_save_message_belongs_to_the_file_it_saved()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);
        await inspector.LoadAsync(Item());
        inspector.Rating = 4;

        await inspector.WriteSidecarCommand.ExecuteAsync(null);
        Assert.NotNull(inspector.WriteStatus);

        // Another file. The panel was still reporting "Saved to IMG001.xmp" beside it.
        await inspector.LoadAsync(new MediaItemViewModel(
            new MediaFile
            {
                FullPath = "/library/IMG002.jpg",
                FileName = "IMG002.jpg",
                MediaType = MediaType.Image,
                SizeBytes = 1,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new StubThumbnailService()));

        Assert.Null(inspector.WriteStatus);
    }

    [Fact]
    public async Task A_failed_write_keeps_the_pending_change()
    {
        var (inspector, store, writer) = Create(MediaMetadata.Empty);
        writer.ShouldSucceed = false;
        await inspector.LoadAsync(Item());

        inspector.Title = "New title";
        await inspector.WriteSidecarCommand.ExecuteAsync(null);

        // Losing the user's edit because a write failed would be the worst possible outcome.
        Assert.True(store.HasChanges(FilePath));
        Assert.True(inspector.WriteFailed);
        Assert.Equal("stub failure", inspector.WriteStatus);
    }

    [Fact]
    public async Task Conflicts_are_surfaced_when_the_layers_disagree()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Embedded", Rating = 1 },
            Sidecar = new EditableMetadata { Title = "Sidecar", Rating = 5 },
            SidecarPath = "/library/IMG001.xmp"
        };

        var (inspector, _, _) = Create(metadata);
        var item = Item();
        await inspector.LoadAsync(item);

        Assert.True(inspector.HasConflicts);
        Assert.Equal(2, inspector.Conflicts.Count);
        Assert.True(item.HasConflicts);
    }

    [Fact]
    public async Task Resolving_a_conflict_records_a_pending_change_without_writing()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Embedded", Rating = 1 },
            Sidecar = new EditableMetadata { Title = "Sidecar", Rating = 5 },
            SidecarPath = "/library/IMG001.xmp"
        };

        var (inspector, store, writer) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.ResolveConflictsCommand.Execute("KeepEmbedded");

        Assert.Equal("Embedded", inspector.Title);
        Assert.Equal(1, inspector.Rating);
        Assert.False(inspector.HasConflicts);

        // Resolving is a decision, not a commit.
        Assert.True(store.HasChanges(FilePath));
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task An_unrecognised_resolution_is_ignored()
    {
        var metadata = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Embedded" },
            Sidecar = new EditableMetadata { Title = "Sidecar" },
            SidecarPath = "/library/IMG001.xmp"
        };

        var (inspector, _, _) = Create(metadata);
        await inspector.LoadAsync(Item());

        inspector.ResolveConflictsCommand.Execute("nonsense");

        Assert.True(inspector.HasConflicts);
    }

    private static MediaItemViewModel VideoItem() => new(
        new MediaFile
        {
            FullPath = "/library/CLIP.mp4",
            FileName = "CLIP.mp4",
            MediaType = MediaType.Video,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        },
        new StubThumbnailService());

    [Fact]
    public async Task Selecting_an_image_moves_off_the_hidden_video_tab()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);

        await inspector.LoadAsync(VideoItem());
        inspector.SelectedTabIndex = 2; // Video

        await inspector.LoadAsync(Item());

        // The Video tab is hidden for stills; leaving it selected shows empty fields under no tab.
        Assert.False(inspector.IsVideo);
        Assert.Equal(0, inspector.SelectedTabIndex);
    }

    [Fact]
    public async Task Selecting_an_image_keeps_a_still_relevant_tab()
    {
        var (inspector, _, _) = Create(MediaMetadata.Empty);

        await inspector.LoadAsync(VideoItem());
        inspector.SelectedTabIndex = 1; // Camera

        await inspector.LoadAsync(Item());

        Assert.Equal(1, inspector.SelectedTabIndex);
    }

    [Fact]
    public async Task Deselecting_clears_the_panel()
    {
        var metadata = new MediaMetadata { Embedded = new EditableMetadata { Title = "Something" } };
        var (inspector, _, _) = Create(metadata);

        await inspector.LoadAsync(Item());
        await inspector.LoadAsync(null);

        Assert.False(inspector.HasItem);
        Assert.Null(inspector.Title);
        Assert.Empty(inspector.Keywords);
    }
}

/// <summary>
/// The tick list on the metadata panel. What is written is the keyword's own bare name — the grouping
/// is an organising device and never reaches the file.
/// </summary>
public class KeywordPickerTests
{
    private static MetadataInspectorViewModel Create(KeywordLibrary library, params string[] existing)
        => Create(library, AppSettings.Default, existing);

    private static MetadataInspectorViewModel Create(KeywordLibrary library, AppSettings settings, params string[] existing)
        => new(
            new StubProvider(existing),
            new StubWriter(),
            new PendingChangeStore(),
            new StubLibrary(library),
            new StubSettingsService(settings),
            new KeywordClipboard(),
            NullLogger<MetadataInspectorViewModel>.Instance);

    private sealed class StubSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = settings;

        public event EventHandler<AppSettings>? Changed;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    private static KeywordPickerNodeViewModel Node(MetadataInspectorViewModel inspector, string name)
        => inspector.KeywordPicker.SelectMany(root => root.SelfAndDescendants()).First(n => n.Name == name);

    [Fact]
    public void The_library_becomes_a_tick_list()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Shot type|wide", "Shot type|close"]));

        Assert.True(inspector.HasKeywordLibrary);

        var group = Assert.Single(inspector.KeywordPicker);
        Assert.Equal("Shot type", group.Name);
        Assert.Equal(2, group.Children.Count);
    }

    [Fact]
    public void An_empty_library_has_nothing_to_show()
        => Assert.False(Create(KeywordLibrary.Empty).HasKeywordLibrary);

    /// <summary>Ticking writes the bare name, never the path it is filed under.</summary>
    [Fact]
    public void Ticking_applies_the_bare_name()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Shot type|wide"]));

        Node(inspector, "wide").IsChecked = true;

        Assert.Equal(["wide"], inspector.Keywords);
    }

    [Fact]
    public void Unticking_removes_it_again()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Shot type|wide"]));
        var node = Node(inspector, "wide");

        node.IsChecked = true;
        node.IsChecked = false;

        Assert.Empty(inspector.Keywords);
    }

    /// <summary>A group is a keyword in its own right, so it can be applied like any other.</summary>
    [Fact]
    public void A_group_can_be_ticked_too()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Mood|warm"]));

        Node(inspector, "Mood").IsChecked = true;

        Assert.Equal(["Mood"], inspector.Keywords);
    }

    /// <summary>
    /// The consequence of names being the identity: the same word filed in two groups is one keyword,
    /// so ticking it in one place must show it ticked in the other.
    /// </summary>
    [Fact]
    public void The_same_name_in_two_groups_ticks_in_both()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Shot type|wide", "Landscape|wide"]));

        var nodes = inspector.KeywordPicker
            .SelectMany(root => root.SelfAndDescendants())
            .Where(n => n.Name == "wide")
            .ToList();

        Assert.Equal(2, nodes.Count);

        nodes[0].IsChecked = true;

        Assert.True(nodes[1].IsChecked);
        Assert.Single(inspector.Keywords);
    }

    /// <summary>Typing a keyword by hand must tick it wherever the library offers it.</summary>
    [Fact]
    public void Typing_a_keyword_ticks_it_in_the_library()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Shot type|wide"]));

        inspector.NewKeyword = "wide";
        inspector.AddKeywordCommand.Execute(null);

        Assert.True(Node(inspector, "wide").IsChecked);
    }

    [Fact]
    public void Removing_a_chip_unticks_it()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Shot type|wide"]));
        Node(inspector, "wide").IsChecked = true;

        inspector.RemoveKeywordCommand.Execute("wide");

        Assert.False(Node(inspector, "wide").IsChecked);
    }

    /// <summary>Case is not a difference: "Wide" on the file ticks "wide" in the library.</summary>
    [Fact]
    public void Ticking_matches_regardless_of_case()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["wide"]));

        inspector.NewKeyword = "WIDE";
        inspector.AddKeywordCommand.Execute(null);

        Assert.True(Node(inspector, "wide").IsChecked);
        Assert.Single(inspector.Keywords);
    }

    /// <summary>Editing the library in Settings rebuilds the list without a restart.</summary>
    [Fact]
    public async Task The_list_follows_the_library()
    {
        var service = new StubLibrary(KeywordLibrary.Empty);
        var inspector = new MetadataInspectorViewModel(
            new StubProvider([]), new StubWriter(), new PendingChangeStore(), service,
            new StubSettingsService(AppSettings.Default),
            new KeywordClipboard(),
            NullLogger<MetadataInspectorViewModel>.Instance);

        Assert.False(inspector.HasKeywordLibrary);

        await service.SaveAsync(KeywordLibrary.FromFlat(["Mood|warm"]));

        Assert.True(inspector.HasKeywordLibrary);
        Assert.Equal("Mood", inspector.KeywordPicker.Single().Name);
    }

    /// <summary>
    /// The panel must not fold itself away between photographs: tagging a folder means tagging one and
    /// moving to the next, and reopening it each time would make the feature not worth using.
    /// </summary>
    [Fact]
    public async Task The_open_state_survives_a_change_of_selection()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Mood|warm"]));
        inspector.IsKeywordLibraryOpen = true;

        await inspector.LoadAsync(null);

        Assert.True(inspector.IsKeywordLibraryOpen);
    }

    [Fact]
    public void It_starts_closed()
        => Assert.False(Create(KeywordLibrary.FromFlat(["Mood|warm"])).IsKeywordLibraryOpen);

    private sealed class StubProvider(string[] keywords) : IMetadataProvider
    {
        public bool IsAvailable => true;

        public Task<MediaMetadata> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(new MediaMetadata
            {
                Embedded = new EditableMetadata { Keywords = [.. keywords] }
            });

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(new Dictionary<string, MediaMetadata>());
    }

    private sealed class StubWriter : IMetadataWriter
    {
        public bool IsAvailable => true;

        public Task<SidecarWriteResult> WriteSidecarAsync(
            MediaFile file,
            EditableMetadata metadata,
            SidecarWriteOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SidecarWriteResult(file.FullPath, true, file.FullPath + ".xmp"));

        public Task<EmbedWriteResult> WriteEmbeddedAsync(
            MediaFile file,
            EditableMetadata metadata,
            EmbedWriteOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbedWriteResult(file.FullPath, true));
    }

    private sealed class StubLibrary(KeywordLibrary library) : IKeywordLibraryService
    {
        public KeywordLibrary Current { get; private set; } = library;

        public event EventHandler<KeywordLibrary>? Changed;

        public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
        {
            Current = library;
            Changed?.Invoke(this, library);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Searching the library instead of accepting free text — the discipline that keeps a ground-texture
/// shot from becoming "ground" one day and "sand", "dirt" or "texture" the next.
/// </summary>
public class KeywordSearchTests
{
    private static readonly KeywordLibrary Library =
        KeywordLibrary.FromFlat(["Subject|Ground", "Subject|Sand", "Mood|Golden Hour"]);

    private static MetadataInspectorViewModel Create(
        KeywordLibrary? library = null,
        bool restrict = true,
        params string[] existing)
        => new(
            new SearchStubProvider(existing),
            new SearchStubWriter(),
            new PendingChangeStore(),
            new SearchStubLibrary(library ?? Library),
            new SearchStubSettings(AppSettings.Default with { RestrictKeywordsToLibrary = restrict }),
            new KeywordClipboard(),
            NullLogger<MetadataInspectorViewModel>.Instance);

    [Fact]
    public void Typing_filters_the_library()
    {
        var inspector = Create();

        inspector.NewKeyword = "an";

        Assert.Equal(["Sand"], inspector.KeywordMatches.Select(m => m.Name));
    }

    /// <summary>
    /// Contains, not starts-with. Remembering the beginning of a name is the hard part — "hour"
    /// should find "Golden Hour".
    /// </summary>
    [Fact]
    public void Matching_looks_anywhere_in_the_name()
    {
        var inspector = Create();

        inspector.NewKeyword = "hour";

        Assert.Equal(["Golden Hour"], inspector.KeywordMatches.Select(m => m.Name));
    }

    [Fact]
    public void An_empty_box_shows_no_matches()
    {
        var inspector = Create();

        inspector.NewKeyword = "an";
        inspector.NewKeyword = "";

        Assert.Empty(inspector.KeywordMatches);
        Assert.False(inspector.IsSearchingKeywords);
    }

    [Fact]
    public void Enter_applies_the_only_match()
    {
        var inspector = Create();

        inspector.NewKeyword = "grou";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["Ground"], inspector.Keywords);
        Assert.Equal(string.Empty, inspector.NewKeyword);
    }

    /// <summary>
    /// An exact name beats a partial one that happens to sort first, or typing "Sand" in full would
    /// apply something else entirely.
    /// </summary>
    [Fact]
    public void An_exact_name_wins_over_a_partial_match()
    {
        var inspector = Create(KeywordLibrary.FromFlat(["Sand", "Sand Dune"]));

        inspector.NewKeyword = "Sand";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["Sand"], inspector.Keywords);
    }

    /// <summary>The point of the restriction: a word outside the library is not applied by typing it.</summary>
    [Fact]
    public void A_word_outside_the_library_is_not_applied()
    {
        var inspector = Create();

        inspector.NewKeyword = "dirt";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Empty(inspector.Keywords);
        Assert.True(inspector.HasNoKeywordMatch);
        Assert.True(inspector.CanAddTypedKeywordToLibrary);
    }

    /// <summary>But it is never a dead end: one action adds it to the library and applies it.</summary>
    [Fact]
    public async Task An_unmatched_word_can_be_adopted_and_applied()
    {
        var service = new SearchStubLibrary(Library);
        var inspector = new MetadataInspectorViewModel(
            new SearchStubProvider([]), new SearchStubWriter(), new PendingChangeStore(), service,
            new SearchStubSettings(AppSettings.Default),
            new KeywordClipboard(),
            NullLogger<MetadataInspectorViewModel>.Instance);

        inspector.NewKeyword = "texture";
        await inspector.AddTypedKeywordToLibraryCommand.ExecuteAsync(null);

        Assert.Equal(["texture"], inspector.Keywords);
        Assert.Contains("texture", service.Current.AllNames());
        Assert.Equal(string.Empty, inspector.NewKeyword);
    }

    /// <summary>With the restriction off, the box behaves as it always did.</summary>
    [Fact]
    public void Unrestricted_typing_still_accepts_anything()
    {
        var inspector = Create(restrict: false);

        inspector.NewKeyword = "dirt";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["dirt"], inspector.Keywords);
    }

    /// <summary>
    /// With nothing to choose from, refusing every word would be absurd — so the restriction only
    /// takes effect once a library exists.
    /// </summary>
    [Fact]
    public void An_empty_library_leaves_typing_unrestricted()
    {
        var inspector = Create(KeywordLibrary.Empty);

        Assert.False(inspector.IsRestrictedToLibrary);

        inspector.NewKeyword = "dirt";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(["dirt"], inspector.Keywords);
    }

    [Fact]
    public void Clicking_a_match_applies_it()
    {
        var inspector = Create();

        inspector.NewKeyword = "grou";
        inspector.ApplyFromLibraryCommand.Execute("Ground");

        Assert.Equal(["Ground"], inspector.Keywords);
    }

    // ---- Chips ------------------------------------------------------------------------------------

    /// <summary>
    /// Files arrive carrying keywords from cameras and other tools. The chip has to say which of them
    /// the library has never heard of, or there is no way to notice.
    /// </summary>
    [Fact]
    public async Task Chips_say_whether_the_library_knows_them()
    {
        var inspector = Create();

        inspector.NewKeyword = "Ground";
        inspector.AddKeywordCommand.Execute(null);

        // Something that arrived with the file rather than from the library.
        var unrestricted = Create(restrict: false);
        unrestricted.NewKeyword = "dirt";
        unrestricted.AddKeywordCommand.Execute(null);

        Assert.True(inspector.AppliedKeywords.First(k => !k.IsCopyAction).IsInLibrary);
        Assert.False(unrestricted.AppliedKeywords.First(k => !k.IsCopyAction).IsInLibrary);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_unknown_keyword_can_be_adopted_from_its_chip()
    {
        var service = new SearchStubLibrary(Library);
        var inspector = new MetadataInspectorViewModel(
            new SearchStubProvider([]), new SearchStubWriter(), new PendingChangeStore(), service,
            new SearchStubSettings(AppSettings.Default with { RestrictKeywordsToLibrary = false }),
            new KeywordClipboard(),
            NullLogger<MetadataInspectorViewModel>.Instance);

        inspector.NewKeyword = "dirt";
        inspector.AddKeywordCommand.Execute(null);

        await inspector.AdoptKeywordCommand.ExecuteAsync("dirt");

        Assert.Contains("dirt", service.Current.AllNames());
        Assert.True(inspector.AppliedKeywords.First(k => !k.IsCopyAction).IsInLibrary);
    }

    // ---- The copy pill ----------------------------------------------------------------------------

    /// <summary>
    /// The copy control rides in the same collection as the keywords so it wraps with them. Two
    /// elements in one wrap panel would each be measured as a block, and a long list would either
    /// overflow or strand the button on a line of its own.
    /// </summary>
    [Fact]
    public void The_copy_pill_trails_the_keywords()
    {
        var inspector = Create();

        inspector.NewKeyword = "Ground";
        inspector.AddKeywordCommand.Execute(null);

        Assert.Equal(2, inspector.AppliedKeywords.Count);
        Assert.False(inspector.AppliedKeywords[0].IsCopyAction);
        Assert.True(inspector.AppliedKeywords[1].IsCopyAction);
    }

    [Fact]
    public void There_is_nothing_to_copy_when_there_are_no_keywords()
        => Assert.Empty(Create().AppliedKeywords);

    // ---- Copying from another file ------------------------------------------------------------------

    /// <summary>
    /// Copying the file already on screen uses what is on screen — including an edit made a moment
    /// ago and not yet written anywhere.
    /// </summary>
    [Fact]
    public async Task Copying_the_current_file_uses_what_is_shown()
    {
        var clipboard = new KeywordClipboard();
        var inspector = new MetadataInspectorViewModel(
            new SearchStubProvider(["Sand"]), new SearchStubWriter(), new PendingChangeStore(),
            new SearchStubLibrary(Library), new SearchStubSettings(AppSettings.Default), clipboard,
            NullLogger<MetadataInspectorViewModel>.Instance);

        await inspector.LoadAsync(new MediaItemViewModel(File("/library/a.jpg"), new NoThumbnails()));

        inspector.NewKeyword = "Ground";
        inspector.AddKeywordCommand.Execute(null);

        await inspector.CopyKeywordsFromAsync(File("/library/a.jpg"));

        Assert.Equal(["Sand", "Ground"], clipboard.Keywords.ToArray());
    }

    private sealed class NoThumbnails : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file,
            int maxEdgePixels,
            ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    /// <summary>
    /// Copying a tile that is not selected reads that file — and reads the pending store first, so it
    /// picks up edits that have not been written yet rather than spreading stale tags.
    /// </summary>
    [Fact]
    public async Task Copying_another_file_prefers_its_pending_edit()
    {
        var clipboard = new KeywordClipboard();
        var pending = new PendingChangeStore();

        pending.Set(
            "/library/other.jpg",
            EditableMetadata.Empty,
            new EditableMetadata { Keywords = ["edited"] });

        var inspector = new MetadataInspectorViewModel(
            new SearchStubProvider(["onDisk"]), new SearchStubWriter(), pending,
            new SearchStubLibrary(Library), new SearchStubSettings(AppSettings.Default), clipboard,
            NullLogger<MetadataInspectorViewModel>.Instance);

        await inspector.CopyKeywordsFromAsync(File("/library/other.jpg"));

        Assert.Equal(["edited"], clipboard.Keywords.ToArray());
    }

    [Fact]
    public async Task Copying_another_file_falls_back_to_what_is_on_disk()
    {
        var clipboard = new KeywordClipboard();
        var inspector = new MetadataInspectorViewModel(
            new SearchStubProvider(["onDisk"]), new SearchStubWriter(), new PendingChangeStore(),
            new SearchStubLibrary(Library), new SearchStubSettings(AppSettings.Default), clipboard,
            NullLogger<MetadataInspectorViewModel>.Instance);

        await inspector.CopyKeywordsFromAsync(File("/library/other.jpg"));

        Assert.Equal(["onDisk"], clipboard.Keywords.ToArray());
    }

    private static MediaFile File(string path) => new()
    {
        FullPath = path,
        FileName = Path.GetFileName(path),
        MediaType = MediaType.Image,
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private sealed class SearchStubProvider(string[] keywords) : IMetadataProvider
    {
        public bool IsAvailable => true;

        public Task<MediaMetadata> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(new MediaMetadata { Embedded = new EditableMetadata { Keywords = [.. keywords] } });

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(new Dictionary<string, MediaMetadata>());
    }

    private sealed class SearchStubWriter : IMetadataWriter
    {
        public bool IsAvailable => true;

        public Task<SidecarWriteResult> WriteSidecarAsync(MediaFile file, EditableMetadata metadata, SidecarWriteOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new SidecarWriteResult(file.FullPath, true, file.FullPath + ".xmp"));

        public Task<EmbedWriteResult> WriteEmbeddedAsync(MediaFile file, EditableMetadata metadata, EmbedWriteOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbedWriteResult(file.FullPath, true));
    }

    private sealed class SearchStubLibrary(KeywordLibrary library) : IKeywordLibraryService
    {
        public KeywordLibrary Current { get; private set; } = library;

        public event EventHandler<KeywordLibrary>? Changed;

        public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
        {
            Current = library;
            Changed?.Invoke(this, library);
            return Task.CompletedTask;
        }
    }

    private sealed class SearchStubSettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = settings;

        public event EventHandler<AppSettings>? Changed;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// The raw tag list, grouped and filtered.
///
/// A RAF arrives with around 200 tags across eight ExifTool groups, two thirds of them MakerNotes
/// nobody went looking for. Collapsing by group and filtering is what makes the tab usable.
/// </summary>
public class RawMetadataFilterTests
{
    private static RawMetadataTag Tag(string group, string name, string value = "x")
        => new(group, name, value);

    [Fact]
    public void An_empty_filter_keeps_everything()
    {
        Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), null));
        Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), ""));
        Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "   "));
    }

    [Fact]
    public void The_name_and_the_value_are_both_searched()
    {
        Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "iso"));
        Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "200"));
        Assert.False(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "shutter"));
    }

    [Fact]
    public void The_group_is_searched_too_so_a_group_name_narrows_to_it()
    {
        Assert.True(RawMetadataFilter.Matches(Tag("MakerNotes", "Sharpness"), "makernotes"));
        Assert.False(RawMetadataFilter.Matches(Tag("EXIF", "Sharpness"), "makernotes"));
    }

    [Fact]
    public void Matching_ignores_case()
        => Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "Iso"));

    /// <summary>Terms narrow rather than widen — otherwise a second word is worse than useless.</summary>
    [Fact]
    public void Every_term_must_match()
    {
        Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "exif iso"));
        Assert.False(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "exif shutter"));
    }

    /// <summary>
    /// Each term may match either field, which is what makes "iso 200" work: the name carries one
    /// and the value the other, and neither field holds both.
    /// </summary>
    [Fact]
    public void A_term_may_match_the_name_while_another_matches_the_value()
        => Assert.True(RawMetadataFilter.Matches(Tag("EXIF", "ISO", "200"), "iso 200"));
}

public class RawMetadataGroupingTests
{
    private sealed class Provider(MediaMetadata result) : IMetadataProvider
    {
        public bool IsAvailable => true;

        public Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult<MediaMetadata?>(result);

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(
                files.ToDictionary(f => f.FullPath, _ => result));
    }

    private sealed class Thumbnails : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file,
            int maxEdgePixels,
            ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private static readonly ImmutableArray<RawMetadataTag> Tags =
    [
        new("EXIF", "ISO", "200"),
        new("EXIF", "Make", "FUJIFILM"),
        new("File", "FileSize", "31 MB"),
        new("MakerNotes", "Sharpness", "Normal"),
        new("MakerNotes", "ShutterType", "Mechanical")
    ];

    private static MediaItemViewModel Item() => new(
        new MediaFile
        {
            FullPath = "/library/DSCF9148.RAF",
            FileName = "DSCF9148.RAF",
            MediaType = MediaType.Image,
            SizeBytes = 1,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            CreatedUtc = DateTimeOffset.UnixEpoch
        },
        new Thumbnails());

    private static async Task<MetadataInspectorViewModel> LoadedAsync(ImmutableArray<RawMetadataTag>? tags = null)
    {
        var inspector = new MetadataInspectorViewModel(
            new Provider(new MediaMetadata { RawTags = tags ?? Tags }),
            new StubWriter(),
            new PendingChangeStore(),
            new StubLibrary(),
            new StubSettings(),
            new KeywordClipboard(),
            NullLogger<MetadataInspectorViewModel>.Instance);

        await inspector.LoadAsync(Item());
        return inspector;
    }

    private sealed class StubWriter : IMetadataWriter
    {
        public bool IsAvailable => true;

        public Task<SidecarWriteResult> WriteSidecarAsync(
            MediaFile file, EditableMetadata metadata, SidecarWriteOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SidecarWriteResult(file.FullPath, true, file.FullPath + ".xmp"));

        public Task<EmbedWriteResult> WriteEmbeddedAsync(
            MediaFile file, EditableMetadata metadata, EmbedWriteOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbedWriteResult(file.FullPath, true));
    }

    private sealed class StubLibrary : IKeywordLibraryService
    {
        public KeywordLibrary Current { get; private set; } = KeywordLibrary.Empty;

        public event EventHandler<KeywordLibrary>? Changed;

        public Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
        {
            Current = library;
            Changed?.Invoke(this, library);
            return Task.CompletedTask;
        }
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; private set; } = AppSettings.Default;

        public event EventHandler<AppSettings>? Changed;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Changed?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Tags_are_gathered_into_one_section_per_group()
    {
        var inspector = await LoadedAsync();

        Assert.Equal(["EXIF", "File", "MakerNotes"], inspector.RawTagGroups.Select(g => g.Name));
        Assert.Equal(["ISO", "Make"], inspector.RawTagGroups[0].Tags.Select(t => t.Name));
    }

    /// <summary>
    /// With a sidecar, the list splits into a "Sidecar" half and an "Embedded" half, each heading on
    /// its first section only, and the sidecar sections drop the prefix the heading now carries.
    /// </summary>
    [Fact]
    public async Task A_sidecar_splits_the_sections_under_two_headings()
    {
        var inspector = await LoadedAsync([new("Sidecar:XMP", "Rating", "5"), new("Sidecar:XMP", "Label", "Red"), .. Tags]);

        Assert.Equal(["Sidecar", "Embedded", null, null], inspector.RawTagGroups.Select(g => g.SectionHeading));
        Assert.Equal(["XMP", "EXIF", "File", "MakerNotes"], inspector.RawTagGroups.Select(g => g.Label));
    }

    /// <summary>Nothing to tell apart without a sidecar, so no headings at all.</summary>
    [Fact]
    public async Task Without_a_sidecar_there_are_no_headings()
    {
        var inspector = await LoadedAsync();

        Assert.All(inspector.RawTagGroups, group => Assert.Null(group.SectionHeading));
    }

    /// <summary>A filter matching only the file's own tags still says they are embedded.</summary>
    [Fact]
    public async Task A_filter_keeps_the_heading_of_the_half_it_matched()
    {
        var inspector = await LoadedAsync([new("Sidecar:XMP", "Rating", "5"), .. Tags]);

        inspector.RawTagFilter = "Sharpness";

        var only = Assert.Single(inspector.RawTagGroups);
        Assert.Equal("Embedded", only.SectionHeading);
    }

    /// <summary>Shut by default — collapsing the noise is the whole point of grouping it.</summary>
    [Fact]
    public async Task Sections_start_collapsed()
    {
        var inspector = await LoadedAsync();

        Assert.All(inspector.RawTagGroups, group => Assert.False(group.IsExpanded));
    }

    [Fact]
    public async Task The_header_counts_the_tags_in_the_section()
    {
        var inspector = await LoadedAsync();

        Assert.Equal("2", inspector.RawTagGroups[0].Summary);
        Assert.Equal("1", inspector.RawTagGroups[1].Summary);
    }

    [Fact]
    public async Task Filtering_drops_sections_with_no_match()
    {
        var inspector = await LoadedAsync();

        inspector.RawTagFilter = "shutter";

        Assert.Equal(["MakerNotes"], inspector.RawTagGroups.Select(g => g.Name));
        Assert.Equal(["ShutterType"], inspector.RawTagGroups[0].Tags.Select(t => t.Name));
    }

    /// <summary>A hit inside a shut section looks like no hit at all.</summary>
    [Fact]
    public async Task Filtering_opens_the_sections_that_matched()
    {
        var inspector = await LoadedAsync();

        inspector.RawTagFilter = "shutter";

        Assert.All(inspector.RawTagGroups, group => Assert.True(group.IsExpanded));
    }

    [Fact]
    public async Task A_partly_filtered_section_says_so_in_its_header()
    {
        var inspector = await LoadedAsync();

        inspector.RawTagFilter = "shutter";

        Assert.Equal("1 of 2", inspector.RawTagGroups[0].Summary);
    }

    [Fact]
    public async Task The_summary_counts_the_matches_against_the_whole()
    {
        var inspector = await LoadedAsync();

        Assert.Null(inspector.RawTagFilterSummary);

        inspector.RawTagFilter = "shutter";

        Assert.True(inspector.IsFilteringRawTags);
        Assert.Equal("1 of 5 tags", inspector.RawTagFilterSummary);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_says_so()
    {
        var inspector = await LoadedAsync();

        inspector.RawTagFilter = "nothinglikethis";

        Assert.Empty(inspector.RawTagGroups);
        Assert.True(inspector.HasNoRawTagMatches);
    }

    [Fact]
    public async Task Clearing_the_filter_restores_every_section()
    {
        var inspector = await LoadedAsync();

        inspector.RawTagFilter = "shutter";
        inspector.ClearRawTagFilterCommand.Execute(null);

        Assert.Equal("", inspector.RawTagFilter);
        Assert.Equal(3, inspector.RawTagGroups.Count);
        Assert.False(inspector.HasNoRawTagMatches);
    }

    /// <summary>
    /// Opening EXIF and clicking through a folder should leave EXIF open. The interest is in the
    /// section, not in one file's copy of it.
    /// </summary>
    [Fact]
    public async Task An_opened_section_stays_open_when_another_file_is_selected()
    {
        var inspector = await LoadedAsync();

        inspector.RawTagGroups.Single(g => g.Name == "EXIF").IsExpanded = true;
        await inspector.LoadAsync(Item());

        Assert.True(inspector.RawTagGroups.Single(g => g.Name == "EXIF").IsExpanded);
        Assert.False(inspector.RawTagGroups.Single(g => g.Name == "File").IsExpanded);
    }

    /// <summary>
    /// The filter opened these, not the user, so clearing it must not leave everything open — that
    /// would quietly undo the collapsing the tab exists to provide.
    /// </summary>
    [Fact]
    public async Task Sections_the_filter_opened_shut_again_when_it_is_cleared()
    {
        var inspector = await LoadedAsync();

        inspector.RawTagFilter = "shutter";
        inspector.RawTagFilter = "";

        Assert.All(inspector.RawTagGroups, group => Assert.False(group.IsExpanded));
    }

    [Fact]
    public async Task Clearing_the_selection_empties_the_sections()
    {
        var inspector = await LoadedAsync();

        await inspector.LoadAsync(null);

        Assert.Empty(inspector.RawTagGroups);
    }
}
