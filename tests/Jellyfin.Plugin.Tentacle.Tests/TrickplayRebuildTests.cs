using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Tentacle.CrossVendor;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Tentacle.Protocol;
using Xunit;
using EncodingOptions = MediaBrowser.Model.Configuration.EncodingOptions;

namespace Jellyfin.Plugin.Tentacle.Tests;

/// <summary>
/// Trickplay, the way Jellyfin 12's media encoder builds it, from Jellyfin 12.1's own
/// EncodingHelper: the server's command line predicted exactly, and rebuilt for an AMD
/// (VAAPI) and an NVIDIA (NVENC) tentacle.
/// </summary>
public class TrickplayRebuildTests
{
    private const string Temp = "/config/cache/temp/jellyfin";

    // The server's devices are /dev/null in these tests: Jellyfin writes a render node only
    // when the file exists where it builds the command line, and /dev/null exists on every
    // host (CI runners have no /dev/dri). The fixtures say /dev/dri/renderD128, as recorded.
    private const string ServerDevice = "/dev/null";

    private static readonly Version Ffmpeg = new(8, 1, 3);

    private static readonly GpuInfo Amd = new()
    {
        Device = "/dev/dri/renderD128",
        Vendor = Hardware.Amd,
        Driver = "amdgpu",
        Api = "vaapi",
        Capabilities = ["encode:h264", "encode:hevc", "encode:hevc10", "decode:h264", "decode:hevc", "decode:hevc10", "decode:vp9"],
    };

    private static readonly GpuInfo Nvidia = new()
    {
        Device = "/dev/nvidia0",
        Vendor = Hardware.Nvidia,
        Driver = "nvidia",
        Api = "nvenc",
        Capabilities = ["encode:h264", "encode:hevc", "decode:h264", "decode:hevc", "decode:vp9", "decode:av1"],
    };

    // An Intel QSV server set up as the one the fixtures were recorded on.
    private static EncodingOptions ServerOptions() => new()
    {
        HardwareAccelerationType = HardwareAccelerationType.qsv,
        QsvDevice = ServerDevice,
        VaapiDevice = ServerDevice,
        EnableHardwareEncoding = true,
        PreferSystemNativeHwDecoder = true,
        HardwareDecodingCodecs = ["h264", "hevc", "mpeg2video", "vc1", "vp9", "av1"],
        EnableDecodingColorDepth10Hevc = true,
        EnableIntelLowPowerH264HwEncoder = true,
        EnableIntelLowPowerHevcHwEncoder = true,
        AllowHevcEncoding = true,
        EnableVppTonemapping = true,
    };

    private static IMediaEncoder ServerEncoder() => Fakes.Create<IMediaEncoder>(
        ("get_EncoderPath", _ => "/usr/lib/jellyfin-ffmpeg/ffmpeg"),
        ("get_EncoderVersion", _ => Ffmpeg),
        ("get_IsVaapiDeviceInteliHD", _ => true),
        ("SupportsHwaccel", _ => true),
        ("SupportsEncoder", _ => true),
        ("SupportsDecoder", _ => true),
        ("SupportsFilter", _ => true),
        ("SupportsFilterWithOption", _ => true),
        ("SupportsBitStreamFilterWithOption", _ => true),
        ("GetInputPathArgument", a => a![0] is EncodingJobInfo job ? $"file:\"{job.MediaPath}\"" : $"file:\"{a[0]}\""));

    private static EncodingHelper Helper(IMediaEncoder encoder)
    {
        var paths = Fakes.Create<IApplicationPaths>(("get_CachePath", _ => "/config/cache"), ("get_TempDirectory", _ => Temp));
        var configuration = Fakes.Create<MediaBrowser.Common.Configuration.IConfigurationManager>(("get_CommonApplicationPaths", _ => paths));
        return new EncodingHelper(paths, encoder, Fakes.Create<ISubtitleEncoder>(), new ConfigurationBuilder().Build(), configuration, Fakes.Create<IPathManager>());
    }

