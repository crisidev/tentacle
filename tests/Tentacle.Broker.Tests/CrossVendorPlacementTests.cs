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

public class CrossVendorPlacementTests
{
    private const string Output = "/config/cache/transcodes/x.m3u8";
    private static readonly string[] Roots = ["/config/cache/transcodes", "/data/movies"];

    // What an Intel server builds (QSV) and what the plugin rebuilds it to for an AMD or
    // NVIDIA tentacle: the shapes Jellyfin's EncodingHelper produces.
    private static readonly string[] QsvArgs = ["-init_hw_device", "vaapi=va:/dev/dri/renderD128,driver=iHD", "-init_hw_device", "qsv=qs@va", "-hwaccel", "vaapi", "-i", "/data/movies/a.mkv", "-codec:v:0", "h264_qsv", "-f", "hls", Output];
    private static readonly string[] AmdArgs = ["-init_hw_device", "vaapi=va:/dev/dri/renderD128,driver=radeonsi", "-filter_hw_device", "va", "-hwaccel", "vaapi", "-i", "/data/movies/a.mkv", "-codec:v:0", "h264_vaapi", "-f", "hls", Output];
    private static readonly string[] NvidiaArgs = ["-init_hw_device", "cuda=cu:0", "-filter_hw_device", "cu", "-hwaccel", "cuda", "-i", "/data/movies/a.mkv", "-codec:v:0", "h264_nvenc", "-f", "hls", Output];

    private static ArgvAnalysis Analyze(string[] args) => ArgvAnalysis.Analyze("ffmpeg", args);

    private static GpuInfo Gpu(string vendor, string api) => new() { Device = "/dev/dri/renderD128", Vendor = vendor, Api = api };

    private static TentacleNode Node(string name, GpuInfo? gpu, bool hardwareOk, bool foreignOk, int slots = 2)
    {
        var node = new TentacleNode(new Hello { Node = name, Host = name, Arch = "x64", MaxJobs = slots, MaxBackgroundJobs = 1, Gpu = gpu });
        node.ApplyVerification([], Roots, hardwareOk, incompatible: false, foreignHardwareOk: foreignOk);
        return node;
    }

    private static LocalSeat Seat(int slots) => new() { Slots = slots };

    [Fact]
    public void ARebuiltCommandGoesOnlyToItsVendorAndNeverRunsHere()
    {
        var intel = Node("mandalore", Gpu("intel", "qsv"), hardwareOk: true, foreignOk: false);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);

        var decision = Scheduler.Place(Analyze(AmdArgs), PlacementMode.Active, [intel, amd], Seat(4), serverVendor: "intel");
        Assert.Same(amd, decision.Node);
        Assert.Equal("cross-vendor:amd-vaapi", decision.Reason);

        // No AMD tentacle with room: not this server, whose GPU cannot run it.
        amd.Reserve(1, false);
        amd.Reserve(1, false);
        var full = Scheduler.Place(Analyze(AmdArgs), PlacementMode.Active, [intel, amd], Seat(4), serverVendor: "intel");
        Assert.Null(full.Node);
        Assert.True(Scheduler.IsCapacity(full.Reason), full.Reason);

