using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Amberglass.UI.Services;

namespace Amberglass.Tests;

public class BatchMetadataEditTests
{
    private static readonly EditableMetadata Original = new()
    {
        Title = "Original title",
        Headline = "Original headline",
        Description = "Original description",
        Rating = 2,
        Label = "Green",
        Creator = "Kevin",
        Copyright = "(c) Kevin",
        Keywords = ["wildlife", "Namibia"]
    };

    [Fact]
    public void An_empty_edit_changes_nothing()
    {
        var edit = new BatchMetadataEdit();

        Assert.False(edit.HasAnyChange);
        Assert.True(edit.ApplyTo(Original).ValueEquals(Original));
    }

    [Fact]
    public void An_unticked_field_is_left_alone_even_when_a_value_is_present()
    {
        // The safety property that matters most: typing in a box without ticking it must not wipe
        // that field across the whole selection.
        var edit = new BatchMetadataEdit { Title = "Ignored", ApplyTitle = false };

        Assert.Equal("Original title", edit.ApplyTo(Original).Title);
    }

    [Fact]
    public void A_ticked_field_is_applied()
    {
        var edit = new BatchMetadataEdit { ApplyTitle = true, Title = "New title" };

        var result = edit.ApplyTo(Original);

        Assert.Equal("New title", result.Title);

        // Untouched fields survive.
        Assert.Equal("Original headline", result.Headline);
        Assert.Equal(2, result.Rating);
    }

    [Fact]
    public void A_ticked_field_with_a_blank_value_clears_it()
    {
        // Explicitly ticking and clearing is the only way to blank a field.
        var edit = new BatchMetadataEdit { ApplyCopyright = true, Copyright = "  " };

        Assert.Null(edit.ApplyTo(Original).Copyright);
    }

    [Fact]
    public void Rating_can_be_set_and_cleared()
    {
        Assert.Equal(5, new BatchMetadataEdit { ApplyRating = true, Rating = 5 }.ApplyTo(Original).Rating);
        Assert.Null(new BatchMetadataEdit { ApplyRating = true, Rating = null }.ApplyTo(Original).Rating);
    }

    [Fact]
    public void Keywords_are_added_without_disturbing_existing_ones()
    {
        var edit = new BatchMetadataEdit { KeywordsToAdd = ["lioness"] };

        Assert.Equal(["wildlife", "Namibia", "lioness"], edit.ApplyTo(Original).Keywords.ToArray());
    }

    [Fact]
    public void Adding_a_keyword_that_already_exists_does_not_duplicate_it()
    {
        var edit = new BatchMetadataEdit { KeywordsToAdd = ["WILDLIFE"] };

        Assert.Equal(["wildlife", "Namibia"], edit.ApplyTo(Original).Keywords.ToArray());
    }

    [Fact]
    public void Keywords_are_removed_case_insensitively()
    {
        var edit = new BatchMetadataEdit { KeywordsToRemove = ["NAMIBIA"] };

        Assert.Equal(["wildlife"], edit.ApplyTo(Original).Keywords.ToArray());
    }

    [Fact]
    public void Adding_and_removing_in_one_edit_both_take_effect()
    {
        var edit = new BatchMetadataEdit
        {
            KeywordsToAdd = ["lioness"],
            KeywordsToRemove = ["Namibia"]
        };

        Assert.Equal(["wildlife", "lioness"], edit.ApplyTo(Original).Keywords.ToArray());
    }

    [Fact]
    public void Replacing_keywords_discards_the_existing_list()
    {
        var edit = new BatchMetadataEdit
        {
            ReplaceKeywords = true,
            ReplacementKeywords = ["studio", "product"]
        };

        Assert.Equal(["studio", "product"], edit.ApplyTo(Original).Keywords.ToArray());
    }

    [Fact]
    public void Replacing_with_nothing_clears_the_keywords()
    {
        var edit = new BatchMetadataEdit { ReplaceKeywords = true, ReplacementKeywords = [] };

        Assert.Empty(edit.ApplyTo(Original).Keywords);
    }

    [Fact]
    public void Replacement_keywords_are_deduplicated()
    {
        var edit = new BatchMetadataEdit
        {
            ReplaceKeywords = true,
            ReplacementKeywords = ["studio", "STUDIO", "  ", "product"]
        };

        Assert.Equal(["studio", "product"], edit.ApplyTo(Original).Keywords.ToArray());
    }