    // Trickplay as the server above asks for it: 320 px every 10 s, quality 4, one thread.
    private static TrickplayRequest Request(string path, int width = 1920, int height = 1080, float rate = 23.976f, bool keyFramesOnly = false)
    {
        var video = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = "h264",
            Width = width,
            Height = height,
            PixelFormat = "yuv420p",
            BitDepth = 8,
            AverageFrameRate = rate,
            RealFrameRate = rate,
            ColorPrimaries = "bt709",
            ColorTransfer = "bt709",
            ColorSpace = "bt709",
            IsDefault = true,
        };
        var media = new MediaSourceInfo
        {
            Id = "0123456789abcdef0123456789abcdef",
            Path = path,
            Protocol = MediaProtocol.File,
            Container = "mkv",
            MediaStreams = [video, new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" }],
        };
        return new TrickplayRequest(path, "mkv", media, video, 320, TimeSpan.FromSeconds(10), AllowHwAccel: true, EnableHwEncoding: true, Threads: 1, QualityScale: 4, keyFramesOnly);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".args")).TrimEnd();

    // A prediction the way the fixtures were recorded: the server's render node, a run's output directory.
    private static string AsRecorded(string predicted) => predicted
        .Replace("vaapi=va:" + ServerDevice + ",", "vaapi=va:/dev/dri/renderD128,", StringComparison.Ordinal)
        .Replace(TrickplayCommand.OutputPlaceholder, "0123456789abcdef0123456789abcdef", StringComparison.Ordinal);

    private static string Server(TrickplayRequest request) => Assert.Single(TrickplayCommand.Build(Helper(ServerEncoder()), ServerOptions(), Ffmpeg, 2, Temp, request));

    private static (string Command, EncodingOptions Options) Worker(GpuInfo gpu, TrickplayRequest request)
    {
        var options = WorkerEncodingOptions.For(ServerOptions(), gpu)!;
        var built = Assert.Single(TrickplayCommand.Build(Helper(new WorkerMediaEncoder(ServerEncoder(), gpu)), options, Ffmpeg, 2, Temp, request));
        return (CommandRebuilder.PinVaapiDevice(built, options), options);
    }

    [Fact]
    public void TheServersCommandLineIsPredictedExactly()
    {
        Assert.Equal(Fixture("trickplay-mjpeg_qsv"), AsRecorded(Server(Request("/data/movies/Test (2026)/Test (2026).mkv"))));
    }

    [Fact]
    public void AScopeFilmIsPredictedExactly()
    {
        // 2.39:1 at 25 fps: the height and the frame rate come from Jellyfin's helper too.
        Assert.Equal(Fixture("trickplay-mjpeg_qsv-scope"), AsRecorded(Server(Request("/data/tv/Show/Season 1/Show - S01E01.mkv", 1920, 792, 25f))));
    }

    [Fact]
    public void ForAnAmdGpuJellyfinDecodesAndScalesOnItAndEncodesJpegInSoftware()
    {
        var request = Request("/data/movies/Test (2026)/Test (2026).mkv");
        var (rebuilt, _) = Worker(Amd, request);

        // Jellyfin's own AMD pipeline: Renoir-class GPUs have no VAAPI JPEG encoder.
        Assert.Contains("-init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va -hwaccel vaapi -hwaccel_output_format vaapi", rebuilt, StringComparison.Ordinal);
        Assert.Contains("scale_vaapi=w=320:h=180", rebuilt, StringComparison.Ordinal);
        Assert.Contains("-c:v mjpeg -qscale:v 4", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("qsv", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("iHD", rebuilt, StringComparison.Ordinal);
        AssertSafe(Server(request), rebuilt, Amd);
    }

    [Fact]
    public void ForAnNvidiaGpuJellyfinUsesCuda()
    {
        var request = Request("/data/movies/Test (2026)/Test (2026).mkv");
        var (rebuilt, _) = Worker(Nvidia, request);
        Assert.Contains("-init_hw_device cuda=cu:0 -filter_hw_device cu -hwaccel cuda", rebuilt, StringComparison.Ordinal);
        Assert.Contains("scale_cuda=w=320:h=180", rebuilt, StringComparison.Ordinal);
        Assert.Contains("-c:v mjpeg", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("qsv", rebuilt, StringComparison.Ordinal);
        AssertSafe(Server(request), rebuilt, Nvidia);
    }

    [Fact]
    public void KeyFrameOnlyExtractionPredictsBothAttempts()
    {
        // Jellyfin tries -skip_frame nokey first and falls back to the plain command line.
        var request = Request("/data/movies/Test (2026)/Test (2026).mkv", keyFramesOnly: true);
        var server = TrickplayCommand.Build(Helper(ServerEncoder()), ServerOptions(), Ffmpeg, 2, Temp, request);
        Assert.Equal(2, server.Count);
        Assert.StartsWith("-loglevel error -skip_frame nokey ", server[0], StringComparison.Ordinal);
        Assert.Equal(server[0].Replace("-skip_frame nokey ", string.Empty, StringComparison.Ordinal), server[1]);
        Assert.DoesNotContain("setpts=", server[0], StringComparison.Ordinal);

        var options = WorkerEncodingOptions.For(ServerOptions(), Amd)!;
        var worker = TrickplayCommand.Build(Helper(new WorkerMediaEncoder(ServerEncoder(), Amd)), options, Ffmpeg, 2, Temp, Request("/data/movies/Test (2026)/Test (2026).mkv", keyFramesOnly: true));
        Assert.Equal(2, worker.Count);
        for (var i = 0; i < 2; i++)
        {
            AssertSafe(server[i], CommandRebuilder.PinVaapiDevice(worker[i], options), Amd);
        }
    }

    [Fact]
    public void WithoutHardwareTrickplayThereIsNothingToRebuild()
    {
        // Key-frame-only on QSV without the native decoder: Jellyfin decodes in software,
        // which any tentacle runs as it is.
        var options = ServerOptions();
        options.PreferSystemNativeHwDecoder = false;
        var server = TrickplayCommand.Build(Helper(ServerEncoder()), options, Ffmpeg, 2, Temp, Request("/data/movies/Test (2026)/Test (2026).mkv", keyFramesOnly: true));
        Assert.All(server, s => Assert.False(ArgvAnalysis.Analyze("ffmpeg", ProcessArguments.Split(s), "/").UsesHardware));
        Assert.All(server, s => Assert.Contains("-threads 1 -i file:", s, StringComparison.Ordinal));
    }

    [Fact]
    public void ARebuildThatTouchesMoreThanTheGpuIsRefused()
    {
        var request = Request("/data/movies/Test (2026)/Test (2026).mkv");
        var server = ProcessArguments.Split(Server(request));
        var rebuilt = ProcessArguments.Split(Worker(Amd, request).Command);
        Assert.Null(CommandRebuilder.WhatChanged(server, rebuilt));

        var otherOutput = rebuilt.Take(rebuilt.Count - 1).Append(Temp + "/" + TrickplayCommand.OutputPlaceholder + "/../../x/%08d.jpg").ToArray();
        Assert.Equal("output", CommandRebuilder.WhatChanged(server, otherOutput));
        var otherInput = rebuilt.Select(a => a.StartsWith("file:", StringComparison.Ordinal) ? "file:/etc/passwd" : a).ToArray();
        Assert.Equal("inputs", CommandRebuilder.WhatChanged(server, otherInput));
        var otherStream = rebuilt.Select(a => a == "0:0" ? "0:1" : a).ToArray();
        Assert.Equal("options", CommandRebuilder.WhatChanged(server, otherStream));
    }

    private static void AssertSafe(string server, string rebuilt, GpuInfo gpu)
    {
        Assert.Null(CommandRebuilder.WhatChanged(ProcessArguments.Split(server), ProcessArguments.Split(rebuilt)));
        Assert.True(CrossVendorPlanner.IsFor(rebuilt, gpu));
        Assert.EndsWith("-f image2 \"" + Temp + "/" + TrickplayCommand.OutputPlaceholder + "/%08d.jpg\"", rebuilt, StringComparison.Ordinal);
    }
}