        // Its own test is what counts: the server's (HardwareOk) does not admit it.
        var untested = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: true, foreignOk: false);
        Assert.Equal("no-hardware-tentacle", Scheduler.Place(Analyze(AmdArgs), PlacementMode.Active, [untested], null, "intel").Reason);
    }

    [Fact]
    public void APlainVaapiRebuildFollowsTheHostsWord()
    {
        // Jellyfin's VAAPI pipeline for an AMD GPU names no driver, so no vendor.
        string[] plain = ["-init_hw_device", "vaapi=va:/dev/dri/renderD128", "-filter_hw_device", "va", "-hwaccel", "vaapi", "-i", "/data/movies/a.mkv", "-codec:v:0", "h264_vaapi", "-f", "hls", Output];
        Assert.Null(Analyze(plain).HardwareVendor);

        var intel = Node("mandalore", Gpu("intel", "qsv"), hardwareOk: true, foreignOk: false);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);

        // Rebuilt for AMD: the AMD tentacle, not the Intel one, not this server.
        var rebuilt = Scheduler.Place(Analyze(plain), PlacementMode.Active, [intel, amd], Seat(4), "intel", rebuiltFor: "amd");
        Assert.Same(amd, rebuilt.Node);
        Assert.Equal("cross-vendor:amd-vaapi", rebuilt.Reason);
        amd.Reserve(1, false);
        amd.Reserve(1, false);
        Assert.Null(Scheduler.Place(Analyze(plain), PlacementMode.Active, [intel, amd], Seat(4), "intel", rebuiltFor: "amd").Node);

        // Not rebuilt (no word from the host): the server's own rules, as before.
        Assert.NotSame(amd, Scheduler.Place(Analyze(plain), PlacementMode.Active, [intel, amd], null, "intel").Node);
    }

    [Fact]
    public void TheServersOwnCommandsNeverGoToAForeignGpu()
    {
        // A passed foreign test only admits command lines rebuilt for that GPU.
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        Assert.Equal("no-hardware-tentacle", Scheduler.Place(Analyze(QsvArgs), PlacementMode.Active, [amd], null, "intel").Reason);

        // And an NVIDIA rebuild never lands on the AMD tentacle.
        Assert.Equal("no-hardware-tentacle", Scheduler.Place(Analyze(NvidiaArgs), PlacementMode.Active, [amd], null, "intel").Reason);
    }

    [Fact]
    public void WithoutAServerVendorNothingIsForeign()
    {
        // Software servers and unknown VAAPI devices: the old rules, unchanged.
        Assert.False(Scheduler.IsForeignJob(Analyze(AmdArgs), null));
        Assert.False(Scheduler.IsForeignJob(Analyze(AmdArgs), "amd"));
        Assert.True(Scheduler.IsForeignJob(Analyze(AmdArgs), "intel"));
        Assert.True(Scheduler.IsForeignJob(Analyze(NvidiaArgs), "intel"));
        Assert.False(Scheduler.IsForeignJob(Analyze(QsvArgs), "intel"));
    }

    [Fact]
    public void AForeignGpuTakesTheJobsTheServersGpusCannot()
    {
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);

        // No Intel tentacle: the job would run on this server for want of one.
        Assert.Same(amd, Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Active, [amd], null, "intel", _ => true));

        // The host cannot rebuild it (codec, pipeline): leave it as built.
        Assert.Null(Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Active, [amd], null, "intel", _ => false));

        // Shadow mode plans too, to show where it would have gone.
        Assert.Same(amd, Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.DryRun, [amd], null, "intel", _ => true));
        Assert.Null(Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Disabled, [amd], null, "intel", _ => true));

        // Jobs that use no hardware need no rebuilding.
        var remux = Analyze(["-i", "/data/movies/a.mkv", "-c:v", "copy", "-f", "hls", Output]);
        Assert.Null(Scheduler.PlaceForeign(remux, PlacementMode.Active, [amd], null, "intel", _ => true));
    }

    [Fact]
    public void AForeignGpuCompetesOnLoadWithTheServersGpus()
    {
        var intel = Node("mandalore", Gpu("intel", "qsv"), hardwareOk: true, foreignOk: false);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);

        // Both idle: a tie keeps the job as built, on the server's kind of GPU.
        Assert.Null(Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Active, [intel, amd], null, "intel", _ => true));

        // The Intel tentacle is busier: the AMD tentacle wins on load.
        intel.Reserve(1, false);
        Assert.Same(amd, Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Active, [intel, amd], null, "intel", _ => true));

        // The AMD tentacle is busier: the job stays as built, for Intel.
        amd.Reserve(1, false);
        amd.Reserve(1, false);
        Assert.Null(Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Active, [intel, amd], null, "intel", _ => true));

        // The Intel tentacle is full: the AMD tentacle, while it has room.
        var spare = Node("coruscant2", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        intel.Reserve(1, false);
        Assert.Same(spare, Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Active, [intel, amd, spare], null, "intel", _ => true));
    }

    [Fact]
    public void AForeignGpuOnlyBeatsThisServerOnLoad()
    {
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        var seat = Seat(4);
        amd.Reserve(1, false);

        // This server is idle and competes for jobs: it keeps the job.
        Assert.Null(Scheduler.PlaceForeign(Analyze(QsvArgs), PlacementMode.Active, [amd], seat, "intel", _ => true));
    }

    [Fact]
    public void AForeignGpuIsTestedWithItsOwnHardwareType()
    {
        var qsv = new HardwareTest("qsv", ["-x"], "intel");
        Assert.True(Broker.IsForeign(Gpu("amd", "vaapi"), qsv));
        Assert.True(Broker.IsForeign(Gpu("nvidia", "nvenc"), qsv));
        Assert.False(Broker.IsForeign(Gpu("intel", "qsv"), qsv));
        Assert.False(Broker.IsForeign(new GpuInfo { Vendor = "amd", Api = Hardware.NoApi }, qsv));
        Assert.False(Broker.IsForeign(Gpu("amd", "vaapi"), new HardwareTest("vaapi", ["-x"])));
        Assert.False(Broker.IsForeign(Gpu("amd", "vaapi"), null));
    }

    [Fact]
    public async Task ShadowModeRecordsWhereARebuiltJobWouldHaveRun()
    {
        var options = new BrokerOptions
        {
            Placement = PlacementMode.DryRun,
            CrossVendor = PlacementMode.Active,
            SharedRoots = () => [],
            ServerFfmpegVersion = () => "8.1.3",
            HardwareTest = () => new HardwareTest("qsv", ["-x"], "intel"),
        };
        var broker = new Broker(options, NullLogger<Broker>.Instance);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        broker.NoteCrossVendor(Output, amd, "ffmpeg " + string.Join(' ', QsvArgs), "ffmpeg " + string.Join(' ', AmdArgs), AmdArgs, shadow: true);

        await RunShimAsync(broker, QsvArgs);

        var record = Assert.Single(broker.History.Snapshot());
        Assert.Equal("local", record.Node);
        Assert.Equal("coruscant", record.WouldRunOn);
        Assert.Equal("dry-run:cross-vendor:amd-vaapi", record.Reason);
        Assert.Contains("h264_vaapi", record.RewrittenCommand, StringComparison.Ordinal);
        Assert.Contains("h264_qsv", record.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARebuiltJobWithNoTentacleIsRefusedNotRunHere()
    {
        var options = new BrokerOptions
        {
            Placement = PlacementMode.Active,
            CrossVendor = PlacementMode.Active,
            SharedRoots = () => [],
            ServerFfmpegVersion = () => "8.1.3",
            HardwareTest = () => new HardwareTest("qsv", ["-x"], "intel"),
            NoFallbackWait = TimeSpan.Zero,
        };
        var broker = new Broker(options, NullLogger<Broker>.Instance);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        broker.NoteCrossVendor(Output, amd, "ffmpeg " + string.Join(' ', QsvArgs), "ffmpeg " + string.Join(' ', AmdArgs), AmdArgs, shadow: false);

        // The tentacle it was rebuilt for is gone: an AMD command line cannot run on Intel.
        var replies = await RunShimAsync(broker, AmdArgs);
        Assert.Contains(replies, f => f.Type == FrameType.Placement && f.Read(ProtocolJson.Default.Placement).Refused.Length > 0);
    }

    [Fact]
    public async Task AJobThatIsNotTheRecordedRebuildIsRefused()
    {
        var options = new BrokerOptions
        {
            Placement = PlacementMode.Active,
            CrossVendor = PlacementMode.Active,
            SharedRoots = () => [],
            ServerFfmpegVersion = () => "8.1.3",
            HardwareTest = () => new HardwareTest("qsv", ["-x"], "intel"),
        };
        var broker = new Broker(options, NullLogger<Broker>.Instance);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        broker.NoteCrossVendor(Output, amd, "ffmpeg " + string.Join(' ', QsvArgs), "ffmpeg " + string.Join(' ', AmdArgs), AmdArgs, shadow: false);

        // Something changed the command line between the plugin and the shim: an extra
        // input. Neither the tentacle nor this server runs it.
        string[] tampered = [.. AmdArgs[..^1], "-i", "/etc/passwd", Output];
        var replies = await RunShimAsync(broker, tampered);
        var placement = Assert.Single(replies, f => f.Type == FrameType.Placement).Read(ProtocolJson.Default.Placement);
        Assert.NotEmpty(placement.Refused);
        Assert.Equal("cross-vendor-mismatch", placement.Reason);
        var record = Assert.Single(broker.History.Snapshot());
        Assert.Equal("refused", record.Outcome);
    }

    [Fact]
    public async Task ARebuiltJobNoTentacleCanTakeIsRefusedWhateverTheReason()
    {
        var options = new BrokerOptions
        {
            Placement = PlacementMode.Active,
            CrossVendor = PlacementMode.Active,
            SharedRoots = () => [],
            ServerFfmpegVersion = () => "8.1.3",
            HardwareTest = () => new HardwareTest("qsv", ["-x"], "intel"),
        };
        var broker = new Broker(options, NullLogger<Broker>.Instance);

        // Its tentacle no longer proves the library: not a capacity reason, still refused.
        var amd = new TentacleNode(new Hello { Node = "coruscant", Host = "coruscant", Arch = "x64", MaxJobs = 2, Gpu = Gpu("amd", "vaapi") });
        amd.ApplyVerification([], ["/config/cache/transcodes"], hardwareOk: false, incompatible: false, foreignHardwareOk: true);
        broker.Nodes.Add(amd);
        broker.NoteCrossVendor(Output, amd, "ffmpeg " + string.Join(' ', QsvArgs), "ffmpeg " + string.Join(' ', AmdArgs), AmdArgs, shadow: false);

        var replies = await RunShimAsync(broker, AmdArgs);
        var placement = Assert.Single(replies, f => f.Type == FrameType.Placement).Read(ProtocolJson.Default.Placement);
        Assert.StartsWith("path-ineligible:", placement.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(placement.Refused);
    }

    [Fact]
    public void AnUntrustedGpuIsDroppedAtHello()
    {
        var hello = new Hello { Node = "coruscant", Gpu = new GpuInfo { Vendor = "amd", Driver = "amdgpu", Api = "vaapi", Device = "/dev/dri/renderD128 -i /etc/passwd" } };
        Assert.Contains("not a render node", Broker.DropUntrustedGpu(hello), StringComparison.Ordinal);
        Assert.Null(hello.Gpu);

        // The node it makes takes only jobs that use no hardware.
        var node = new TentacleNode(hello);
        Assert.Null(node.Gpu);

        var honest = new Hello { Node = "coruscant", Gpu = Gpu("amd", "vaapi") };
        honest.Gpu.Driver = "amdgpu";
        Assert.Null(Broker.DropUntrustedGpu(honest));
        Assert.NotNull(honest.Gpu);
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

    private static async Task<string> ScrapeAsync(Broker broker)
    {
        using var stream = new MemoryStream();
        await broker.Metrics.Registry.CollectAndExportAsTextAsync(stream, CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Theory]
    [InlineData(PlacementMode.Active, PlacementMode.Active, PlacementMode.Active)]
    [InlineData(PlacementMode.Active, PlacementMode.DryRun, PlacementMode.DryRun)]
    [InlineData(PlacementMode.Active, PlacementMode.Disabled, PlacementMode.Disabled)]
    [InlineData(PlacementMode.DryRun, PlacementMode.Active, PlacementMode.DryRun)]
    [InlineData(PlacementMode.DryRun, PlacementMode.DryRun, PlacementMode.DryRun)]
    [InlineData(PlacementMode.Disabled, PlacementMode.Active, PlacementMode.Disabled)]
    [InlineData(PlacementMode.Disabled, PlacementMode.DryRun, PlacementMode.Disabled)]
    public void CrossVendorNeverGoesFurtherThanWhereJobsRun(PlacementMode overall, PlacementMode own, PlacementMode effective)
    {
        Assert.Equal(effective, Broker.EffectiveCrossVendor(overall, own));
        Assert.Equal(effective, BrokerWith(overall, own).CrossVendorMode);
    }

    [Fact]
    public async Task ShadowWhileJobsRunForRealRunsTheJobAsBuiltAndRecordsTheRebuild()
    {
        var broker = BrokerWith(PlacementMode.Active, crossVendor: PlacementMode.DryRun);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);

        // Even a note that asks to run the rebuild is only recorded in shadow.
        broker.NoteCrossVendor(Output, amd, "ffmpeg " + string.Join(' ', QsvArgs), "ffmpeg " + string.Join(' ', AmdArgs), AmdArgs, shadow: false);
        var replies = await RunShimAsync(broker, QsvArgs);

        // Jellyfin's own command line, placed by the usual rules (here: no tentacle, so here).
        var usual = Scheduler.Place(Analyze(QsvArgs), PlacementMode.Active, [], broker.Local, "intel");
        var record = Assert.Single(broker.History.Snapshot());
        Assert.Contains("h264_qsv", record.Command, StringComparison.Ordinal);
        Assert.Equal(usual.Reason, record.Reason);
        Assert.Equal("local", record.Node);
        Assert.Null(record.WouldRunOn);
        Assert.DoesNotContain(replies, f => f.Type == FrameType.Placement && f.Read(ProtocolJson.Default.Placement).Refused.Length > 0);

        // And what it would have been.
        Assert.Contains("h264_vaapi", record.RewrittenCommand, StringComparison.Ordinal);
        Assert.Equal("coruscant", record.RewrittenFor);
        Assert.Contains("tentacle_cross_vendor_total{node=\"coruscant\",outcome=\"shadow\"} 1", await ScrapeAsync(broker), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveWhileJobsAreInShadowIsShadow()
    {
        var broker = BrokerWith(PlacementMode.DryRun, crossVendor: PlacementMode.Active);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        Assert.Same(amd, Scheduler.PlaceForeign(Analyze(QsvArgs), broker.CrossVendorMode, [amd], null, "intel", _ => true));

        broker.NoteCrossVendor(Output, amd, "ffmpeg " + string.Join(' ', QsvArgs), "ffmpeg " + string.Join(' ', AmdArgs), AmdArgs, shadow: false);
        await RunShimAsync(broker, QsvArgs);

        var record = Assert.Single(broker.History.Snapshot());
        Assert.Equal("local", record.Node);
        Assert.Equal("dry-run:cross-vendor:amd-vaapi", record.Reason);
        Assert.Equal("coruscant", record.WouldRunOn);
        Assert.Contains("h264_vaapi", record.RewrittenCommand, StringComparison.Ordinal);
        Assert.Contains("outcome=\"shadow\"} 1", await ScrapeAsync(broker), StringComparison.Ordinal);
        Assert.DoesNotContain("outcome=\"rebuilt\"", await ScrapeAsync(broker), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OffRecordsNothing()
    {
        var broker = BrokerWith(PlacementMode.Active, crossVendor: PlacementMode.Disabled);
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: false, foreignOk: true);
        broker.Nodes.Add(amd);

        Assert.Null(broker.PlanCrossVendor(Analyze(QsvArgs), _ => true));
        broker.NoteCrossVendor(Output, amd, "ffmpeg " + string.Join(' ', QsvArgs), "ffmpeg " + string.Join(' ', AmdArgs), AmdArgs, shadow: true);
        await RunShimAsync(broker, QsvArgs);

        var record = Assert.Single(broker.History.Snapshot());
        Assert.Null(record.RewrittenCommand);
        Assert.Null(record.RewrittenFor);
        Assert.Null(record.OriginalCommand);
        Assert.DoesNotContain("tentacle_cross_vendor_total{", await ScrapeAsync(broker), StringComparison.Ordinal);
    }

    private static async Task<Frame[]> RunShimAsync(Broker broker, string[] args)
    {
        var input = new MemoryStream();
        var start = new StartRequest { Args = args };
        await StreamFrames.WriteAsync(input, Frame.Json(FrameType.Start, start, ProtocolJson.Default.StartRequest), CancellationToken.None);
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
