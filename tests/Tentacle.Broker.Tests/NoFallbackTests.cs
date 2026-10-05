using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

public class NoFallbackTests
{
    private static Broker NewBroker(bool fallback) => new(
        new BrokerOptions
        {
            Placement = PlacementMode.Active,
            SharedRoots = () => [],
            ServerFfmpegVersion = () => "8.1.3",
            LocalFallback = fallback,
            LocalSlots = 4,
            NoFallbackWait = TimeSpan.FromMilliseconds(300),
            ServerName = "corellia",
        },
        NullLogger<Broker>.Instance);

    private static async Task<Placement?> RunAsync(Broker broker, params string[] args)
    {
        var input = new MemoryStream();
        await StreamFrames.WriteAsync(input, Frame.Json(FrameType.Start, new StartRequest { Args = args }, ProtocolJson.Default.StartRequest), CancellationToken.None);
        input.Position = 0;
        var output = new MemoryStream();
        await broker.HandleShimAsync(input, output, null, CancellationToken.None);
        output.Position = 0;
        return (await StreamFrames.ReadAsync(output, CancellationToken.None))?.Read(ProtocolJson.Default.Placement);
    }

    [Fact]
    public async Task APlaybackNoTentacleCanTakeWaitsThenIsRefused()
    {
        var broker = NewBroker(fallback: false);
        var clock = Stopwatch.StartNew();
        var placement = await RunAsync(broker, "-i", "/data/a.mkv", "-f", "hls", "/config/cache/transcodes/x.m3u8");

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(250), "it should wait for a tentacle first");
        Assert.NotNull(placement);
        Assert.False(placement.Remote);
        Assert.Contains("corellia does not run jobs a tentacle could", placement.Refused, StringComparison.Ordinal);
        var job = Assert.Single(broker.History.Snapshot(10));
        Assert.Equal(("none", "refused", "no-tentacle"), (job.Node, job.Outcome, job.Reason));

        // Its slots do not count either: with no fallback the server never competes.
        Assert.Equal(0, broker.Local.Load);
    }

    [Fact]
    public async Task JobsOnlyTheServerCanRunStillRunThere()
    {
        var broker = NewBroker(fallback: false);
        var node = new TentacleNode(new Hello { Node = "mandalore", Arch = "x64", MaxJobs = 4, MaxBackgroundJobs = 2 });
        node.ApplyVerification([], ["/config/cache/transcodes", "/data/movies"], false, incompatible: false);
        broker.Nodes.Add(node);

        // A directory no tentacle shares: waiting would not help, so no refusal.
        var unshared = await RunAsync(broker, "-i", "/data/private/a.mkv", "-f", "hls", "/config/cache/transcodes/x.m3u8");
        Assert.Equal(string.Empty, unshared!.Refused);
        Assert.StartsWith("path-ineligible", unshared.Reason, StringComparison.Ordinal);

        // A short kind always runs here.
        var image = await RunAsync(broker, "-i", "/data/movies/a.mkv", "-frames:v", "1", "-f", "image2", "/config/cache/temp/a.jpg");
        Assert.Equal((string.Empty, "kind-local"), (image!.Refused, image.Reason));
    }

    [Fact]
    public async Task WithTheFallbackTheServerRunsIt()
    {
        var placement = await RunAsync(NewBroker(fallback: true), "-i", "/data/a.mkv", "-f", "hls", "/config/cache/transcodes/x.m3u8");
        Assert.Equal(string.Empty, placement!.Refused);
        Assert.False(placement.Remote);
    }
}
