using System.Text.Json;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

/// <summary>
/// The keyword library as JSON, next to settings and outside the cache — it is the user's own work,
/// not derived data, so clearing the cache must never touch it.
/// </summary>
public sealed class JsonKeywordLibraryService : IKeywordLibraryService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<JsonKeywordLibraryService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonKeywordLibraryService(ILogger<JsonKeywordLibraryService> logger)
        : this(GetDefaultPath(), logger)
    {
    }

    public JsonKeywordLibraryService(string filePath, ILogger<JsonKeywordLibraryService> logger)
    {
        _filePath = filePath;
        _logger = logger;
        Current = Load();
    }

    public KeywordLibrary Current { get; private set; }

    public event EventHandler<KeywordLibrary>? Changed;

    public static string GetDefaultPath()
        => Path.Combine(AppPaths.GetAppDataRoot(), "keywords.json");

    public async Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default)
    {
        // Published before the write, not after.
        //
        // The settings service does it the other way round, and that cost a real bug: a caller that
        // saved and then immediately acted on the new value raced the disk and got the old one about
        // half the time. Nothing here depends on the file having landed — the file is only how the
        // value survives a restart.
        Current = library;
        Changed?.Invoke(this, library);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            // Write-then-move, so an interrupted save cannot leave a half-written file that fails to
            // parse on next launch and silently loses the vocabulary.
            var temporary = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(library, SerializerOptions), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save the keyword library to {Path}", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private KeywordLibrary Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return KeywordLibrary.Empty;
            }

            return JsonSerializer.Deserialize<KeywordLibrary>(File.ReadAllText(_filePath), SerializerOptions)
                   ?? KeywordLibrary.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable library is not worth failing to start over; it is rebuilt or re-imported.
            _logger.LogWarning(ex, "Could not read the keyword library at {Path}", _filePath);
            return KeywordLibrary.Empty;
        }
    }
}