    [Fact]
    public void Applying_to_a_file_with_no_keywords_works()
    {
        var edit = new BatchMetadataEdit { KeywordsToAdd = ["new"] };

        Assert.Equal(["new"], edit.ApplyTo(EditableMetadata.Empty).Keywords.ToArray());
    }

    [Fact]
    public void Has_any_change_reflects_every_kind_of_edit()
    {
        Assert.True(new BatchMetadataEdit { ApplyRating = true }.HasAnyChange);
        Assert.True(new BatchMetadataEdit { KeywordsToAdd = ["x"] }.HasAnyChange);
        Assert.True(new BatchMetadataEdit { KeywordsToRemove = ["x"] }.HasAnyChange);
        Assert.True(new BatchMetadataEdit { ReplaceKeywords = true }.HasAnyChange);
        Assert.False(new BatchMetadataEdit { Title = "typed but not ticked" }.HasAnyChange);
    }
}

public class BatchMetadataServiceTests
{
    private sealed class StubProvider : IMetadataProvider
    {
        public Dictionary<string, MediaMetadata> Data { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

        public int ReadManyCalls { get; private set; }

        public bool IsAvailable => true;

        public Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult(Data.GetValueOrDefault(file.FullPath));

        public Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
            IReadOnlyList<MediaFile> files,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ReadManyCalls++;
            cancellationToken.ThrowIfCancellationRequested();

            var result = new Dictionary<string, MediaMetadata>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (!Unreadable.Contains(file.FullPath) && Data.TryGetValue(file.FullPath, out var metadata))
                {
                    result[file.FullPath] = metadata;
                }
            }

            progress?.Report(files.Count);
            return Task.FromResult<IReadOnlyDictionary<string, MediaMetadata>>(result);
        }
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

    private static (BatchMetadataService Service, StubProvider Provider, PendingChangeStore Store) Create(
        params (string Path, EditableMetadata Metadata)[] files)
    {
        var provider = new StubProvider();
        foreach (var (path, metadata) in files)
        {
            provider.Data[path] = new MediaMetadata { Embedded = metadata };
        }

        var store = new PendingChangeStore();
        var service = new BatchMetadataService(provider, store, NullLogger<BatchMetadataService>.Instance);
        return (service, provider, store);
    }

    [Fact]
    public async Task Applies_an_edit_to_every_file()
    {
        var (service, _, store) = Create(
            ("/a.jpg", new EditableMetadata { Rating = 1 }),
            ("/b.jpg", new EditableMetadata { Rating = 2 }),
            ("/c.jpg", new EditableMetadata { Rating = 3 }));

        var result = await service.ApplyAsync(
            [FileAt("/a.jpg"), FileAt("/b.jpg"), FileAt("/c.jpg")],
            new BatchMetadataEdit { ApplyRating = true, Rating = 5 });

        Assert.Equal(3, result.Changed);
        Assert.Equal(3, store.Count);
        Assert.All(new[] { "/a.jpg", "/b.jpg", "/c.jpg" }, p => Assert.Equal(5, store.GetEdited(p)!.Rating));
    }

    [Fact]
    public async Task Files_that_already_match_are_counted_as_unchanged()
    {
        var (service, _, store) = Create(
            ("/a.jpg", new EditableMetadata { Rating = 5 }),
            ("/b.jpg", new EditableMetadata { Rating = 1 }));

        var result = await service.ApplyAsync(
            [FileAt("/a.jpg"), FileAt("/b.jpg")],
            new BatchMetadataEdit { ApplyRating = true, Rating = 5 });

        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Unchanged);

