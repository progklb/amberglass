using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Amberglass.Core.Services;

/// <summary>
/// The embed queue, kept on disk as one path per line so a request outlives the session the way
/// pending edits do. Rewritten whole on every change: it holds a handful of paths, not thousands.
/// </summary>
public sealed class EmbedQueue : IEmbedQueue
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string? _file;
    private readonly ILogger _logger;

    /// <param name="paths">Where to keep the queue. Without it the queue is memory-only, which is what a test wants.</param>
    public EmbedQueue(IAppPaths? paths = null, ILogger<EmbedQueue>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _file = paths is null ? null : Path.Combine(paths.AppDataRoot, "embed-queue.txt");

        foreach (var path in Restore())
        {
            _paths.Add(path);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _paths.Count;
            }
        }
    }

    public event EventHandler? Changed;

    public bool Contains(string filePath)
    {
        lock (_gate)
        {
            return _paths.Contains(filePath);
        }
    }

    public void Add(string filePath)
    {
        lock (_gate)
        {
            if (!_paths.Add(filePath))
            {
                return;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string filePath)
    {
        lock (_gate)
        {
            if (!_paths.Remove(filePath))
            {
                return;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyCollection<string> GetAll()
    {
        lock (_gate)
        {
            return _paths.ToList();
        }
    }

    private IEnumerable<string> Restore()
    {
        if (_file is null)
        {
            return [];
        }

        try
        {
            return File.Exists(_file)
                ? File.ReadAllLines(_file).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the queue only means asking again, so it must never stop the application.
            _logger.LogWarning(ex, "Could not read the embed queue at {Path}", _file);
            return [];
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllLines(_file, _paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the embed queue to {Path}", _file);
        }
    }
}
