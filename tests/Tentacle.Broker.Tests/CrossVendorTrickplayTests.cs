using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

/// <summary>
/// Cross-vendor trickplay: the host predicts what Jellyfin will start, and the broker
/// runs the rebuild only for a shim that brings exactly that.
/// </summary>
public class CrossVendorTrickplayTests
{
    private const string Temp = "/config/cache/temp/jellyfin";
    private const string Media = "/data/movies/a.mkv";
    private const string Predicted = Temp + "/" + Broker.TrickplayOutputPlaceholder + "/%08d.jpg";
    private const string Run = Temp + "/40aca63192f348a5bcb758f036ece786/%08d.jpg";
    private static readonly string[] Roots = ["/data/movies", Temp];

    private static readonly string[] Qsv =
    [
        "-loglevel", "error", "-init_hw_device", "vaapi=va:/dev/dri/renderD128,driver=iHD", "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs",
        "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi", "-i", "file:" + Media, "-map", "0:0", "-an", "-sn",
        "-vf", "fps=0.1,scale_vaapi=w=320:h=180,hwmap=derive_device=qsv,format=qsv", "-threads", "1", "-c:v", "mjpeg_qsv", "-global_quality:v", "91",
        "-f", "image2", Predicted,
    ];

    private static readonly string[] Amd =
    [
        "-loglevel", "error", "-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-filter_hw_device", "va",
        "-hwaccel", "vaapi", "-hwaccel_output_format", "vaapi", "-i", "file:" + Media, "-map", "0:0", "-an", "-sn",
        "-vf", "fps=0.1,scale_vaapi=w=320:h=180,hwmap,format=nv12", "-threads", "1", "-c:v", "mjpeg", "-qscale:v", "4",
        "-f", "image2", Predicted,
    ];

    private static string[] At(string[] argv, string output) => [.. argv[..^1], output];

    private static TentacleNode Node(string name)
    {
        var gpu = new GpuInfo { Device = "/dev/dri/renderD128", Vendor = "amd", Api = "vaapi" };
        var node = new TentacleNode(new Hello { Node = name, Host = name, Arch = "x64", MaxJobs = 2, MaxBackgroundJobs = 1, Gpu = gpu });
        node.ApplyVerification([], Roots, hardwareOk: false, incompatible: false, foreignHardwareOk: true);
        return node;
    }

    private static Broker BrokerWith(PlacementMode placement, PlacementMode crossVendor) => new(
        new BrokerOptions
        {
            Placement = placement,
            CrossVendor = crossVendor,
            SharedRoots = () => [],
            ServerFfmpegVersion = () => "8.1.3",
            HardwareTest = () => new HardwareTest("qsv", ["-x"], "intel"),
        },
        NullLogger<Broker>.Instance);

    [Fact]
    public void ATrickplayJobIsPlacedOnAForeignGpuWithinItsBackgroundSlots()
    {
        var analysis = ArgvAnalysis.Analyze("ffmpeg", At(Qsv, Run));
        Assert.Equal(JobKind.Trickplay, analysis.Kind);
        var amd = Node("coruscant");
        Assert.Same(amd, Scheduler.PlaceForeign(analysis, PlacementMode.Active, [amd], null, "intel", _ => true));

        // Its background share (one slot: two trickplay jobs at 0.5) taken: trickplay never
        // takes playback's room.
        amd.Reserve(0.5, background: true);
        Assert.Same(amd, Scheduler.PlaceForeign(analysis, PlacementMode.Active, [amd], null, "intel", _ => true));
        amd.Reserve(0.5, background: true);
        Assert.Null(Scheduler.PlaceForeign(analysis, PlacementMode.Active, [amd], null, "intel", _ => true));
    }

    [Fact]
    public void ExactlyThePredictedCommandLineRunsRebuilt()
    {
        var broker = BrokerWith(PlacementMode.Active, PlacementMode.Active);
        var amd = Node("coruscant");
        broker.Nodes.Add(amd);
        broker.NoteTrickplay(amd, [new TrickplayAttempt(Qsv, Amd)], Temp, shadow: false);

        var note = broker.MatchTrickplay(At(Qsv, Run));
        Assert.NotNull(note);
        Assert.False(note.Shadow);
        Assert.Equal("Trickplay", note.Kind);
        Assert.Equal(At(Amd, Run), note.Argv);
        Assert.Contains("mjpeg_qsv", note.Original, StringComparison.Ordinal);
        Assert.Contains("-qscale:v 4", note.Rewritten, StringComparison.Ordinal);

        // A prediction is used once.
        Assert.Null(broker.MatchTrickplay(At(Qsv, Run)));
    }

    [Theory]
    [InlineData("/config/cache/temp/jellyfin/40ACA63192F348A5BCB758F036ECE786/%08d.jpg")]
    [InlineData("/config/cache/temp/jellyfin/40aca631/%08d.jpg")]
    [InlineData("/config/cache/temp/other/40aca63192f348a5bcb758f036ece786/%08d.jpg")]
    [InlineData("/config/cache/temp/jellyfin/40aca63192f348a5bcb758f036ece786/../../x/%08d.jpg")]
    [InlineData("/config/cache/temp/jellyfin/40aca63192f348a5bcb758f036ece786/%08d.png")]
    public void AnythingButAFreshRunDirectoryBelowThePredictedRootIsNotAMatch(string output)
    {
        var broker = BrokerWith(PlacementMode.Active, PlacementMode.Active);
        var amd = Node("coruscant");
        broker.Nodes.Add(amd);
        broker.NoteTrickplay(amd, [new TrickplayAttempt(Qsv, Amd)], Temp, shadow: false);
        Assert.Null(broker.MatchTrickplay(At(Qsv, output)));
    }

