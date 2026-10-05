using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

public class HardwarePlacementTests
{
    private static readonly string[] Roots = ["/config/cache/transcodes", "/data/movies"];

    private static ArgvAnalysis Qsv()
        => ArgvAnalysis.Analyze("ffmpeg", ["-init_hw_device", "vaapi=va:/dev/dri/renderD128,driver=iHD", "-init_hw_device", "qsv=qs@va", "-hwaccel", "vaapi", "-i", "/data/movies/a.mkv", "-c:v", "h264_qsv", "-f", "hls", "/config/cache/transcodes/x.m3u8"]);

    private static ArgvAnalysis Remux()
        => ArgvAnalysis.Analyze("ffmpeg", ["-i", "/data/movies/a.mkv", "-c:v", "copy", "-f", "hls", "/config/cache/transcodes/x.m3u8"]);

    private static TentacleNode Node(string name, GpuInfo? gpu, bool hardwareOk, bool detectOnly = false, string arch = "x64")
    {
        var node = new TentacleNode(new Hello { Node = name, Host = name, Arch = arch, MaxJobs = detectOnly ? 0 : 2, DetectOnly = detectOnly, Gpu = gpu });
        node.ApplyVerification([], Roots, hardwareOk, incompatible: false);
        return node;
    }

    private static GpuInfo Gpu(string vendor, string api) => new() { Device = "/dev/dri/renderD128", Vendor = vendor, Api = api };

    [Fact]
    public void HardwareJobsStayWithTheirVendor()
    {
        // An AMD tentacle that (somehow) passed the self-test still never gets a QSV job.
        var amd = Node("coruscant", Gpu("amd", "vaapi"), hardwareOk: true);
        Assert.Equal("no-hardware-tentacle", Scheduler.Place(Qsv(), PlacementMode.Active, [amd]).Reason);
        var intel = Node("mandalore", Gpu("intel", "qsv"), hardwareOk: true);
        Assert.Same(intel, Scheduler.Place(Qsv(), PlacementMode.Active, [amd, intel]).Node);
    }

    [Fact]
    public void JobsWithoutHardwareRunAnywhere()
    {
        var cpu = Node("scarif", gpu: null, hardwareOk: false);
        Assert.Same(cpu, Scheduler.Place(Remux(), PlacementMode.Active, [cpu]).Node);
        Assert.Equal("no-hardware-tentacle", Scheduler.Place(Qsv(), PlacementMode.Active, [cpu]).Reason);
    }

    [Fact]
    public void DetectOnlyTentaclesNeverGetJobs()
    {
        var probe = Node("dagobah", gpu: null, hardwareOk: false, detectOnly: true, arch: "arm64");
        Assert.Equal(NodeState.DetectOnly, probe.State);
        Assert.False(probe.IsSchedulable);
        Assert.Equal(0, probe.MaxJobs);
        Assert.Equal("no-ready-tentacle", Scheduler.Place(Remux(), PlacementMode.Active, [probe]).Reason);
    }

    [Fact]
    public void TheServersTestRunsOnlyWhereItCanPass()
    {
        var qsv = new HardwareTest("qsv", ["-x"], "intel");
        Assert.Null(Broker.WhyNoHardwareTest(Node("mandalore", Gpu("intel", "qsv"), false), qsv));
        Assert.Contains("amd GPU", Broker.WhyNoHardwareTest(Node("coruscant", Gpu("amd", "vaapi"), false), qsv), System.StringComparison.Ordinal);
        Assert.Contains("no GPU", Broker.WhyNoHardwareTest(Node("scarif", null, false), qsv), System.StringComparison.Ordinal);

        // Agents before 0.7 report no GPU facts: tested as before.
        Assert.Null(Broker.WhyNoHardwareTest(Node("old", null, false, arch: string.Empty), qsv));

        // VAAPI on a server whose device vendor is unknown: any GPU may try.
        Assert.Null(Broker.WhyNoHardwareTest(Node("coruscant", Gpu("amd", "vaapi"), false), new HardwareTest("vaapi", ["-x"])));
    }
}
