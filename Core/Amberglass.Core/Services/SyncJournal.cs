using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

/// <summary>
/// Records which files a sync run has finished, so an interrupted run can pick up where it stopped
/// instead of re-writing files it already committed.
///
/// Deliberately a line-per-path append-only text file rather than a serialised document: appending
/// one line is about as close to atomic as a filesystem gets, so a crash — or a pulled power cable —
/// mid-run leaves a readable journal rather than a half-rewritten JSON blob. It lives outside the
/// cache, because losing it would mean redoing work.
/// </summary>
public sealed class SyncJournal
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _writeLock = new();

    public SyncJournal(IAppPaths paths, ILogger logger)
    {
        _path = Path.Combine(paths.AppDataRoot, "sync-journal.txt");
        _logger = logger;
    }

    /// <summary>Paths a previous run committed. Empty when there is nothing to resume.</summary>
    public IReadOnlyList<string> LoadCompleted()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            return File.ReadAllLines(_path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable journal only costs redoing work, so it must never be fatal.
            _logger.LogWarning(ex, "Could not read the sync journal at {Path}", _path);
            return [];
        }
    }

    public void RecordCompleted(string filePath)
    {
        try
        {
            lock (_writeLock)
            {
                File.AppendAllText(_path, filePath + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to journal is not worth failing the write that already succeeded; the worst
            // outcome is that a resumed run repeats this file, which is harmless.
            _logger.LogWarning(ex, "Could not record {File} in the sync journal", filePath);
        }
    }

    public void Clear()
    {
        try
        {
            lock (_writeLock)
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clear the sync journal at {Path}", _path);
        }
    }
}
