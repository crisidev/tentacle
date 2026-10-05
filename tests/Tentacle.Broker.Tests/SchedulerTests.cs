using System;
using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

public class SchedulerTests
{
    private static ArgvAnalysis Hls(string input = "/data/movies/a.mkv", bool hardware = false)
        => ArgvAnalysis.Analyze("ffmpeg", hardware
            ? ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-hwaccel", "vaapi", "-i", input, "-f", "hls", "/config/cache/transcodes/x.m3u8"]
            : ["-i", input, "-f", "hls", "-hls_segment_filename", "/config/cache/transcodes/x%d.ts", "/config/cache/transcodes/x.m3u8"]);

    private static ArgvAnalysis Trickplay()
        => ArgvAnalysis.Analyze("ffmpeg", ["-i", "/data/movies/a.mkv", "-f", "image2", "/config/cache/temp/jellyfin/g/%08d.jpg"]);

    private static TentacleNode Node(string name, int slots = 2, int background = 1, bool hardware = false, string ffmpeg = "ffmpeg version 8.1.3-Jellyfin")
    {
        var node = new TentacleNode(new Hello { Node = name, MaxJobs = slots, MaxBackgroundJobs = background, FfmpegVersion = ffmpeg });
        node.ApplyVerification([], ["/config/cache/transcodes", "/config/cache/temp/jellyfin", "/data/movies"], hardware, incompatible: false);
        return node;
    }

    [Fact]
    public void TranscodeGoesToTheLeastLoadedTentacle()
    {
        var busy = Node("mandalore");
        busy.Reserve(1, false);
        var idle = Node("tatooine");
        Assert.Same(idle, Scheduler.Place(Hls(), PlacementMode.Active, [busy, idle]).Node);
    }

    [Fact]
    public void WeightScalesTheLoad()
    {
        var big = new TentacleNode(new Hello { Node = "big", MaxJobs = 4, Weight = 3 });
        big.ApplyVerification([], ["/config/cache/transcodes", "/data/movies"], false, false);
        big.Reserve(1, false);
        var small = Node("small");
        Assert.Same(big, Scheduler.Place(Hls(), PlacementMode.Active, [small, big]).Node);
    }

    [Fact]
    public void ShadowModeRecordsButRunsLocally()
    {
        var d = Scheduler.Place(Hls(), PlacementMode.DryRun, [Node("tatooine")]);
        Assert.Null(d.Node);
        Assert.Equal("tatooine", d.WouldRunOn);
    }

    [Fact]
    public void UnverifiedOrUnsharedRunsLocally()
    {
        var unverified = new TentacleNode(new Hello { Node = "new", MaxJobs = 2 });
        Assert.Equal("no-ready-tentacle", Scheduler.Place(Hls(), PlacementMode.Active, [unverified]).Reason);
        Assert.Equal("path-ineligible:/mnt/usb/a.mkv", Scheduler.Place(Hls("/mnt/usb/a.mkv"), PlacementMode.Active, [Node("tatooine")]).Reason);
    }

    [Fact]
    public void PathsGoOnlyToTentaclesThatShareThem()
    {
        var partial = new TentacleNode(new Hello { Node = "partial", MaxJobs = 4 });
        partial.ApplyVerification([new NodeCheck("root:/data/movies", false, "missing")], ["/config/cache/transcodes"], false, false);
        var full = Node("full");
        full.Reserve(1, false);
        Assert.Same(full, Scheduler.Place(Hls(), PlacementMode.Active, [partial, full]).Node);
        Assert.Equal(NodeState.Degraded, partial.State);
    }

    [Fact]
    public void HardwareJobsNeedAWorkingDevice()
    {
        Assert.Equal("no-hardware-tentacle", Scheduler.Place(Hls(hardware: true), PlacementMode.Active, [Node("cpu")]).Reason);
        var gpu = Node("gpu", hardware: true);
        Assert.Same(gpu, Scheduler.Place(Hls(hardware: true), PlacementMode.Active, [Node("cpu"), gpu]).Node);
    }

