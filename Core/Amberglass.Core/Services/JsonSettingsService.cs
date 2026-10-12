using System.Text.Json;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Core.Services;

/// <summary>
/// Settings stored as JSON next to — but deliberately <b>not inside</b> — the cache directory, so
/// clearing or relocating the cache never discards preferences.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonSettingsService(ILogger<JsonSettingsService> logger)
        : this(GetDefaultPath(), logger)
    {
    }

    public JsonSettingsService(string filePath, ILogger<JsonSettingsService> logger)
    {
        _filePath = filePath;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public static string GetDefaultPath()
        => Path.Combine(AppPaths.GetAppDataRoot(), "settings.json");

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            // Write-then-move so an interrupted save cannot leave a half-written settings file that
            // fails to parse on next launch.
            var temporary = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, SerializerOptions), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, _filePath, overwrite: true);

            Current = settings;
        }
        finally
        {
            _writeLock.Release();
        }

        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppSettings.Default;
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath)) ?? AppSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings must not stop the application starting.
            _logger.LogWarning(ex, "Could not read settings from {Path}; using defaults", _filePath);
            return AppSettings.Default;
        }
    }
}
