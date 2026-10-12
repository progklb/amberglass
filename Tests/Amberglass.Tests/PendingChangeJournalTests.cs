using System.Collections.Immutable;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// The working tree is the one thing here that cannot be rebuilt from the files, and it now outlives
/// the process. These are about what survives, and what a half-written journal does.
/// </summary>
public class PendingChangeJournalTests
{
    private static readonly EditableMetadata Was = new() { Rating = 1, Keywords = ["sand"] };

    private static readonly EditableMetadata Now = new()
    {
        Rating = 4,
        Keywords = ["sand", "dust"],
        Title = "Dune, late",
        Flag = MediaFlag.Accepted,
        Label = "Select"
    };

    private static PendingChangeJournal JournalIn(TempFolder temp)
        => new(new TestPaths(temp.Path), NullLogger<PendingChangeJournal>.Instance);

    [Fact]
    public void Edits_come_back_after_the_process_does_not()
    {
        using var temp = new TempFolder();

        using (var journal = JournalIn(temp))
        {
            var store = new PendingChangeStore(journal);
            store.Set("/ws/a.jpg", Was, Now);
            store.Set("/ws/b.jpg", Was, Now with { Rating = 2 });

            // No clean shutdown: the journal is simply abandoned, as a crash would.
        }

        using var reopened = JournalIn(temp);
        var restored = new PendingChangeStore(reopened);

        Assert.Equal(2, restored.Count);

        var a = restored.GetEdited("/ws/a.jpg");
        Assert.NotNull(a);
        Assert.Equal(4, a.Rating);
        Assert.Equal("Dune, late", a.Title);
        Assert.Equal(MediaFlag.Accepted, a.Flag);
        Assert.Equal("Select", a.Label);
        Assert.Equal(["dust", "sand"], a.Keywords.OrderBy(k => k).ToArray());

        Assert.Equal(2, restored.GetEdited("/ws/b.jpg")?.Rating);
    }

    /// <summary>Both sides are kept, because the review shows the edit as was → now.</summary>
    [Fact]
    public void The_value_a_change_started_from_survives_too()
    {
        using var temp = new TempFolder();

        using (var journal = JournalIn(temp))
        {
            new PendingChangeStore(journal).Set("/ws/a.jpg", Was, Now);
        }

        using var reopened = JournalIn(temp);
        var change = Assert.Single(new PendingChangeStore(reopened).GetAll());

        Assert.Equal(1, change.Original.Rating);
        Assert.Equal(["sand"], change.Original.Keywords.ToArray());
    }

    [Fact]
    public void A_discarded_edit_does_not_come_back()
    {
        using var temp = new TempFolder();

        using (var journal = JournalIn(temp))
        {
            var store = new PendingChangeStore(journal);
            store.Set("/ws/a.jpg", Was, Now);
            store.Set("/ws/b.jpg", Was, Now);
            store.Discard("/ws/a.jpg");
        }

        using var reopened = JournalIn(temp);
        var restored = new PendingChangeStore(reopened);

        Assert.False(restored.HasChanges("/ws/a.jpg"));
        Assert.True(restored.HasChanges("/ws/b.jpg"));
    }

    [Fact]
    public void Writing_everything_leaves_nothing_to_restore()
    {
        using var temp = new TempFolder();

        using (var journal = JournalIn(temp))
        {
            var store = new PendingChangeStore(journal);
            store.Set("/ws/a.jpg", Was, Now);
            store.DiscardAll();
        }

        using var reopened = JournalIn(temp);
        Assert.Equal(0, new PendingChangeStore(reopened).Count);
    }

    /// <summary>
    /// Editing a field and putting it back drops the entry, and that has to reach the journal too —
    /// otherwise the edit returns from the dead next time the application opens.
    /// </summary>
    [Fact]
    public void An_edit_undone_by_hand_does_not_come_back_either()
    {
        using var temp = new TempFolder();

        using (var journal = JournalIn(temp))
        {
            var store = new PendingChangeStore(journal);
            store.Set("/ws/a.jpg", Was, Now);
            store.Set("/ws/a.jpg", Was, Was);
        }

        using var reopened = JournalIn(temp);
        Assert.Equal(0, new PendingChangeStore(reopened).Count);
    }

    /// <summary>
    /// The shape an interrupted write actually takes. Everything before the torn line is still good,
    /// which is the entire reason for a line per edit.
    /// </summary>
    [Fact]
    public void A_half_written_last_line_costs_only_that_line()
    {
        using var temp = new TempFolder();

        using (var journal = JournalIn(temp))
        {
            var store = new PendingChangeStore(journal);
            store.Set("/ws/a.jpg", Was, Now);
            store.Set("/ws/b.jpg", Was, Now);
        }

        var path = Path.Combine(temp.Path, "pending-changes.jsonl");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        File.WriteAllText(path, lines[0] + Environment.NewLine + lines[1][..(lines[1].Length / 2)]);

        using var reopened = JournalIn(temp);
        var restored = new PendingChangeStore(reopened);

        Assert.Equal(1, restored.Count);
        Assert.True(restored.HasChanges("/ws/a.jpg"));
    }

    /// <summary>
    /// A long session rewrites the same file over and over. Restoring compacts, so the journal is
    /// bounded by what is actually pending rather than by how much editing has been done.
    /// </summary>
    [Fact]
    public void Restoring_compacts_the_journal()
    {
        using var temp = new TempFolder();

        using (var journal = JournalIn(temp))
        {
            var store = new PendingChangeStore(journal);
            for (var rating = 1; rating <= 5; rating++)
            {
                store.Set("/ws/a.jpg", Was, Now with { Rating = rating });
            }
        }

        var path = Path.Combine(temp.Path, "pending-changes.jsonl");
        Assert.Equal(5, File.ReadAllLines(path).Length);

        using var reopened = JournalIn(temp);
        var restored = new PendingChangeStore(reopened);

        Assert.Single(File.ReadAllLines(path));
        Assert.Equal(5, restored.GetEdited("/ws/a.jpg")?.Rating);
    }

    [Fact]
    public void An_absent_journal_is_simply_an_empty_working_tree()
    {
        using var temp = new TempFolder();
        using var journal = JournalIn(temp);

        Assert.Equal(0, new PendingChangeStore(journal).Count);
    }

    /// <summary>Without a journal the store behaves exactly as it always did.</summary>
    [Fact]
    public void A_store_with_no_journal_keeps_everything_in_memory()
    {
        var store = new PendingChangeStore();
        store.Set("/ws/a.jpg", Was, Now);

        Assert.Equal(1, store.Count);
    }
}
