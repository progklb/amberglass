using Amberglass.Metadata.ExifTool;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Amberglass.Tests;

public class ExifToolSessionTests
{
    [Fact]
    public async Task Returns_the_output_for_a_request()
    {
        if (!FakeExifTool.IsSupported)
        {
            return; // The /bin/sh stub cannot run on Windows.
        }

        using var fake = new FakeExifTool("hello from exiftool");
        await using var session = new ExifToolSession(fake.Path_, NullLogger.Instance);

        var output = await session.ExecuteAsync(["-json", "/some/file.jpg"]);

        Assert.Contains("hello from exiftool", output);
    }

    [Fact]
    public async Task Output_excludes_the_ready_marker()
    {
        if (!FakeExifTool.IsSupported)
        {
            return; // The /bin/sh stub cannot run on Windows.
        }

        using var fake = new FakeExifTool("payload");
        await using var session = new ExifToolSession(fake.Path_, NullLogger.Instance);

        var output = await session.ExecuteAsync(["-json"]);

        Assert.DoesNotContain("ready", output);
    }

    [Fact]
    public async Task Reuses_a_single_process_across_requests()
    {
        if (!FakeExifTool.IsSupported)
        {
            return; // The /bin/sh stub cannot run on Windows.
        }

        using var temp = new TempFolder();
        var counter = Path.Combine(temp.Path, "starts.txt");

        using var fake = new FakeExifTool("payload", countInvocationsTo: counter);
        await using var session = new ExifToolSession(fake.Path_, NullLogger.Instance);

        for (var i = 0; i < 5; i++)
        {
            await session.ExecuteAsync(["-json", $"/file{i}.jpg"]);
        }

        // The whole point of -stay_open: five reads, one process.
        Assert.Equal(1, File.ReadAllLines(counter).Length);
    }

    [Fact]
    public async Task Sequential_requests_each_get_their_own_response()
    {
        if (!FakeExifTool.IsSupported)
        {
            return; // The /bin/sh stub cannot run on Windows.
        }

        using var fake = new FakeExifTool("payload");
        await using var session = new ExifToolSession(fake.Path_, NullLogger.Instance);

        var first = await session.ExecuteAsync(["-json", "/a.jpg"]);
        var second = await session.ExecuteAsync(["-json", "/b.jpg"]);

        Assert.Equal("payload", first.Trim());
        Assert.Equal("payload", second.Trim());
    }

    [Fact]
    public async Task Concurrent_requests_are_serialised_without_interleaving()
    {
        if (!FakeExifTool.IsSupported)
        {
            return; // The /bin/sh stub cannot run on Windows.
        }

        using var fake = new FakeExifTool("payload");
        await using var session = new ExifToolSession(fake.Path_, NullLogger.Instance);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(i => session.ExecuteAsync(["-json", $"/f{i}.jpg"])));

        Assert.All(results, r => Assert.Equal("payload", r.Trim()));
    }

    [Fact]
    public async Task Handles_a_ready_marker_sharing_a_line_with_the_payload()
    {
        if (!FakeExifTool.IsSupported)
        {
            return; // The /bin/sh stub cannot run on Windows.
        }

        // Output that does not end in a newline puts {ready1} on the same line as the payload.
        // Matching whole lines only would block until the request timeout.
        using var fake = new FakeExifTool("payload", terminateBodyWithNewline: false);
        await using var session = new ExifToolSession(fake.Path_, NullLogger.Instance);

        var output = await session.ExecuteAsync(["-json"]);

        Assert.Equal("payload", output.Trim());
    }

    [Fact]
    public async Task Disposing_twice_is_safe()
    {
        if (!FakeExifTool.IsSupported)
        {
            return; // The /bin/sh stub cannot run on Windows.
        }

        using var fake = new FakeExifTool("payload");
        var session = new ExifToolSession(fake.Path_, NullLogger.Instance);

        await session.ExecuteAsync(["-json"]);
        await session.DisposeAsync();
        await session.DisposeAsync();
    }
}
