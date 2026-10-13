using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Images;
using Xunit;

namespace Amberglass.Tests;

public class SharedRawDecoderTests
{
    /// <summary>A develop that finishes when the test says so, counting how often it was started.</summary>
    private sealed class GatedDecoder : IRawDecoder
    {
        private TaskCompletionSource<DecodedImage?> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public bool IsAvailable => true;

        public async Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        {
            Started++;
            LastToken = cancellationToken;
            return await _gate.Task.WaitAsync(cancellationToken);
        }

        public void Finish() => _gate.TrySetResult(new DecodedImage(new byte[4], 1, 1));

        public void Reset() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly MediaFile Pano = new()
    {
        FullPath = "/library/pano.dng",
        FileName = "pano.dng",
        MediaType = MediaType.Image,
        SizeBytes = 1,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        CreatedUtc = DateTimeOffset.UnixEpoch
    };

    private static (SharedRawDecoder Shared, GatedDecoder Inner, StubSettingsService Settings) Create(TimeSpan? retention = null)
    {
        var inner = new GatedDecoder();
        var settings = new StubSettingsService(AppSettings.Default);
        return (new SharedRawDecoder(inner, settings, retention), inner, settings);
    }

    /// <summary>The preview pane and the viewer asking together get one develop between them.</summary>
    [Fact]
    public async Task Concurrent_requests_share_one_develop()
    {
        var (shared, inner, _) = Create();

        var pane = shared.DevelopAsync(Pano);
        var viewer = shared.DevelopAsync(Pano);
        inner.Finish();

        Assert.Same(await pane, await viewer);
        Assert.Equal(1, inner.Started);
    }

    [Fact]
    public async Task A_request_just_after_a_develop_is_given_its_result()
    {
        var (shared, inner, _) = Create();

        inner.Finish();
        var first = await shared.DevelopAsync(Pano);
        var second = await shared.DevelopAsync(Pano);

        Assert.Same(first, second);
        Assert.Equal(1, inner.Started);
    }

    /// <summary>Held briefly, not for good: a 190 MB develop is not kept for nothing.</summary>
    [Fact]
    public async Task The_result_is_let_go_after_a_while()
    {
        var (shared, inner, _) = Create(retention: TimeSpan.FromMilliseconds(20));

        inner.Finish();
        await shared.DevelopAsync(Pano);
        await Task.Delay(200);
        await shared.DevelopAsync(Pano);

        Assert.Equal(2, inner.Started);
    }

    /// <summary>A moved slider is a different picture, so it must not be served the old develop.</summary>
    [Fact]
    public async Task Different_develop_settings_develop_again()
    {
        var (shared, inner, settings) = Create();

        inner.Finish();
        await shared.DevelopAsync(Pano);
        await settings.SaveAsync(settings.Current with { RawDevelop = new RawDevelopSettings { ExposureStops = 1 } });
        await shared.DevelopAsync(Pano);

        Assert.Equal(2, inner.Started);
    }

    [Fact]
    public async Task One_caller_giving_up_leaves_the_develop_for_the_other()
    {
        var (shared, inner, _) = Create();
        using var leaving = new CancellationTokenSource();

        var abandoned = shared.DevelopAsync(Pano, leaving.Token);
        var staying = shared.DevelopAsync(Pano);

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        Assert.False(inner.LastToken.IsCancellationRequested);
        inner.Finish();
        Assert.NotNull(await staying);
    }

    /// <summary>Moving on through a folder must not leave a develop burning a core for nobody.</summary>
    [Fact]
    public async Task The_develop_stops_when_everyone_has_given_up()
    {
        var (shared, inner, _) = Create();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var a = shared.DevelopAsync(Pano, first.Token);
        var b = shared.DevelopAsync(Pano, second.Token);
        await Task.Delay(20);

        await first.CancelAsync();
        await second.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);

        Assert.True(inner.LastToken.IsCancellationRequested);

        // And a later request starts afresh rather than joining the abandoned one.
        inner.Reset();
        var again = shared.DevelopAsync(Pano);
        inner.Finish();
        Assert.NotNull(await again);
        Assert.Equal(2, inner.Started);
    }
}
