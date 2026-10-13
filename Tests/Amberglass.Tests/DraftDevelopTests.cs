using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Images;
using Amberglass.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class DraftDevelopTests
{
    private static MediaFile Raw(string name = "shot.raf") => new()
    {
        FullPath = "/library/" + name,
        FileName = name,
        MediaType = MediaType.Image,
        SizeBytes = 27_000_000,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    /// <summary>A decoder whose full and draft develops are told apart by their width.</summary>
    private sealed class TwoSpeedDecoder(bool hasDraft = true) : IRawDecoder
    {
        public int Full { get; private set; }

        public int Drafts { get; private set; }

        public bool IsAvailable => true;

        public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Full++;
            return Task.FromResult<DecodedImage?>(new DecodedImage(new byte[16], 2, 2));
        }

        public Task<DecodedImage?> DevelopDraftAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Drafts++;
            return Task.FromResult(hasDraft ? new DecodedImage(new byte[4], 1, 1) : null);
        }
    }

    // ---- LibRaw ------------------------------------------------------------------------------------

    /// <summary>Half size skips the demosaic, which is what makes a draft a fraction of the cost.</summary>
    [Fact]
    public void A_libraw_draft_develops_at_half_size()
    {
        Assert.Contains("-h", LibRawImageDecoder.BuildArguments("/library/shot.raf", RawDevelopSettings.Default, draft: true));
        Assert.DoesNotContain("-h", LibRawImageDecoder.BuildArguments("/library/shot.raf", RawDevelopSettings.Default));
    }

    /// <summary>The draft has to show the change being made, so the settings still apply.</summary>
    [Fact]
    public void A_libraw_draft_keeps_the_develop_settings()
    {
        var develop = RawDevelopSettings.Default with { ExposureStops = 1 };

        Assert.Contains("-aexpo", LibRawImageDecoder.BuildArguments("/library/shot.raf", develop, draft: true));
    }

    // ---- Composite and shared ----------------------------------------------------------------------

    /// <summary>
    /// The platform decoder offers no draft and ignores the settings anyway; a file only it can read
    /// gets no draft rather than a full develop disguised as one.
    /// </summary>
    [Fact]
    public async Task A_decoder_without_a_draft_is_passed_over_for_drafts()
    {
        var withoutDraft = new TwoSpeedDecoder(hasDraft: false);
        var composite = new CompositeRawDecoder([withoutDraft], NullLogger<CompositeRawDecoder>.Instance);

        Assert.Null(await composite.DevelopDraftAsync(Raw()));
        Assert.Equal(0, withoutDraft.Full);
    }

    [Fact]
    public async Task The_first_decoder_with_a_draft_supplies_it()
    {
        var first = new TwoSpeedDecoder(hasDraft: false);
        var second = new TwoSpeedDecoder();
        var composite = new CompositeRawDecoder([first, second], NullLogger<CompositeRawDecoder>.Instance);

        var draft = await composite.DevelopDraftAsync(Raw());

        Assert.Equal(1, draft!.Width);
        Assert.Equal(1, second.Drafts);
    }

    /// <summary>A draft kept for latecomers must never be handed to someone asking for the real thing.</summary>
    [Fact]
    public async Task A_shared_draft_never_stands_in_for_a_full_develop()
    {
        var inner = new TwoSpeedDecoder();
        var shared = new SharedRawDecoder(inner, new StubSettingsService(AppSettings.Default));

        var draft = await shared.DevelopDraftAsync(Raw());
        var full = await shared.DevelopAsync(Raw());

        Assert.Equal(1, draft!.Width);
        Assert.Equal(2, full!.Width);
        Assert.Equal(1, inner.Full);
    }

    // ---- Full image decoder ------------------------------------------------------------------------

    private static SkiaFullImageDecoder Decoder(IRawDecoder raw, bool developRaw = true)
        => new(
            new NoPreview(),
            raw,
            new StubSettingsService(AppSettings.Default with { DevelopRawFiles = developRaw }),
            NullLogger<SkiaFullImageDecoder>.Instance);

    private sealed class NoPreview : IEmbeddedPreviewExtractor
    {
        public bool IsAvailable => false;

        public Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
            => Task.FromResult<EmbeddedPreview?>(null);
    }

    [Fact]
    public async Task A_raw_being_developed_has_a_draft()
    {
        var draft = await Decoder(new TwoSpeedDecoder()).DecodeDraftAsync(Raw());

        Assert.Equal(1, draft!.Width);
    }

    /// <summary>The settings only apply to developed RAWs; anything else has nothing to draft.</summary>
    [Fact]
    public async Task Ordinary_images_and_undeveloped_raws_have_no_draft()
    {
        var raw = new TwoSpeedDecoder();

        Assert.Null(await Decoder(raw).DecodeDraftAsync(Raw("photo.jpg")));
        Assert.Null(await Decoder(raw, developRaw: false).DecodeDraftAsync(Raw()));
        Assert.Equal(0, raw.Drafts);
    }

    // ---- Scheduling --------------------------------------------------------------------------------

    private sealed class Recorder
    {
        private readonly Lock _gate = new();

        public List<string> Events { get; } = [];

        public List<CancellationToken> DraftTokens { get; } = [];

        public Task Draft(CancellationToken token)
        {
            lock (_gate)
            {
                Events.Add("draft");
                DraftTokens.Add(token);
            }

            return Task.CompletedTask;
        }

        public Task Full()
        {
            lock (_gate)
            {
                Events.Add("full");
            }

            return Task.CompletedTask;
        }
    }

    private static (DevelopPreviewScheduler Scheduler, Recorder Recorder) Scheduler()
    {
        var recorder = new Recorder();
        var scheduler = new DevelopPreviewScheduler(
            recorder.Draft, recorder.Full, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(250));
        return (scheduler, recorder);
    }

    /// <summary>
    /// A drag is a burst of ticks. Each one must not start a develop of its own: the burst gets one
    /// draft once the ticks pause and one full develop once they stop.
    /// </summary>
    [Fact]
    public async Task A_drag_draws_one_draft_and_one_full_develop()
    {
        var (scheduler, recorder) = Scheduler();

        for (var i = 0; i < 5; i++)
        {
            scheduler.Changed(dragged: true);
            await Task.Delay(10);
        }

        await Task.Delay(600);

        Assert.Equal(["draft", "full"], recorder.Events);
    }

    /// <summary>A draft is drawn while the slider is still in hand, before the settings rest.</summary>
    [Fact]
    public async Task A_draft_arrives_before_the_settings_have_settled()
    {
        var (scheduler, recorder) = Scheduler();

        scheduler.Changed(dragged: true);
        await Task.Delay(150);

        Assert.Equal(["draft"], recorder.Events);
    }

    [Fact]
    public async Task A_new_change_cancels_the_draft_before_it()
    {
        var (scheduler, recorder) = Scheduler();

        scheduler.Changed(dragged: true);
        await Task.Delay(120);
        scheduler.Changed(dragged: true);
        await Task.Delay(600);

        Assert.True(recorder.DraftTokens[0].IsCancellationRequested);
        Assert.Equal(["draft", "draft", "full"], recorder.Events);
    }

    /// <summary>A pick has no ticks behind it: draft at once, then the full develop.</summary>
    [Fact]
    public async Task A_picked_change_drafts_immediately_then_develops()
    {
        var (scheduler, recorder) = Scheduler();

        scheduler.Changed(dragged: false);
        await Task.Delay(50);

        Assert.Equal(["draft", "full"], recorder.Events);
    }

    /// <summary>Moving to another photograph abandons work meant for the one left behind.</summary>
    [Fact]
    public async Task Cancelling_abandons_everything_still_waiting()
    {
        var (scheduler, recorder) = Scheduler();

        scheduler.Changed(dragged: true);
        scheduler.Cancel();
        await Task.Delay(400);

        Assert.Empty(recorder.Events);
    }
}
