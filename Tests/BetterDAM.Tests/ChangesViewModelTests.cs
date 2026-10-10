using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using BetterDAM.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// The Changes review: what it lists, what its buttons act on, and what settling a conflict does.
/// </summary>
public class ChangesViewModelTests
{
    private sealed class StubWriter : IMetadataWriter
    {
        public bool IsAvailable { get; set; } = true;

        public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

        public List<string> Written { get; } = [];

        public Task<SidecarWriteResult> WriteSidecarAsync(
            MediaFile file, EditableMetadata metadata, SidecarWriteOptions options,
            CancellationToken cancellationToken = default)
        {
            if (FailFor.Contains(file.FullPath))
            {
                return Task.FromResult(SidecarWriteResult.Failed(file.FullPath, "stub failure"));
            }

            Written.Add(file.FullPath);
            return Task.FromResult(new SidecarWriteResult(file.FullPath, true, file.FullPath + ".xmp"));
        }

        public Task<EmbedWriteResult> WriteEmbeddedAsync(
            MediaFile file, EditableMetadata metadata, EmbedWriteOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubProvider : IMetadataProvider
    {
        public Dictionary<string, MediaMetadata> Data { get; } = new(StringComparer.Ordinal);

        public bool IsAvailable => true;

        public Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(Data.GetValueOrDefault(file.FullPath));

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files, IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = files.Where(f => Data.ContainsKey(f.FullPath)).ToDictionary(f => f.FullPath, f => Data[f.FullPath], StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(result);
        }
    }

    private sealed class NoopThumbnails : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file, int maxEdgePixels, ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }

