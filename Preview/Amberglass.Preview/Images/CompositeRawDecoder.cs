using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Images;

/// <summary>
/// Tries each RAW decoder in turn.
///
/// No single decoder covers the formats a photographer actually has. LibRaw cannot unpack a JPEG XL
/// compressed DNG unless it was built against libjxl, which the usual packages are not; macOS
/// ImageIO cannot decode plenty of current cameras, including the Fujifilm X-S20. Between them the
/// coverage is good, and a file only falls back to its embedded preview when both fail.
///
/// Order matters: LibRaw first, because it is the one that answers to the develop settings. ImageIO
/// renders the file its own way, which is much better than a thumbnail but is not adjustable.
/// </summary>
public sealed class CompositeRawDecoder : IRawDecoder
{
    private readonly IReadOnlyList<IRawDecoder> _decoders;
    private readonly ILogger<CompositeRawDecoder> _logger;

    public CompositeRawDecoder(IEnumerable<IRawDecoder> decoders, ILogger<CompositeRawDecoder> logger)
    {
        _decoders = decoders.Where(d => d.IsAvailable).ToList();
        _logger = logger;
    }

    public bool IsAvailable => _decoders.Count > 0;

    public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        => FirstAsync(file, (decoder, token) => decoder.DevelopAsync(file, token), cancellationToken);

    /// <summary>
    /// The first decoder with a draft to offer. One that has no cheaper route than its full develop
    /// declines, so a file only the platform decoder can read gets no draft and waits for the real
    /// thing — which is right, since that decoder ignores the develop settings anyway.
    /// </summary>
    public Task<DecodedImage?> DevelopDraftAsync(MediaFile file, CancellationToken cancellationToken = default)
        => FirstAsync(file, (decoder, token) => decoder.DevelopDraftAsync(file, token), cancellationToken);

    private async Task<DecodedImage?> FirstAsync(
        MediaFile file,
        Func<IRawDecoder, CancellationToken, Task<DecodedImage?>> develop,
        CancellationToken cancellationToken)
    {
        foreach (var decoder in _decoders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await develop(decoder, cancellationToken).ConfigureAwait(false) is { } decoded)
            {
                return decoded;
            }

            _logger.LogDebug(
                "{Decoder} could not develop {File}; trying the next", decoder.GetType().Name, file.FullPath);
        }

        return null;
    }
}
