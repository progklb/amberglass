using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Microsoft.Extensions.Logging;

namespace Amberglass.Metadata.ExifTool;

public sealed class ExifToolMetadataProvider : IMetadataProvider
{
    /// <summary>Files per ExifTool invocation during a batch read.</summary>
    private const int ReadChunkSize = 100;

    private readonly ExifToolHost _host;
    private readonly ILogger<ExifToolMetadataProvider> _logger;

    public ExifToolMetadataProvider(ExifToolHost host, ILogger<ExifToolMetadataProvider> logger)
    {
        _host = host;
        _logger = logger;
    }

    public bool IsAvailable => _host.IsAvailable;

    public async Task<MediaMetadata?> ReadAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        if (_host.Session is not { } session)
        {
            return null;
        }

        var sidecarPath = XmpSidecar.Find(file.FullPath);

        // Media file and sidecar are read in one round trip rather than two.
        var arguments = new List<string> { "-json", "-G" };
        arguments.Add(file.FullPath);
        if (sidecarPath is not null)
        {
            arguments.Add(sidecarPath);
        }

        string json;
        try
        {
            json = await session.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExifTool failed reading {File}", file.FullPath);
            return null;
        }

        var documents = ParseDocuments(json, file.FullPath);
        if (documents.Count == 0)
        {
            return null;
        }

        var media = FindDocument(documents, file.FullPath) ?? documents[0];
        var sidecar = sidecarPath is null ? null : FindDocument(documents, sidecarPath);

        return Build(file, media, sidecar, sidecarPath);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, MediaMetadata>> ReadManyAsync(
        IReadOnlyList<MediaFile> files,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, MediaMetadata>(StringComparer.Ordinal);

        if (_host.Session is not { } session || files.Count == 0)
        {
            return results;
        }

        var processed = 0;

        // Chunked rather than one giant invocation: a chunk bounds how much is lost to a single
        // failure, and keeps the JSON response a sane size for a thousand-file selection.
        foreach (var chunk in files.Chunk(ReadChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var arguments = new List<string> { "-json", "-G" };
            var sidecars = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var file in chunk)
            {
                arguments.Add(file.FullPath);

                if (XmpSidecar.Find(file.FullPath) is { } sidecarPath)
                {
                    sidecars[file.FullPath] = sidecarPath;
                    arguments.Add(sidecarPath);
                }
            }

            string json;
            try
            {
                json = await session.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ExifTool failed reading a chunk of {Count} files", chunk.Length);
                processed += chunk.Length;
                progress?.Report(processed);
                continue;
            }

            var documents = ParseDocuments(json, "batch read");

            foreach (var file in chunk)
            {
                var media = FindDocument(documents, file.FullPath);
                if (media is null)
                {
                    continue;
                }

                sidecars.TryGetValue(file.FullPath, out var sidecarPath);
                var sidecar = sidecarPath is null ? null : FindDocument(documents, sidecarPath);

                results[file.FullPath] = Build(file, media, sidecar, sidecarPath);
            }

            processed += chunk.Length;
            progress?.Report(processed);
        }

        return results;
    }

    /// <summary>Shared by both read paths so single and batch results cannot drift apart.</summary>
    private static MediaMetadata Build(
        MediaFile file,
        Dictionary<string, JsonElement> media,
        Dictionary<string, JsonElement>? sidecar,
        string? sidecarPath)
        => new()
        {
            Embedded = ReadEditable(media),
            Sidecar = sidecar is null ? null : ReadEditable(sidecar),
            Camera = ReadCamera(media),
            Video = file.MediaType == MediaType.Video ? ReadVideo(media) : VideoInfo.Empty,
            Dimensions = ReadDimensions(media),
            RawTags = ReadRawTags(media, sidecar),
            SidecarPath = sidecar is null ? null : sidecarPath,
            FileMetadataChangedUtc = MetadataChangedAt(media, file.FullPath),
            SidecarMetadataChangedUtc = sidecar is null ? null : MetadataChangedAt(sidecar, sidecarPath)
        };