    [Fact]
    public void AnyOtherTokenIsNotAMatch()
    {
        var broker = BrokerWith(PlacementMode.Active, PlacementMode.Active);
        var amd = Node("coruscant");
        broker.Nodes.Add(amd);
        broker.NoteTrickplay(amd, [new TrickplayAttempt(Qsv, Amd)], Temp, shadow: false);
        Assert.Null(broker.MatchTrickplay(At(Qsv.Select(a => a == "file:" + Media ? "file:/data/movies/b.mkv" : a).ToArray(), Run)));
        Assert.Null(broker.MatchTrickplay(At([.. Qsv[..^1], "-y", Predicted], Run)));
    }

    [Fact]
    public async Task InShadowTheJobRunsAsBuiltAndShowsTheRebuild()
    {
        var broker = BrokerWith(PlacementMode.Active, PlacementMode.DryRun);
        var amd = Node("coruscant");
        broker.NoteTrickplay(amd, [new TrickplayAttempt(Qsv, Amd)], Temp, shadow: true);

        var replies = await RunShimAsync(broker, At(Qsv, Run));

        var record = Assert.Single(broker.History.Snapshot());
        Assert.Contains("mjpeg_qsv", record.Command, StringComparison.Ordinal);
        Assert.Contains("-c:v mjpeg -qscale:v 4", record.RewrittenCommand, StringComparison.Ordinal);
        Assert.Contains(Run, record.RewrittenCommand, StringComparison.Ordinal);
        Assert.Equal("coruscant", record.RewrittenFor);
        Assert.DoesNotContain(replies, f => f.Type == FrameType.Placement && f.Read(ProtocolJson.Default.Placement).Refused.Length > 0);
        Assert.Contains("tentacle_cross_vendor_total{node=\"coruscant\",outcome=\"shadow\",kind=\"Trickplay\"} 1", await ScrapeAsync(broker), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenItsTentacleIsGoneJellyfinsOwnCommandLineRuns()
    {
        // Unlike a transcode, the server's command line is still there: nothing is refused.
        var broker = BrokerWith(PlacementMode.Active, PlacementMode.Active);
        var amd = Node("coruscant");
        broker.NoteTrickplay(amd, [new TrickplayAttempt(Qsv, Amd)], Temp, shadow: false);

        var replies = await RunShimAsync(broker, At(Qsv, Run));

        var record = Assert.Single(broker.History.Snapshot());
        Assert.Contains("mjpeg_qsv", record.Command, StringComparison.Ordinal);
        Assert.Null(record.OriginalCommand);
        Assert.Equal("local", record.Node);
        Assert.DoesNotContain(replies, f => f.Type == FrameType.Placement && f.Read(ProtocolJson.Default.Placement).Refused.Length > 0);
        Assert.Contains("outcome=\"skipped:unavailable\",kind=\"Trickplay\"} 1", await ScrapeAsync(broker), StringComparison.Ordinal);
    }

    [Fact]
    public void OffNotesNothing()
    {
        var broker = BrokerWith(PlacementMode.Active, PlacementMode.Disabled);
        var amd = Node("coruscant");
        broker.Nodes.Add(amd);
        broker.NoteTrickplay(amd, [new TrickplayAttempt(Qsv, Amd)], Temp, shadow: false);
        Assert.Null(broker.MatchTrickplay(At(Qsv, Run)));
    }

    [Fact]
    public void APredictionWhoseOutputIsNotTheRunDirectoryIsNotNoted()
    {
        var broker = BrokerWith(PlacementMode.Active, PlacementMode.Active);
        var amd = Node("coruscant");
        broker.Nodes.Add(amd);
        string[] elsewhere = [.. Amd[..^1], "/tmp/x/%08d.jpg"];
        broker.NoteTrickplay(amd, [new TrickplayAttempt(Qsv, elsewhere)], Temp, shadow: false);
        Assert.Null(broker.MatchTrickplay(At(Qsv, Run)));
    }

    private static async Task<string> ScrapeAsync(Broker broker)
    {
        using var stream = new MemoryStream();
        await broker.Metrics.Registry.CollectAndExportAsTextAsync(stream, CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<Frame[]> RunShimAsync(Broker broker, string[] args)
    {
        var input = new MemoryStream();
        await StreamFrames.WriteAsync(input, Frame.Json(FrameType.Start, new StartRequest { Args = args }, ProtocolJson.Default.StartRequest), CancellationToken.None);
        input.Position = 0;
        var output = new MemoryStream();

        // No tentacle connected; the stream's EOF stands for ffmpeg exiting.
        await broker.HandleShimAsync(input, output, null, CancellationToken.None);
        output.Position = 0;
        var frames = new System.Collections.Generic.List<Frame>();
        while (await StreamFrames.ReadAsync(output, CancellationToken.None) is { } frame)
        {
            frames.Add(frame);
        }

        return frames.ToArray();
    }
}
