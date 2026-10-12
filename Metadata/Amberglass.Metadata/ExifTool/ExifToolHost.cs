using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Metadata.ExifTool;

/// <summary>
/// Owns the single long-lived ExifTool process shared by the reader and the writer. Registered as a
/// singleton so that reading and writing never spawn competing processes.
/// </summary>
public sealed class ExifToolHost : IAsyncDisposable
{
    private readonly Lazy<ExifToolSession?> _session;

    public ExifToolHost(IExifToolLocator locator, ILogger<ExifToolHost> logger)
    {
        IsAvailable = locator.IsAvailable;
        _session = new Lazy<ExifToolSession?>(() =>
            locator.ExifToolPath is { } path ? new ExifToolSession(path, logger) : null);
    }

    public bool IsAvailable { get; }

    /// <summary>The session, or null when ExifTool is not installed.</summary>
    public ExifToolSession? Session => _session.Value;

    public async ValueTask DisposeAsync()
    {
        if (_session.IsValueCreated && _session.Value is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