    /// <summary>
    /// When this document's metadata last changed.
    ///
    /// <c>xmp:MetadataDate</c> first, because it moves only when metadata moves — re-exporting or
    /// rotating a JPEG leaves it where it was, while the file's timestamp would jump and look like
    /// somebody had edited the metadata. The file's own timestamp is the fallback, since plenty of
    /// files carry no XMP date at all.
    /// </summary>
    private static DateTimeOffset? MetadataChangedAt(Dictionary<string, JsonElement> document, string? path)
    {
        if (First(document, "XMP:MetadataDate", "XMP-xmp:MetadataDate") is { } stamp
            && TryParseExifDate(stamp) is { } parsed)
        {
            return parsed;
        }

        try
        {
            return path is not null && File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                : null;
        }
        catch (Exception)
        {
            // An unreadable timestamp is simply unknown, which the detector already treats as
            // "cannot rule the disagreement out".
            return null;
        }
    }

    /// <summary>
    /// ExifTool's date format is <c>yyyy:MM:dd HH:mm:ss</c>, optionally with a fraction and a zone,
    /// which no standard parse handles on its own.
    /// </summary>
    internal static DateTimeOffset? TryParseExifDate(string value)
    {
        var text = value.Trim();
        if (text.Length < 19)
        {
            return null;
        }

        // Only the date part uses colons as separators; swapping them for dashes makes the rest
        // ordinary ISO-8601, zone and all.
        var normalised = string.Concat(text[..10].Replace(':', '-'), text[10..]);

        return DateTimeOffset.TryParse(
            normalised, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private List<Dictionary<string, JsonElement>> ParseDocuments(string json, string context)
    {
        var documents = new List<Dictionary<string, JsonElement>>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return documents;
        }

        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return documents;
            }

            foreach (var element in parsed.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = property.Value.Clone();
                }

                documents.Add(map);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse ExifTool output for {File}", context);
        }

