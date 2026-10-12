using System.Text.Json;
using System.Text.Json.Serialization;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

/// <summary>
/// Keeps the pending edits on disk so a session's work survives the application not being closed
/// politely.
///
/// The working tree is the point of this application: a photographer makes hundreds of judgements
/// and flushes them when it suits them, which means the staging area may hold hours of irreplaceable
/// work for days. Held only in memory, a crash, a kill or an overnight restart took all of it, and
/// the close guard covers none of those — it covers quitting.
///
/// A line per edit, appended, like <see cref="SyncJournal"/> and for the same reason: appending one
/// line is about as close to atomic as a filesystem gets, so an interruption leaves a readable
/// journal rather than a half-rewritten document. It lives in the application's data directory and
/// not in the cache, because the cache is disposable by design and this is the one thing here that
/// cannot be rebuilt from the files.
///
/// The stream is kept open and flushed after every line. That survives the process dying, which is
/// the case worth defending against; a power cut can still lose the last line, and buying that back
/// would mean an fsync per keystroke.
/// </summary>
public sealed class PendingChangeJournal : IDisposable
{
    private static readonly JsonSerializerOptions Format = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private StreamWriter? _writer;
    private bool _disabled;

    public PendingChangeJournal(IAppPaths paths, ILogger<PendingChangeJournal> logger)
    {
        _path = Path.Combine(paths.AppDataRoot, "pending-changes.jsonl");
        _logger = logger;
    }

    private sealed record Entry(
        [property: JsonPropertyName("op")] string Operation,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("was")] EditableMetadata? Original = null,
        [property: JsonPropertyName("now")] EditableMetadata? Edited = null);

    private const string SetOperation = "set";
    private const string DiscardOperation = "del";

    /// <summary>
    /// Replays the journal, then rewrites it holding only what survived.
    ///
    /// Compacting here rather than on a threshold means the file can only grow by one session's
    /// worth of edits, and the moment to do it is the one moment nothing else is writing.
    /// </summary>
    public IReadOnlyList<PendingChange> Restore()
    {
        var live = new Dictionary<string, PendingChange>(StringComparer.Ordinal);

        try
        {
            if (File.Exists(_path))
            {
                foreach (var line in File.ReadLines(_path))
                {
                    Apply(line, live);
                }
            }
        }
        catch (Exception ex)
        {
            // A damaged journal must not stop the application opening. Whatever was readable is
            // kept; the rest is gone either way, and saying so is better than failing to start.
            _logger.LogError(ex, "Could not fully read the pending-change journal at {Path}", _path);
        }

        if (live.Count > 0)
        {
            _logger.LogInformation("Restored {Count} pending change(s) from the journal", live.Count);
        }

        Compact(live.Values);
        return live.Values.ToList();
    }

    private void Apply(string line, Dictionary<string, PendingChange> live)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            if (JsonSerializer.Deserialize<Entry>(line, Format) is not { } entry)
            {
                return;
            }

            if (entry.Operation == DiscardOperation)
            {
                live.Remove(entry.Path);
            }
            else if (entry is { Original: { } original, Edited: { } edited })
            {
                live[entry.Path] = new PendingChange(entry.Path, original, edited);
            }
        }
        catch (JsonException ex)
        {
            // One torn line at the end is the expected shape of an interrupted write. The lines
            // before it are still good, which is the whole reason for a line-per-edit format.
            _logger.LogWarning(ex, "Skipping an unreadable line in the pending-change journal");
        }
    }

    public void RecordSet(PendingChange change)
        => Append(new Entry(SetOperation, change.FilePath, change.Original, change.Edited));

    public void RecordDiscard(string filePath)
        => Append(new Entry(DiscardOperation, filePath));

    private void Append(Entry entry)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                _writer ??= Open();
                _writer.WriteLine(JsonSerializer.Serialize(entry, Format));
                _writer.Flush();
            }
        }
        catch (Exception ex)
        {
            // Journalling is a safety net, not the feature. If it cannot be written the edits still
            // work for this session; stopping would turn a lost-work risk into lost work now.
            _logger.LogError(ex, "Could not record a pending change; the journal is now off for this session");
            _disabled = true;
        }
    }

    /// <summary>Rewrites the journal so it holds exactly <paramref name="live"/> and nothing else.</summary>
    public void Compact(IReadOnlyCollection<PendingChange> live)
    {
        try
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;

                if (live.Count == 0)
                {
                    if (File.Exists(_path))
                    {
                        File.Delete(_path);
                    }

                    return;
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

                // Written beside and moved into place, so an interruption leaves the old journal
                // rather than a half-written one.
                var staging = _path + ".new";
                using (var writer = new StreamWriter(staging, append: false))
                {
                    foreach (var change in live)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(
                            new Entry(SetOperation, change.FilePath, change.Original, change.Edited), Format));
                    }
                }

                File.Move(staging, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not compact the pending-change journal at {Path}", _path);
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        return new StreamWriter(_path, append: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