        // No pending change for the file that already had the value.
        Assert.False(store.HasChanges("/a.jpg"));
        Assert.True(store.HasChanges("/b.jpg"));
    }

    [Fact]
    public async Task Nothing_is_written_to_disk()
    {
        var (service, _, store) = Create(("/a.jpg", EditableMetadata.Empty));

        await service.ApplyAsync([FileAt("/a.jpg")], new BatchMetadataEdit { ApplyRating = true, Rating = 4 });

        // Batch editing must go through the same pending-change workflow as single-file editing.
        Assert.Equal(1, store.Count);
        Assert.Equal(4, store.GetEdited("/a.jpg")!.Rating);
    }

    [Fact]
    public async Task Successive_batches_compose_rather_than_overwriting()
    {
        var (service, _, store) = Create(("/a.jpg", new EditableMetadata { Keywords = ["existing"] }));
        var files = new[] { FileAt("/a.jpg") };

        await service.ApplyAsync(files, new BatchMetadataEdit { KeywordsToAdd = ["first"] });
        await service.ApplyAsync(files, new BatchMetadataEdit { KeywordsToAdd = ["second"] });

        // The second run must build on the first, not on disk alone.
        Assert.Equal(["existing", "first", "second"], store.GetEdited("/a.jpg")!.Keywords.ToArray());
    }

    [Fact]
    public async Task An_unreadable_file_is_reported_rather_than_guessed_at()
    {
        var (service, provider, store) = Create(
            ("/a.jpg", EditableMetadata.Empty),
            ("/b.jpg", EditableMetadata.Empty));
        provider.Unreadable.Add("/b.jpg");

        var result = await service.ApplyAsync(
            [FileAt("/a.jpg"), FileAt("/b.jpg")],
            new BatchMetadataEdit { ApplyRating = true, Rating = 3 });

        Assert.Equal(1, result.Changed);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("/b.jpg", failure.FilePath);

        // Without a trustworthy baseline, no pending change is recorded for that file.
        Assert.False(store.HasChanges("/b.jpg"));
    }

    [Fact]
    public async Task One_failure_does_not_abandon_the_rest()
    {
        var (service, provider, _) = Create(
            ("/a.jpg", EditableMetadata.Empty),
            ("/b.jpg", EditableMetadata.Empty),
            ("/c.jpg", EditableMetadata.Empty));
        provider.Unreadable.Add("/b.jpg");

        var result = await service.ApplyAsync(
            [FileAt("/a.jpg"), FileAt("/b.jpg"), FileAt("/c.jpg")],
            new BatchMetadataEdit { ApplyRating = true, Rating = 3 });

        Assert.Equal(2, result.Changed);
        Assert.Single(result.Failures);
        Assert.Equal(3, result.Total);
    }

    [Fact]
    public async Task Reports_progress_across_the_selection()
    {
        var (service, _, _) = Create(
            ("/a.jpg", EditableMetadata.Empty),
            ("/b.jpg", EditableMetadata.Empty));

        var reports = new List<JobProgress>();
        await service.ApplyAsync(
            [FileAt("/a.jpg"), FileAt("/b.jpg")],
            new BatchMetadataEdit { ApplyRating = true, Rating = 1 },
            new Progress<JobProgress>(reports.Add));

        await Task.Delay(50); // Progress<T> posts through the sync context

        Assert.NotEmpty(reports);
        Assert.Equal(2, reports.Max(r => r.Total));
        Assert.Equal(1, reports.Max(r => r.Fraction));
    }

    [Fact]
    public async Task Cancelling_stops_the_run_and_says_so()
    {
        var (service, _, store) = Create(
            ("/a.jpg", EditableMetadata.Empty),
            ("/b.jpg", EditableMetadata.Empty));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await service.ApplyAsync(
            [FileAt("/a.jpg"), FileAt("/b.jpg")],
            new BatchMetadataEdit { ApplyRating = true, Rating = 1 },
            cancellationToken: cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task An_edit_with_no_changes_does_no_work()
    {
        var (service, provider, store) = Create(("/a.jpg", EditableMetadata.Empty));

        var result = await service.ApplyAsync([FileAt("/a.jpg")], new BatchMetadataEdit());

        Assert.Equal(0, result.Total);
        Assert.Equal(0, store.Count);

        // Not even a read should happen for an empty edit.
        Assert.Equal(0, provider.ReadManyCalls);
    }

    [Fact]
    public async Task Reading_happens_in_one_batched_call_not_one_per_file()
    {
        var files = Enumerable.Range(0, 50).Select(i => ($"/f{i}.jpg", EditableMetadata.Empty)).ToArray();
        var (service, provider, _) = Create(files);

        await service.ApplyAsync(
            files.Select(f => FileAt(f.Item1)).ToList(),
            new BatchMetadataEdit { ApplyRating = true, Rating = 2 });

        // 50 files, one read call — this is what makes a large selection usable.
        Assert.Equal(1, provider.ReadManyCalls);
    }
}

public class BatchEditViewModelTests
{
    private sealed class NoopBatchService : IBatchMetadataService
    {
        public Task<BatchResult> ApplyAsync(
            IReadOnlyList<MediaFile> files,
            BatchMetadataEdit edit,
            IProgress<JobProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchResult(files.Count, 0, [], false));
    }

    private static Amberglass.UI.ViewModels.BatchEditViewModel Create()
        => new(new NoopBatchService(), new KeywordClipboard(), NullLogger<Amberglass.UI.ViewModels.BatchEditViewModel>.Instance);

    [Fact]
    public void Clicking_a_star_opts_the_rating_in()
    {
        var vm = Create();

        // The rating stars used to be disabled until the checkbox was ticked, and clicking a star
        // was the only thing that ticked it — so the field could never be reached at all.
        vm.SetRatingCommand.Execute("4");

        Assert.True(vm.ApplyRating);
        Assert.Equal(4, vm.Rating);
        Assert.True(vm.BuildEdit().HasAnyChange);
    }

    [Fact]
    public void Typing_in_a_field_opts_it_in()
    {
        var vm = Create();

        vm.Title = "Kalahari";

        Assert.True(vm.ApplyTitle);
        Assert.Equal("Kalahari", vm.BuildEdit().Title);
    }

    [Fact]
    public void Clearing_a_field_after_ticking_keeps_it_opted_in()
    {
        var vm = Create();
        vm.Creator = "Kevin";
        Assert.True(vm.ApplyCreator);

        // Deliberately blanking a field across a selection must stay possible.
        vm.Creator = string.Empty;

        Assert.True(vm.ApplyCreator);
        Assert.True(vm.BuildEdit().ApplyCreator);
    }

    [Fact]
    public void Untouched_fields_stay_out_of_the_edit()
    {
        var vm = Create();
        vm.Title = "Only this";

        var edit = vm.BuildEdit();

        Assert.True(edit.ApplyTitle);
        Assert.False(edit.ApplyHeadline);
        Assert.False(edit.ApplyRating);
        Assert.False(edit.ApplyCopyright);
    }

    [Fact]
    public void Replace_mode_turns_the_add_list_into_the_replacement_list()
    {
        var vm = Create();
        vm.NewKeywordToAdd = "studio, product";
        vm.AddKeywordToAddCommand.Execute(null);
        vm.ReplaceKeywords = true;

        var edit = vm.BuildEdit();

        Assert.True(edit.ReplaceKeywords);
        Assert.Equal(["studio", "product"], edit.ReplacementKeywords.ToArray());
        Assert.Empty(edit.KeywordsToAdd);
    }

    [Fact]
    public void Clear_resets_every_opt_in()
    {
        var vm = Create();
        vm.Title = "x";
        vm.SetRatingCommand.Execute("3");
        vm.NewKeywordToAdd = "kw";
        vm.AddKeywordToAddCommand.Execute(null);

        vm.ClearFormCommand.Execute(null);

        Assert.False(vm.BuildEdit().HasAnyChange);
        Assert.Empty(vm.KeywordsToAdd);
    }
}

/// <summary>
/// Copying a set of keywords from one photograph and applying it to a selection.
/// </summary>
public class KeywordClipboardTests
{
    [Fact]
    public void Copying_keeps_what_was_copied()
    {
        var clipboard = new KeywordClipboard();

        clipboard.Copy(["wide", "Golden Hour"]);

        Assert.True(clipboard.HasKeywords);
        Assert.Equal(["wide", "Golden Hour"], clipboard.Keywords.ToArray());
    }

    /// <summary>Blank entries and repeats would become blank and repeated tags on every file.</summary>
    [Fact]
    public void Copying_tidies_what_it_is_given()
    {
        var clipboard = new KeywordClipboard();

        clipboard.Copy([" wide ", "", "   ", "WIDE", "sand"]);

        Assert.Equal(["wide", "sand"], clipboard.Keywords.ToArray());
    }

    [Fact]
    public void An_empty_copy_leaves_nothing_to_paste()
    {
        var clipboard = new KeywordClipboard();
        clipboard.Copy(["wide"]);

        clipboard.Copy([]);

        Assert.False(clipboard.HasKeywords);
    }

    [Fact]
    public void Copying_announces_itself()
    {
        var clipboard = new KeywordClipboard();
        var raised = 0;
        clipboard.Changed += (_, _) => raised++;

        clipboard.Copy(["wide"]);
        clipboard.Clear();

        Assert.Equal(2, raised);
    }

    // ---- Pasting ---------------------------------------------------------------------------------

    [Fact]
    public void Nothing_copied_means_nothing_to_paste()
    {
        var (batch, _) = CreateBatch();

        Assert.False(batch.CanPasteKeywords);
    }

    [Fact]
    public void Nothing_selected_means_nothing_to_paste()
    {
        var clipboard = new KeywordClipboard();
        clipboard.Copy(["wide"]);

        var batch = new Amberglass.UI.ViewModels.BatchEditViewModel(
            new RecordingBatchService(), clipboard,
            NullLogger<Amberglass.UI.ViewModels.BatchEditViewModel>.Instance);

        Assert.False(batch.CanPasteKeywords);
    }

    /// <summary>
    /// Added, not replacing. "Give these the same tags as that one" is what copying keywords means
    /// nearly always, and a paste that discarded what was already there would be noticed a hundred
    /// files later.
    /// </summary>
    [Fact]
    public async Task Pasting_adds_the_copied_keywords_to_every_selected_file()
    {
        var (batch, service) = CreateBatch("wide", "sand");
        batch.SetSelection([Item("/library/a.jpg"), Item("/library/b.jpg")]);

        Assert.True(batch.CanPasteKeywords);

        await batch.PasteKeywordsCommand.ExecuteAsync(null);

        Assert.Equal(2, service.Files.Count);
        Assert.Equal(["wide", "sand"], service.Edit!.KeywordsToAdd.ToArray());
        Assert.False(service.Edit.ReplaceKeywords);
        Assert.Empty(service.Edit.KeywordsToRemove);
    }

    [Fact]
    public async Task Pasting_says_how_many_keywords_it_applied()
    {
        var (batch, _) = CreateBatch("wide", "sand");
        batch.SetSelection([Item("/library/a.jpg")]);

        await batch.PasteKeywordsCommand.ExecuteAsync(null);

        Assert.Contains("2 keyword(s)", batch.ResultMessage);
    }

    /// <summary>The menu label has to say what will be pasted, or it is a leap of faith.</summary>
    [Fact]
    public void The_paste_label_names_the_keywords()
    {
        var (batch, _) = CreateBatch("wide", "sand");

        Assert.Equal("Paste wide, sand", batch.PasteKeywordsSummary);
    }

    [Fact]
    public void The_paste_label_says_so_when_nothing_is_copied()
    {
        var (batch, _) = CreateBatch();

        Assert.Equal("Nothing copied", batch.PasteKeywordsSummary);
    }

    private static (Amberglass.UI.ViewModels.BatchEditViewModel Batch, RecordingBatchService Service)
        CreateBatch(params string[] copied)
    {
        var clipboard = new KeywordClipboard();
        if (copied.Length > 0)
        {
            clipboard.Copy(copied);
        }

        var service = new RecordingBatchService();

        return (new Amberglass.UI.ViewModels.BatchEditViewModel(
            service, clipboard, NullLogger<Amberglass.UI.ViewModels.BatchEditViewModel>.Instance), service);
    }

    private static Amberglass.UI.ViewModels.MediaItemViewModel Item(string path)
        => new(
            new MediaFile
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                MediaType = MediaType.Image,
                SizeBytes = 1,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            new NoopThumbnails());

    private sealed class RecordingBatchService : IBatchMetadataService
    {
        public IReadOnlyList<MediaFile> Files { get; private set; } = [];

        public BatchMetadataEdit? Edit { get; private set; }

        public Task<BatchResult> ApplyAsync(
            IReadOnlyList<MediaFile> files,
            BatchMetadataEdit edit,
            IProgress<JobProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Files = files;
            Edit = edit;
            return Task.FromResult(new BatchResult(files.Count, 0, [], false));
        }
    }

    private sealed class NoopThumbnails : IThumbnailService
    {
        public Task<byte[]?> GetThumbnailAsync(
            MediaFile file,
            int maxEdgePixels,
            ThumbnailPriority priority = ThumbnailPriority.Background,
            CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
    }
}