        return documents;
    }

    private static Dictionary<string, JsonElement>? FindDocument(
        List<Dictionary<string, JsonElement>> documents,
        string path)
    {
        foreach (var document in documents)
        {
            if (document.TryGetValue("SourceFile", out var source) &&
                source.ValueKind == JsonValueKind.String &&
                string.Equals(source.GetString(), path, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
    }

    private static EditableMetadata ReadEditable(Dictionary<string, JsonElement> document) => new()
    {
        Title = First(document, "XMP:Title", "IPTC:ObjectName", "QuickTime:Title", "XMP:Headline"),
        Description = First(document, "XMP:Description", "IPTC:Caption-Abstract", "EXIF:ImageDescription", "QuickTime:Description"),
        Keywords = ReadKeywords(document),
        Rating = ReadRating(document),
        Label = First(document, "XMP:Label"),
        Flag = ReadFlag(document),
        Creator = First(document, "XMP:Creator", "IPTC:By-line", "EXIF:Artist", "QuickTime:Artist"),
        Copyright = First(document, "XMP:Rights", "IPTC:CopyrightNotice", "EXIF:Copyright"),
        Headline = First(document, "XMP:Headline", "IPTC:Headline")
    };

    /// <summary>
    /// The cull flag, read from whichever application last wrote one.
    ///
    /// Four conventions are checked in turn, because no single property is understood everywhere:
    /// digiKam's PickLabel carries both states, Photo Mechanic's Tagged and Lightroom's
    /// <c>xmpDM:good</c> each carry both as a boolean, and Adobe's rating of -1 carries "rejected".
    /// Whichever is present wins, most specific first, so a workspace that has been through another
    /// application still reads correctly here.
    ///
    /// Lightroom's is the one that was missing, and it was found the hard way: a folder culled in
    /// Lightroom 9 showed no flags at all. Lightroom writes nothing in the other three — its rating
    /// stays 0 for a reject — so there was nothing to fall back on.
    /// </summary>
    private static MediaFlag? ReadFlag(Dictionary<string, JsonElement> document)
    {
        if (First(document, "XMP:PickLabel", "XMP-digiKam:PickLabel") is { } pick &&
            int.TryParse(pick, out var value))
        {
            // Unknown numbers are ignored rather than guessed at: the property belongs to another
            // application, which is free to add values this one has never heard of.
            if (Enum.IsDefined(typeof(MediaFlag), value))
            {
                return (MediaFlag)value;
            }
        }

        // ExifTool prints this one as Yes/No, and writes it as True/False.
        if (First(document, "XMP:Tagged", "XMP-photomech:Tagged") is { } tagged)
        {
            if (tagged.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                tagged.Equals("True", StringComparison.OrdinalIgnoreCase))
            {
                return MediaFlag.Accepted;
            }

            if (tagged.Equals("No", StringComparison.OrdinalIgnoreCase) ||
                tagged.Equals("False", StringComparison.OrdinalIgnoreCase))
            {
                return MediaFlag.Rejected;
            }
        }

        // Lightroom's, in the Dynamic Media namespace of all places. A boolean, like Tagged, and
        // read the same way: ExifTool hands it over as true/false in JSON and Yes/No elsewhere.
        if (First(document, "XMP:Good", "XMP-xmpDM:Good") is { } good)
        {
            if (good.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                good.Equals("Yes", StringComparison.OrdinalIgnoreCase))
            {
                return MediaFlag.Accepted;
            }

            if (good.Equals("False", StringComparison.OrdinalIgnoreCase) ||
                good.Equals("No", StringComparison.OrdinalIgnoreCase))
            {
                return MediaFlag.Rejected;
            }
        }

        // Adobe's convention, and the reason ReadRating refuses to clamp a negative into a zero.
        if (First(document, "XMP:Rating") is { } rating &&
            double.TryParse(rating, NumberStyles.Any, CultureInfo.InvariantCulture, out var stars) &&
            stars < 0)
        {
            return MediaFlag.Rejected;
        }

        return null;
    }

    private static CameraInfo ReadCamera(Dictionary<string, JsonElement> document)
    {
        var make = First(document, "EXIF:Make", "QuickTime:Make");
        var model = First(document, "EXIF:Model", "QuickTime:Model");

        return new CameraInfo
        {
            Camera = CombineMakeAndModel(make, model),
            Lens = First(document, "EXIF:LensModel", "Composite:LensID", "XMP:Lens", "MakerNotes:LensType"),
            Iso = First(document, "EXIF:ISO", "Composite:ISO", "MakerNotes:ISO"),
            ShutterSpeed = First(document, "Composite:ShutterSpeed", "EXIF:ExposureTime"),
            Aperture = FormatAperture(First(document, "Composite:Aperture", "EXIF:FNumber")),
            FocalLength = First(document, "EXIF:FocalLength", "Composite:FocalLength35efl"),
            CaptureDate = First(document, "EXIF:DateTimeOriginal", "EXIF:CreateDate", "XMP:DateCreated", "QuickTime:CreateDate"),
            Gps = First(document, "Composite:GPSPosition", "EXIF:GPSPosition"),
            Orientation = First(document, "EXIF:Orientation")
        };
    }

    /// <summary>
    /// The picture's size, turned the right way up.
    ///
    /// Composite:ImageSize first because it is the image itself: a raw file's File:ImageWidth can
    /// be the embedded preview rather than the frame. The fallbacks matter for formats that have no
    /// composite, and videos, which carry their dimensions under their container's group.
    /// </summary>
    private static ImageDimensions? ReadDimensions(Dictionary<string, JsonElement> document)
    {
        if (First(document, "Composite:ImageSize") is { } size)
        {
            var parts = size.Split('x', 'X', '×');

            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), out var w) &&
                int.TryParse(parts[1].Trim(), out var h))
            {
                return ImageDimensions.From(w, h, First(document, "EXIF:Orientation"));
            }
        }

        var width = FirstNumber(document,
            "EXIF:ExifImageWidth", "File:ImageWidth", "QuickTime:ImageWidth", "RIFF:ImageWidth");
        var height = FirstNumber(document,
            "EXIF:ExifImageHeight", "File:ImageHeight", "QuickTime:ImageHeight", "RIFF:ImageHeight");

        return ImageDimensions.From(width, height, First(document, "EXIF:Orientation"));
    }

    private static int? FirstNumber(Dictionary<string, JsonElement> document, params string[] keys)
        => First(document, keys) is { } value && int.TryParse(value.Trim(), out var number)
            ? number
            : null;

    private static VideoInfo ReadVideo(Dictionary<string, JsonElement> document)
    {
        var width = First(document, "QuickTime:ImageWidth", "File:ImageWidth", "RIFF:ImageWidth");
        var height = First(document, "QuickTime:ImageHeight", "File:ImageHeight", "RIFF:ImageHeight");

        return new VideoInfo
        {
            Codec = First(document, "QuickTime:CompressorName", "QuickTime:CompressorID", "RIFF:VideoCodec", "Track1:CompressorName"),
            Resolution = width is not null && height is not null ? $"{width} × {height}" : null,
            FrameRate = First(document, "QuickTime:VideoFrameRate", "Composite:VideoFrameRate", "RIFF:VideoFrameRate"),
            Duration = First(document, "QuickTime:Duration", "Composite:Duration", "RIFF:Duration"),
            Bitrate = First(document, "QuickTime:AvgBitrate", "Composite:AvgBitrate", "RIFF:AvgBytesPerSec"),
            ColourSpace = First(document, "QuickTime:ColorRepresentation", "QuickTime:ColorPrimaries", "QuickTime:ColorProfiles"),
            HdrInfo = First(document, "QuickTime:TransferCharacteristics", "QuickTime:HDRVividFlag", "QuickTime:MasteringDisplayMetadata"),
            AudioCodec = First(document, "QuickTime:AudioFormat", "RIFF:Encoding", "QuickTime:AudioCodecID"),
            AudioChannels = First(document, "QuickTime:AudioChannels", "RIFF:NumChannels"),
            AudioSampleRate = First(document, "QuickTime:AudioSampleRate", "RIFF:SampleRate")
        };
    }

    /// <summary>
    /// Every tag, for the inspector's All tags view. Sidecar tags are included and marked so a power
    /// user can see which layer a value actually came from, and they sort first: the sidecar is the
    /// layer this app writes, so it is the one most worth finding without scrolling.
    /// </summary>
    private static ImmutableArray<RawMetadataTag> ReadRawTags(
        Dictionary<string, JsonElement> media,
        Dictionary<string, JsonElement>? sidecar)
    {
        var tags = new List<RawMetadataTag>();
        Collect(media, null);
        Collect(sidecar, "Sidecar");

        return tags
            .OrderBy(t => IsFromSidecar(t) ? 0 : 1)
            .ThenBy(t => t.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        static bool IsFromSidecar(RawMetadataTag tag)
            => tag.Group.StartsWith("Sidecar:", StringComparison.Ordinal);

        void Collect(Dictionary<string, JsonElement>? document, string? prefix)
        {
            if (document is null)
            {
                return;
            }

            foreach (var (key, value) in document)
            {
                if (key is "SourceFile")
                {
                    continue;
                }

                var separator = key.IndexOf(':');
                var group = separator > 0 ? key[..separator] : "Other";
                var name = separator > 0 ? key[(separator + 1)..] : key;

                if (prefix is not null)
                {
                    group = $"{prefix}:{group}";
                }

                var text = Stringify(value);
                if (!string.IsNullOrEmpty(text))
                {
                    tags.Add(new RawMetadataTag(group, name, text));
                }
            }
        }
    }

    private static ImmutableArray<string> ReadKeywords(Dictionary<string, JsonElement> document)
    {
        foreach (var key in (string[])["XMP:Subject", "IPTC:Keywords", "XMP:HierarchicalSubject"])
        {
            if (!document.TryGetValue(key, out var value))
            {
                continue;
            }

            var keywords = value.ValueKind switch
            {
                JsonValueKind.Array => value.EnumerateArray().Select(Stringify),
                _ => Stringify(value).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).AsEnumerable()
            };

            var result = keywords
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

            if (result.Length > 0)
            {
                return result;
            }
        }

        return [];
    }

    private static int? ReadRating(Dictionary<string, JsonElement> document)
    {
        var raw = First(document, "XMP:Rating", "XMP:RatingPercent");
        if (raw is null)
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var rounded = (int)Math.Round(value);

        // A negative rating is not a rating. Adobe writes -1 to mean "rejected", so clamping it to 0
        // would both lose the rejection and invent a zero-star rating the photographer never gave.
        // ReadFlag picks the rejection up instead.
        return rounded < 0 ? null : Math.Clamp(rounded, 0, 5);
    }

    private static string? CombineMakeAndModel(string? make, string? model)
    {
        if (make is null)
        {
            return model;
        }

        if (model is null)
        {
            return make;
        }

        // Most cameras repeat the manufacturer in the model ("Canon EOS R5"); avoid "Canon Canon EOS R5".
        return model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    private static string? FormatAperture(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.StartsWith("f/", StringComparison.OrdinalIgnoreCase) ? value : $"f/{value}";
    }

    private static string? First(Dictionary<string, JsonElement> document, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (document.TryGetValue(key, out var value))
            {
                var text = Stringify(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Stringify).Where(s => !string.IsNullOrEmpty(s))),
        _ => value.GetRawText()
    };
}
