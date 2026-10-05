using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

public class LocalSeatTests
{
    private static ArgvAnalysis Hls()
        => ArgvAnalysis.Analyze("ffmpeg", ["-i", "/data/movies/a.mkv", "-f", "hls", "/config/cache/transcodes/x.m3u8"]);

    private static ArgvAnalysis Trickplay()
        => ArgvAnalysis.Analyze("ffmpeg", ["-i", "/data/movies/a.mkv", "-f", "image2", "/config/cache/temp/jellyfin/g/%08d.jpg"]);

    private static TentacleNode Node(string name, int slots = 4)
    {
        var node = new TentacleNode(new Hello { Node = name, Arch = "x64", MaxJobs = slots, MaxBackgroundJobs = slots / 2 });
        node.ApplyVerification([], ["/config/cache/transcodes", "/config/cache/temp/jellyfin", "/data/movies"], false, incompatible: false);
        return node;
    }

    [Fact]
    public void WithoutSlotsTheServerOnlyTakesWhatNoTentacleCan()
    {
        var local = new LocalSeat { Slots = 0 };
        Assert.NotNull(Scheduler.Place(Hls(), PlacementMode.Active, [Node("mandalore")], local).Node);
        var full = Node("mandalore", slots: 1);
        full.Reserve(1, false);
        Assert.Equal("all-busy", Scheduler.Place(Hls(), PlacementMode.Active, [full], local).Reason);
    }

    [Fact]
    public void WithSlotsTheServerCompetes()
    {
        var local = new LocalSeat { Slots = 2 };
        var mandalore = Node("mandalore");
        var tatooine = Node("tatooine");

        // All idle and none has had a job: a tie, settled by name.
        Assert.Same(mandalore, Scheduler.Place(Hls(), PlacementMode.Active, [mandalore, tatooine], local).Node);

        // Each tentacle runs one: the idle server is the least busy.
        mandalore.Reserve(1, false);
        tatooine.Reserve(1, false);
        var here = Scheduler.Place(Hls(), PlacementMode.Active, [mandalore, tatooine], local);
        Assert.Null(here.Node);
        Assert.Equal("least-loaded-local", here.Reason);

        // Its slots full, it is out of the running.
        local.Reserve(1, false);
        local.Reserve(1, false);
        Assert.Same(mandalore, Scheduler.Place(Hls(), PlacementMode.Active, [mandalore, tatooine], local).Node);
    }

    [Fact]
    public void WeightScalesTheServersShare()
    {
        var local = new LocalSeat { Slots = 4, Weight = 2 };
        var mandalore = Node("mandalore");
        local.Reserve(1, false);

        // (1+1)/2 = 1 against (0+1)/1 = 1: a tie, and the server took a job last, so the
        // tentacle gets it; one more there and the server wins outright.
        Assert.Same(mandalore, Scheduler.Place(Hls(), PlacementMode.Active, [mandalore], local).Node);
        mandalore.Reserve(1, false);
        Assert.Equal("least-loaded-local", Scheduler.Place(Hls(), PlacementMode.Active, [mandalore], local).Reason);
    }

    [Fact]
    public void TiesRotateBetweenAllNodesTheServerIncluded()
    {
        var local = new LocalSeat { Slots = 4 };
        var mandalore = Node("mandalore");
        var tatooine = Node("tatooine");
        var order = new System.Collections.Generic.List<string>();
        for (var i = 0; i < 6; i++)
        {
            // Short jobs: each ends before the next starts, so every placement is a tie.
            var d = Scheduler.Place(Hls(), PlacementMode.Active, [tatooine, mandalore], local);
            if (d.Node is { } node)
            {
                node.Release(node.Reserve(1, false));
                order.Add(node.Name);
            }
            else
            {
                local.Release(local.Reserve(1, false));
                order.Add("local");
            }
        }

        Assert.Equal(["mandalore", "tatooine", "local", "mandalore", "tatooine", "local"], order);
    }

    [Fact]
    public void BackgroundJobsGetHalfTheServersSlots()
    {
        var local = new LocalSeat { Slots = 2 };
        local.Reserve(0.5, true);
        local.Reserve(0.5, true);
        Assert.Null(local.ScoreWith(0.5, true));
        Assert.NotNull(local.ScoreWith(1, false));
        Assert.Equal("no-tentacle", Scheduler.Place(Trickplay(), PlacementMode.Active, [], local).Reason);
    }

    [Fact]
    public void ShadowModeReportsTheServer()
    {
        var local = new LocalSeat { Slots = 1 };
        var busy = Node("mandalore");
        busy.Reserve(2, false);
        var d = Scheduler.Place(Hls(), PlacementMode.DryRun, [busy], local);
        Assert.Equal("local", d.WouldRunOn);
    }
}
