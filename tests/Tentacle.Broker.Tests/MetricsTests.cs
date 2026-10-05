using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

public class MetricsTests
{
    private static async Task<string> ScrapeAsync(TentacleMetrics metrics)
    {
        using var stream = new MemoryStream();
        await metrics.Registry.CollectAndExportAsTextAsync(stream, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static TentacleNode Node(string name, string agent = "0.3.0")
    {
        var node = new TentacleNode(new Hello { Node = name, MaxJobs = 4, MaxBackgroundJobs = 2, AgentVersion = agent, FfmpegVersion = "ffmpeg version 8.1.3 Copyright (c) 2000-2026 the FFmpeg developers" });
        node.ApplyVerification([new NodeCheck("ffmpeg", true, "8.1.3"), new NodeCheck("root:/data", false, "missing", Required: false)], ["/config/cache/transcodes"], false, false);
        return node;
    }

    [Fact]
    public async Task LocalJobIsCountedWithItsPlacement()
    {
        var options = new BrokerOptions { Placement = PlacementMode.Active, SharedRoots = () => [], ServerFfmpegVersion = () => "8.1.3" };
        var broker = new Broker(options, NullLogger<Broker>.Instance);
        var input = new MemoryStream();
        var start = new StartRequest { Args = ["-i", "/data/a.mkv", "-f", "hls", "/config/cache/transcodes/x.m3u8"] };
        await StreamFrames.WriteAsync(input, Frame.Json(FrameType.Start, start, ProtocolJson.Default.StartRequest), CancellationToken.None);
        input.Position = 0;

        // No tentacle: placed locally; the stream's EOF stands for ffmpeg exiting.
        await broker.HandleShimAsync(input, new MemoryStream(), null, CancellationToken.None);

        var text = await ScrapeAsync(broker.Metrics);
        Assert.Contains("tentacle_placements_total{kind=\"Transcode\",node=\"local\",reason=\"no-tentacle\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_jobs_total{node=\"local\",kind=\"Transcode\",outcome=\"exited\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_jobs_running{node=\"local\",kind=\"Transcode\"} 0", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_job_duration_seconds_count{node=\"local\",kind=\"Transcode\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_placement_mode{mode=\"Active\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_broker_up 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerInfoNamesTheServerAndKeepsOneRole()
    {
        var metrics = new TentacleMetrics();
        metrics.RefreshServer("corellia", "backup");
        metrics.RefreshServer("corellia", "worker");

        var text = await ScrapeAsync(metrics);
        Assert.Contains("tentacle_server_info{name=\"corellia\",role=\"worker\"} 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"backup\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NodeGaugesFollowTheRegistry()
    {
        var metrics = new TentacleMetrics();
        var node = Node("tatooine");
        node.Reserve(0.5, background: true);
        metrics.Refresh([node], brokerUp: true, PlacementMode.Active);

        var text = await ScrapeAsync(metrics);
        Assert.Contains("tentacle_node_state{node=\"tatooine\",state=\"Ready\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_state{node=\"tatooine\",state=\"Disconnected\"} 0", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_slots{node=\"tatooine\",class=\"all\"} 4", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_slots_used{node=\"tatooine\",class=\"background\"} 0.5", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_info{node=\"tatooine\",agent_version=\"0.3.0\",ffmpeg_version=\"8.1.3\",host=\"tatooine\",arch=\"\",vendor=\"none\",gpu=\"\",api=\"none\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_capability{node=\"tatooine\",capability=\"encode:h264\"} 0", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_check{node=\"tatooine\",check=\"root:/data\",required=\"false\"} 0", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_broker_up 1", text, StringComparison.Ordinal);

        // It reconnects with a new agent: the old info series goes.
        metrics.Refresh([Node("tatooine", agent: "0.4.0")], brokerUp: true, PlacementMode.Active);
        text = await ScrapeAsync(metrics);
        Assert.DoesNotContain("agent_version=\"0.3.0\"", text, StringComparison.Ordinal);
        Assert.Contains("agent_version=\"0.4.0\"", text, StringComparison.Ordinal);

        // It disconnects: still listed, as Disconnected, without slots or checks.
        metrics.Refresh([], brokerUp: true, PlacementMode.Active);
        text = await ScrapeAsync(metrics);
        Assert.Contains("tentacle_node_state{node=\"tatooine\",state=\"Disconnected\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_state{node=\"tatooine\",state=\"Ready\"} 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tentacle_node_slots{node=\"tatooine\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tentacle_node_check{node=\"tatooine\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tentacle_node_info{node=\"tatooine\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tentacle_node_capability{node=\"tatooine\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NodeGoneBeforeAnyScrapeIsDisconnected()
    {
        var metrics = new TentacleMetrics();
        metrics.NodeSeen("mandalore");
        metrics.Refresh([], brokerUp: true, PlacementMode.Active);
        Assert.Contains("tentacle_node_state{node=\"mandalore\",state=\"Disconnected\"} 1", await ScrapeAsync(metrics), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunningJobsAreSeriesUntilTheyEnd()
    {
        var metrics = new TentacleMetrics();
        var job = new JobRecord { Id = "1-1", Kind = "Transcode", Node = "mandalore", StartedAt = DateTimeOffset.FromUnixTimeSeconds(1000), User = "bigo", Item = "Coyote vs. Acme (2026)" };

        // By default nobody is named.
        metrics.RefreshRunning([job]);
        Assert.DoesNotContain("bigo", await ScrapeAsync(metrics), StringComparison.Ordinal);
        job.User = null;
        job.Item = null;
        metrics.RefreshRunning([job], nameViewers: true);
        Assert.Contains("tentacle_running_job_start_time_seconds{job_id=\"1-1\",node=\"mandalore\",kind=\"Transcode\",user=\"\",client=\"\",item=\"\"} 1000", await ScrapeAsync(metrics), StringComparison.Ordinal);

        // Attributed: the series is replaced, not duplicated.
        job.User = "bigo";
        job.Item = "Coyote vs. Acme (2026)";
        metrics.RefreshRunning([job], nameViewers: true);
        var text = await ScrapeAsync(metrics);
        Assert.Contains("user=\"bigo\",client=\"\",item=\"Coyote vs. Acme (2026)\"} 1000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("user=\"\"", text, StringComparison.Ordinal);

        // Ended: gone.
        job.Outcome = "ok";
        metrics.RefreshRunning([job], nameViewers: true);
        Assert.DoesNotContain("job_id=\"1-1\"", await ScrapeAsync(metrics), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServersSlotsAreNodeLocal()
    {
        var metrics = new TentacleMetrics();
        var local = new LocalSeat { Slots = 2 };
        local.Reserve(1, false);
        metrics.Refresh([], brokerUp: true, PlacementMode.Active, local);
        var text = await ScrapeAsync(metrics);
        Assert.Contains("tentacle_node_slots{node=\"local\",class=\"all\"} 2", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_slots_used{node=\"local\",class=\"all\"} 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectOnlyTentaclesLeaveNoSeriesBehind()
    {
        var metrics = new TentacleMetrics();
        var probe = new TentacleNode(new Hello { Node = "dagobah", Arch = "arm64", MaxJobs = 0, DetectOnly = true, FfmpegVersion = "ffmpeg version 8.1.2-Jellyfin" });
        probe.ApplyVerification([new NodeCheck("ffmpeg", false, "PatchDiffers"), new NodeCheck("root:/config/cache/transcodes", false, "missing on the tentacle", Required: true)], [], false, incompatible: true);
        metrics.NodeSeen("dagobah", detectOnly: true);
        metrics.Refresh([probe], brokerUp: true, PlacementMode.Active);
        var text = await ScrapeAsync(metrics);

        // Nothing it reports can page: its checks are optional, its state is DetectOnly.
        Assert.Contains("tentacle_node_state{node=\"dagobah\",state=\"DetectOnly\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("tentacle_node_state{node=\"dagobah\",state=\"Incompatible\"} 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("tentacle_node_check{node=\"dagobah\",check=\"root:/config/cache/transcodes\",required=\"true\"}", text, StringComparison.Ordinal);

        // Taken away: gone, not Disconnected.
        metrics.Refresh([], brokerUp: true, PlacementMode.Active);
        text = await ScrapeAsync(metrics);
        Assert.DoesNotContain("node=\"dagobah\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("least-loaded", "least-loaded")]
    [InlineData("", "\"\"")]
    [InlineData("path-ineligible:/m/a b.mkv outcome=ok", "\"path-ineligible:/m/a b.mkv outcome=ok\"")]
    [InlineData("x\n[INF] forged", "\"x\\n[INF] forged\"")]
    [InlineData("q\"\\", "\"q\\\"\\\\\"")]
    public void LogfmtValuesCannotForgeFieldsOrLines(string raw, string expected)
        => Assert.Equal(expected, Broker.Logfmt(raw));
}