    [Fact]
    public void BackgroundJobsCannotTakePlaybackSlots()
    {
        var node = Node("tatooine", slots: 2, background: 1);
        var first = Scheduler.Place(Trickplay(), PlacementMode.Active, [node]);
        Assert.Same(node, first.Node);
        Assert.True(first.Policy.Background);
        node.Reserve(first.Policy.Cost, true);
        node.Reserve(first.Policy.Cost, true);

        // Background cap (1 slot) is full: more trickplay waits locally...
        Assert.Equal("background-slots-full", Scheduler.Place(Trickplay(), PlacementMode.Active, [node]).Reason);

        // ...while playback still gets the remaining slot.
        Assert.Same(node, Scheduler.Place(Hls(), PlacementMode.Active, [node]).Node);
    }

    [Fact]
    public void ProbesAndSingleImagesStayLocal()
    {
        var node = Node("tatooine");
        Assert.Equal("kind-local", Scheduler.Place(ArgvAnalysis.Analyze("ffprobe", ["-i", "/data/movies/a.mkv"]), PlacementMode.Active, [node]).Reason);
        Assert.Equal("kind-local", Scheduler.Place(ArgvAnalysis.Analyze("ffmpeg", ["-i", "/data/movies/a.mkv", "-f", "image2", "/config/cache/temp/jellyfin/a.jpg"]), PlacementMode.Active, [node]).Reason);
    }

    [Fact]
    public void FullTentaclesRunLocally()
    {
        var full = Node("tatooine", slots: 1);
        full.Reserve(1, false);
        Assert.Equal("all-busy", Scheduler.Place(Hls(), PlacementMode.Active, [full]).Reason);
        Assert.Equal("no-tentacle", Scheduler.Place(Hls(), PlacementMode.Active, []).Reason);
    }

    [Fact]
    public void RepeatedInfrastructureFailuresCoolDown()
    {
        var now = DateTimeOffset.UtcNow;
        var node = new TentacleNode(new Hello { Node = "flaky", MaxJobs = 2 }, () => now);
        node.ApplyVerification([], ["/config/cache/transcodes", "/data/movies"], false, false);
        Assert.False(node.RecordOutcome(true));
        Assert.False(node.RecordOutcome(false)); // a success resets the count
        Assert.False(node.RecordOutcome(true));
        Assert.False(node.RecordOutcome(true));
        Assert.True(node.RecordOutcome(true));
        Assert.Equal(NodeState.CoolingDown, node.State);
        Assert.Equal("no-ready-tentacle", Scheduler.Place(Hls(), PlacementMode.Active, [node]).Reason);
        now += TimeSpan.FromMinutes(1.5);
        Assert.Equal(NodeState.Ready, node.State);
        Assert.True(node.NeedsVerify);
    }

    [Fact]
    public void DrainingTakesNothingNew()
    {
        var node = Node("tatooine");
        node.Draining = true;
        Assert.Equal(NodeState.Draining, node.State);
        Assert.Equal("no-ready-tentacle", Scheduler.Place(Hls(), PlacementMode.Active, [node]).Reason);
    }
}

public class NodeStateTests
{
    [Fact]
    public void OnlyRequiredChecksDegrade()
    {
        var node = new TentacleNode(new Hello { Node = "n", MaxJobs = 2 });
        node.ApplyVerification([new NodeCheck("root:/config/cache/concat", false, "missing", Required: false)], ["/config/cache/transcodes"], false, false);
        Assert.Equal(NodeState.Ready, node.State);
        node.ApplyVerification([new NodeCheck("root:/config/cache/temp/jellyfin", false, "missing")], ["/config/cache/transcodes"], false, false);
        Assert.Equal(NodeState.Degraded, node.State);
    }
}