    private static MediaFile FileAt(string path) => new()
    {
        FullPath = path,
        FileName = Path.GetFileName(path),
        MediaType = MediaType.Image,
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static readonly MediaFile A = FileAt("/ws/2025/a.jpg");
    private static readonly MediaFile B = FileAt("/ws/2025/b.jpg");
    private static readonly MediaFile C = FileAt("/ws/2024/c.jpg");

    private static readonly EditableMetadata Original = new() { Rating = 2, Keywords = ["sand"] };

    private static (ChangesViewModel Review, PendingChangeStore Store, StubWriter Writer, StubProvider Provider) Create()
    {
        var store = new PendingChangeStore();
        var writer = new StubWriter();
        var provider = new StubProvider();
        var review = new ChangesViewModel(store, new NoopThumbnails(), writer, provider, NullLogger<ChangesViewModel>.Instance)
        {
            WorkspacePath = "/ws",
            Scope = [A, B, C]
        };

        return (review, store, writer, provider);
    }

    [Fact]
    public void ListsEveryPendingFileSortedByPathWithItsDiff()
    {
        var (review, store, _, _) = Create();
        store.Set(B.FullPath, Original, Original with { Rating = 5 });
        store.Set(C.FullPath, Original, Original with { Label = "Red", Keywords = ["sand", "dune"] });

        review.Load();

        Assert.Equal([C.FullPath, B.FullPath], review.Files.Select(f => f.Path).ToArray());
        Assert.Equal("label · +1 keyword", review.Files[0].Summary);
        Assert.Equal("rating", review.Files[1].Summary);
        Assert.Equal("2 files with pending changes", review.Heading);
        Assert.Same(review.Files[0], review.SelectedFile);
    }

    /// <summary>The path shown is relative to the workspace; a full path repeats what every row shares.</summary>
    [Fact]
    public void FoldersAreRelativeToTheWorkspace()
    {
        var (review, store, _, _) = Create();
        store.Set(A.FullPath, Original, Original with { Title = "t" });

        review.Load();

        Assert.Equal("2025", review.Files[0].Folder);
    }

    /// <summary>The rows are the field changes first, then the keywords, so two templates draw one list.</summary>
    [Fact]
    public void RowsCarryFieldsThenKeywords()
    {
        var (review, store, _, _) = Create();
        store.Set(A.FullPath, Original, Original with { Rating = null, Keywords = ["dune"] });

        review.Load();
        var rows = review.Files[0].Rows;

        var field = Assert.IsType<FieldRow>(rows[0]);
        Assert.Equal("2 stars", field.WasText);
        Assert.Equal("—", field.NowText);

        var added = Assert.IsType<KeywordRow>(rows[1]);
        Assert.True(added.Added);
        Assert.Equal("dune", added.Keyword);

        var removed = Assert.IsType<KeywordRow>(rows[2]);
        Assert.False(removed.Added);
        Assert.Equal("sand", removed.Keyword);
    }

    /// <summary>With nothing multi-selected, the buttons act on the file being viewed.</summary>
    [Fact]
    public void DiscardActsOnTheFileInViewWhenNothingElseIsSelected()
    {
        var (review, store, _, _) = Create();
        store.Set(A.FullPath, Original, Original with { Title = "t" });
        store.Set(B.FullPath, Original, Original with { Title = "u" });

        review.Load();
        review.SelectedFile = review.Files.Single(f => f.Path == B.FullPath);

        review.DiscardSelectedCommand.Execute(null);

        Assert.True(store.HasChanges(A.FullPath));
        Assert.False(store.HasChanges(B.FullPath));
    }

    [Fact]
    public async Task WriteActsOnTheSelectionAndLeavesTheRestPending()
    {
        var (review, store, writer, _) = Create();
        store.Set(A.FullPath, Original, Original with { Title = "t" });
        store.Set(B.FullPath, Original, Original with { Title = "u" });
        store.Set(C.FullPath, Original, Original with { Title = "v" });

        review.Load();
        review.SelectedFiles.Add(review.Files.Single(f => f.Path == A.FullPath));
        review.SelectedFiles.Add(review.Files.Single(f => f.Path == C.FullPath));

        await review.WriteSelectedCommand.ExecuteAsync(null);

        Assert.Equal([A.FullPath, C.FullPath], writer.Written);
        Assert.False(store.HasChanges(A.FullPath));
        Assert.True(store.HasChanges(B.FullPath));
        Assert.False(store.HasChanges(C.FullPath));
        Assert.Equal([A, C], review.Written);
    }

    /// <summary>A failure part way leaves exactly the unwritten files pending — not all, not none.</summary>
    [Fact]
    public async Task AFailedWriteStaysPendingWhileTheOthersClear()
    {
        var (review, store, writer, _) = Create();
        writer.FailFor.Add(B.FullPath);
        store.Set(A.FullPath, Original, Original with { Title = "t" });
        store.Set(B.FullPath, Original, Original with { Title = "u" });

        review.Load();
        review.SelectedFiles.Add(review.Files[0]);
        review.SelectedFiles.Add(review.Files[1]);

        await review.WriteSelectedCommand.ExecuteAsync(null);

        Assert.False(store.HasChanges(A.FullPath));
        Assert.True(store.HasChanges(B.FullPath));
        Assert.Contains("1 failed", review.Status);
    }

    [Fact]
    public async Task NothingIsWrittenWithoutAWriter()
    {
        var (review, store, writer, _) = Create();
        writer.IsAvailable = false;
        store.Set(A.FullPath, Original, Original with { Title = "t" });

        review.Load();
        await review.WriteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(writer.Written);
        Assert.True(store.HasChanges(A.FullPath));
    }

    /// <summary>A file in scope that is no longer on disk cannot be written and is not listed.</summary>
    [Fact]
    public void AChangeToAFileThatHasGoneIsNotListed()
    {
        var (review, store, _, _) = Create();
        review.Scope = [];
        store.Set("/nowhere/gone.raf", Original, Original with { Title = "t" });

        review.Load();

        Assert.Empty(review.Files);
        Assert.Equal("Nothing pending", review.Heading);
    }

    // ---- Conflicts ------------------------------------------------------------------------------

    private static MediaMetadata Conflicting() => new()
    {
        Embedded = new EditableMetadata { Title = "In the file", Rating = 3 },
        Sidecar = new EditableMetadata { Title = "In the sidecar" },
        SidecarPath = "/ws/2025/a.xmp"
    };

    // ---- Arriving from one file's warning -------------------------------------------------------

    /// <summary>
    /// The review used to be reachable only from the pending count, so a file that conflicted but
    /// had no edits had no way in. Opening on one file must also not pay for the scan.
    /// </summary>
    [Fact]
    public async Task OpeningOnOneFileShowsItsConflictsWithoutReadingTheRest()
    {
        var (review, _, _, provider) = Create();
        provider.Data[A.FullPath] = Conflicting();
        provider.Data[B.FullPath] = Conflicting();
        review.FocusConflict = A;

        await review.InitialiseAsync();

        Assert.Equal(ChangesViewModel.ConflictsTab, review.SelectedTab);

        var only = Assert.Single(review.Conflicts);
        Assert.Equal(A.FullPath, only.File.FullPath);

        // B is in scope and also conflicts, but was not asked about.
        Assert.Equal(A.FullPath, review.SelectedConflict?.File.FullPath);
    }

    [Fact]
    public async Task OpeningOnOneFileSurfacesBothSidesOfEveryFieldThatDisagrees()
    {
        var (review, _, _, provider) = Create();
        provider.Data[A.FullPath] = Conflicting();
        review.FocusConflict = A;

        await review.InitialiseAsync();

        var conflict = Assert.Single(review.SelectedConflict!.Conflicts);
        Assert.Equal(MetadataConflictDetector.TitleField, conflict.Field);
        Assert.Equal("In the file", conflict.EmbeddedValue);
        Assert.Equal("In the sidecar", conflict.SidecarValue);
    }

    /// <summary>Settled between opening the warning and arriving here.</summary>
    [Fact]
    public async Task OpeningOnAFileThatNoLongerDisagreesSaysSoRatherThanShowingNothing()
    {
        var (review, _, _, provider) = Create();
        provider.Data[A.FullPath] = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Same" },
            Sidecar = new EditableMetadata { Title = "Same" }
        };
        review.FocusConflict = A;

        await review.InitialiseAsync();

        Assert.Empty(review.Conflicts);
        Assert.False(review.HasConflicts);
        Assert.NotNull(review.CheckStatus);
    }

    [Fact]
    public async Task OpeningWithoutAFocusLeavesTheReviewOnTheChangesTab()
    {
        var (review, _, _, provider) = Create();
        provider.Data[A.FullPath] = Conflicting();

        await review.InitialiseAsync();

        Assert.Equal(0, review.SelectedTab);
        Assert.Empty(review.Conflicts);
    }

    /// <summary>The focused file can be settled from here exactly as a scanned one can.</summary>
    [Fact]
    public async Task AFocusedConflictCanBeSettledInPlace()
    {
        var (review, store, _, provider) = Create();
        provider.Data[A.FullPath] = Conflicting();
        review.FocusConflict = A;

        await review.InitialiseAsync();
        review.ResolveCommand.Execute("KeepEmbedded");

        Assert.Empty(review.Conflicts);
        Assert.True(store.HasChanges(A.FullPath));
    }

    [Fact]
    public async Task CheckListsOnlyTheFilesWhoseLayersDisagree()
    {
        var (review, _, _, provider) = Create();
        provider.Data[A.FullPath] = Conflicting();
        provider.Data[B.FullPath] = new MediaMetadata
        {
            Embedded = new EditableMetadata { Title = "Same" },
            Sidecar = new EditableMetadata { Title = "Same" },
            SidecarPath = "/ws/2025/b.xmp"
        };

        await review.CheckConflictsCommand.ExecuteAsync(null);

        var only = Assert.Single(review.Conflicts);
        Assert.Equal(A.FullPath, only.Path);
        Assert.Equal("title", only.Summary);
        Assert.True(review.HasChecked);
        Assert.Equal("1 file in conflict", review.ConflictHeading);
    }

    /// <summary>
    /// Settling records a pending edit whose baseline is what the file currently reads as — the
    /// same edit the inspector would have recorded — and moves the file out of the conflicts.
    /// </summary>
    [Fact]
    public async Task ResolvingRecordsAPendingChangeAndLeavesTheConflicts()
    {
        var (review, store, _, provider) = Create();
        provider.Data[A.FullPath] = Conflicting();

        await review.CheckConflictsCommand.ExecuteAsync(null);
        review.ResolveCommand.Execute("KeepEmbedded");

        Assert.Empty(review.Conflicts);
        Assert.Equal("No conflicts", review.ConflictHeading);

        var edited = store.GetEdited(A.FullPath);
        Assert.NotNull(edited);
        Assert.Equal("In the file", edited.Title);

        // Effective was the sidecar's title over the file's rating; the review shows the move away from it.
        review.Load();
        Assert.Equal("title", review.Files.Single().Summary);
    }

    [Fact]
    public void ANotYetCheckedTabSaysSo()
    {
        var (review, _, _, _) = Create();

        Assert.False(review.HasChecked);
        Assert.Equal("Not checked yet", review.ConflictHeading);
        Assert.Equal("Check 3 files", review.CheckLabel);
    }
}
