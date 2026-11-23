using System;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Tentacle.CrossVendor;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Tentacle.Protocol;
using Xunit;
using EncodingOptions = MediaBrowser.Model.Configuration.EncodingOptions;

namespace Jellyfin.Plugin.Tentacle.Tests;

/// <summary>
/// Rebuilding with Jellyfin 12.1's own EncodingHelper: an Intel QSV server's
/// transcodes, rebuilt for an AMD (VAAPI) and an NVIDIA (NVENC) tentacle.
/// </summary>
public class RebuildTests
{
    private const string Media = "/data/movies/Test (2026)/Test (2026).mkv";

    // What an AMD Radeon (Barcelo, renoir) tentacle's agent measures.
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

    private static EncodingOptions ServerOptions() => new()
    {
        HardwareAccelerationType = HardwareAccelerationType.qsv,
        QsvDevice = "/dev/dri/renderD128",
        EnableHardwareEncoding = true,
        HardwareDecodingCodecs = ["h264", "hevc", "vp9", "av1"],
        EnableDecodingColorDepth10Hevc = true,
        EnableIntelLowPowerH264HwEncoder = true,
        EnableIntelLowPowerHevcHwEncoder = true,
        AllowHevcEncoding = true,
        EnableTonemapping = true,
    };

    private static IMediaEncoder ServerEncoder() => Fakes.Create<IMediaEncoder>(
        ("get_EncoderPath", _ => "/usr/lib/jellyfin-ffmpeg/ffmpeg"),
        ("get_EncoderVersion", _ => new Version(7, 1, 1)),
        ("get_IsVaapiDeviceInteliHD", _ => true),
        ("SupportsHwaccel", _ => true),
        ("SupportsEncoder", _ => true),
        ("SupportsDecoder", _ => true),
        ("SupportsFilter", _ => true),
        ("SupportsFilterWithOption", _ => true),
        ("SupportsBitStreamFilterWithOption", _ => true),
        ("EscapeSubtitleFilterPath", a => a![0]),
        ("GetInputPathArgument", a => a![0] is EncodingJobInfo job ? $"file:\"{job.MediaPath}\"" : $"file:\"{a[0]}\""),
        ("GetInputArgument", a => $"file:\"{(a![0] is string s ? s : Media)}\""));

    private static EncodingHelper Helper(IMediaEncoder encoder)
    {
        var paths = Fakes.Create<IApplicationPaths>(("get_CachePath", _ => "/config/cache"), ("get_TempDirectory", _ => "/config/cache/temp"));
        var configuration = Fakes.Create<MediaBrowser.Common.Configuration.IConfigurationManager>(("get_CommonApplicationPaths", _ => paths));
        return new EncodingHelper(paths, encoder, Fakes.Create<ISubtitleEncoder>(), new ConfigurationBuilder().Build(), configuration, Fakes.Create<IPathManager>());
    }

    private static StreamState State(EncodingHelper helper, EncodingOptions options, TranscodingJobType type, string sourceCodec = "hevc", string colorTransfer = "bt709")
    {
        var media = new MediaSourceInfo
        {
            Id = "0123456789abcdef0123456789abcdef",
            Path = Media,
            Protocol = MediaProtocol.File,
            Container = "mkv",
            RunTimeTicks = TimeSpan.FromMinutes(45).Ticks,
            SupportsTranscoding = true,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = sourceCodec,
                    Width = 1920,
                    Height = 1080,
                    BitRate = 8_000_000,
                    PixelFormat = "yuv420p",
                    BitDepth = 8,
                    Profile = "Main",
                    Level = 120,
                    IsDefault = true,
                    AverageFrameRate = 23.976f,
                    RealFrameRate = 23.976f,
                    ColorPrimaries = colorTransfer == "bt709" ? "bt709" : "bt2020",
                    ColorTransfer = colorTransfer,
                    ColorSpace = colorTransfer == "bt709" ? "bt709" : "bt2020nc",
                },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", Channels = 2, SampleRate = 48000, BitRate = 192_000, IsDefault = true },
            ],
        };
        var request = new VideoRequestDto
        {
            Id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            MediaSourceId = media.Id,
            DeviceId = "test",
            PlaySessionId = "test",
            VideoCodec = "h264",
            AudioCodec = "aac",
            MaxWidth = 1920,
            VideoBitRate = 6_000_000,
            AudioBitRate = 192_000,
            SegmentContainer = "mp4",
        };
        var state = new StreamState(null!, type, null!) { Request = request };
        helper.AttachMediaSourceInfo(state, options, media, string.Empty);
        state.OutputVideoCodec = "h264";
        state.OutputAudioCodec = "aac";
        state.OutputContainer = "mp4";
        state.OutputFilePath = "/config/cache/transcodes/0123456789abcdef0123456789abcdef.mp4";
        return state;
    }

    // The HLS command line the way Jellyfin's HLS controller lays it out, from the same
    // public EncodingHelper calls: what StartFfMpeg receives for this state.
    private static string HlsCommand(EncodingHelper helper, EncodingOptions options, StreamState state)
    {
        var encoder = helper.GetVideoEncoder(state, options);
        var filters = helper.GetVideoProcessingFilterParam(state, options, encoder);
        var video = helper.GetNegativeMapArgsByFilters(state, filters) + "-codec:v:0 " + encoder
            + helper.GetVideoQualityParam(state, encoder, options, EncoderPreset.veryfast)
            + helper.GetHlsVideoKeyFrameArguments(state, encoder, state.SegmentLength, false, 0)
            + filters;
        var threads = EncodingHelper.GetNumberOfThreads(state, options, encoder).ToString(CultureInfo.InvariantCulture);
        return string.Join(
            ' ',
            helper.GetInputModifier(state, options, "mp4"),
            helper.GetInputArgument(state, options, "mp4"),
            "-map_metadata -1 -map_chapters -1 -threads " + threads,
            helper.GetMapArgs(state),
            video,
            "-codec:a:0 libfdk_aac -vbr:a 5 -copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 -f hls -max_delay 5000000 -hls_time 3 -hls_segment_type fmp4",
            "-start_number 0 -hls_segment_filename \"/config/cache/transcodes/0123456789abcdef0123456789abcdef%d.mp4\" -hls_playlist_type vod -hls_list_size 0 -y \"/config/cache/transcodes/0123456789abcdef0123456789abcdef.m3u8\"");
    }

    private static RebuildResult Rebuild(GpuInfo gpu, TranscodingJobType type, Func<EncodingHelper, EncodingOptions, StreamState, string> command, string sourceCodec = "hevc", string colorTransfer = "bt709")
    {
        var serverEncoder = ServerEncoder();
        var server = Helper(serverEncoder);
        var options = ServerOptions();
        var state = State(server, options, type, sourceCodec, colorTransfer);
        var original = command(server, options, state);
        Assert.Contains("qsv", original, StringComparison.Ordinal);
        var result = CommandRebuilder.Rebuild(server, options, Helper(new WorkerMediaEncoder(serverEncoder, gpu)), WorkerEncodingOptions.For(options, gpu)!, state, original, type);

        // What Jellyfin builds for the other GPU passes the whole-command check the
        // planner applies: only the GPU's parts differ.
        if (result.Arguments is { } rebuilt)
        {
            Assert.Null(CommandRebuilder.WhatChanged(ProcessArguments.Split(original), ProcessArguments.Split(rebuilt)));
        }

        return result;
    }

    private static string Progressive(EncodingHelper helper, EncodingOptions options, StreamState state)
        => helper.GetProgressiveVideoFullCommandLine(state, options, EncoderPreset.superfast);

    private static void AssertFor(GpuInfo gpu, string rebuilt)
    {
        Assert.DoesNotContain("qsv", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("driver=iHD", rebuilt, StringComparison.Ordinal);
        Assert.True(CrossVendorPlanner.IsFor(rebuilt, gpu));
        var analysis = ArgvAnalysis.Analyze("ffmpeg", ProcessArguments.Split(rebuilt), "/");
        Assert.Equal(Media, Assert.Single(analysis.Inputs).Replace("file:", string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    // Jellyfin leaves the node out when the file does not exist where it builds the
    // command (the server): renderD129 here, or a CI runner with no /dev/dri at all.
    [InlineData("-f matroska -init_hw_device vaapi=va -filter_hw_device va", "-f matroska -init_hw_device vaapi=va:/dev/dri/renderD129 -filter_hw_device va")]
    [InlineData("-init_hw_device vaapi=va:,vendor_id=0x1002 -hwaccel vaapi", "-init_hw_device vaapi=va:/dev/dri/renderD129,vendor_id=0x1002 -hwaccel vaapi")]
    [InlineData("-x -init_hw_device vaapi=va", "-x -init_hw_device vaapi=va:/dev/dri/renderD129")]
    // A node Jellyfin did write stays as it is.
    [InlineData("-init_hw_device vaapi=va:/dev/dri/renderD129 -filter_hw_device va", "-init_hw_device vaapi=va:/dev/dri/renderD129 -filter_hw_device va")]
    [InlineData("-i file:\"/data/movies/a.mkv\" -codec:v:0 libx264", "-i file:\"/data/movies/a.mkv\" -codec:v:0 libx264")]
    public void TheTentaclesRenderNodeIsNamedWhateverTheServerHas(string built, string expected)
    {
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, VaapiDevice = "/dev/dri/renderD129" };
        Assert.Equal(expected, CommandRebuilder.PinVaapiDevice(built, options));
    }

    [Theory]
    [InlineData(HardwareAccelerationType.vaapi, "/dev/dri/renderD128 -i /etc/passwd")]
    [InlineData(HardwareAccelerationType.vaapi, "../renderD128")]
    [InlineData(HardwareAccelerationType.vaapi, "")]
    [InlineData(HardwareAccelerationType.nvenc, "/dev/dri/renderD128")]
    public void OnlyAValidatedVaapiRenderNodeIsPinned(HardwareAccelerationType type, string device)
    {
        const string Built = "-init_hw_device vaapi=va -filter_hw_device va";
        Assert.Equal(Built, CommandRebuilder.PinVaapiDevice(Built, new EncodingOptions { HardwareAccelerationType = type, VaapiDevice = device }));
    }

    [Fact]
    public void AProgressiveTranscodeIsRebuiltForAnAmdGpu()
    {
        var result = Rebuild(Amd, TranscodingJobType.Progressive, Progressive);
        Assert.Equal("rebuilt", result.Outcome);
        // Plain VAAPI on the tentacle's device: Jellyfin's pipeline for an AMD GPU.
        Assert.Contains("-init_hw_device vaapi=va:/dev/dri/renderD128 -filter_hw_device va -hwaccel vaapi", result.Arguments, StringComparison.Ordinal);
        Assert.Contains("-codec:v:0 h264_vaapi", result.Arguments, StringComparison.Ordinal);
        Assert.Contains("scale_vaapi=", result.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("hwmap", result.Arguments, StringComparison.Ordinal);
        AssertFor(Amd, result.Arguments!);
    }

    [Fact]
    public void AnHlsTranscodeIsRebuiltForAnAmdGpu()
    {
        var result = Rebuild(Amd, TranscodingJobType.Hls, HlsCommand);
        Assert.Equal("rebuilt", result.Outcome);
        Assert.Contains("-codec:v:0 h264_vaapi", result.Arguments, StringComparison.Ordinal);
        Assert.Contains("-init_hw_device vaapi=va:/dev/dri/renderD128", result.Arguments, StringComparison.Ordinal);
        Assert.Contains("-hls_segment_type fmp4", result.Arguments, StringComparison.Ordinal);
        Assert.EndsWith("-y \"/config/cache/transcodes/0123456789abcdef0123456789abcdef.m3u8\"", result.Arguments, StringComparison.Ordinal);
        AssertFor(Amd, result.Arguments!);
    }

    [Fact]
    public void AnHlsTranscodeIsRebuiltForAnNvidiaGpu()
    {
        var result = Rebuild(Nvidia, TranscodingJobType.Hls, HlsCommand);
        Assert.Equal("rebuilt", result.Outcome);
        Assert.Contains("-codec:v:0 h264_nvenc", result.Arguments, StringComparison.Ordinal);
        AssertFor(Nvidia, result.Arguments!);
    }

    [Fact]
    public void ACommandLineThatIsNotJellyfinsIsLeftAlone()
    {
        // One byte off what Jellyfin would build for this state: not this job.
        var result = Rebuild(Amd, TranscodingJobType.Progressive, (h, o, s) => Progressive(h, o, s).Replace("-threads", "-threads ", StringComparison.Ordinal));
        Assert.Equal("skipped:no-match:command", result.Outcome);

        var hls = Rebuild(Amd, TranscodingJobType.Hls, (h, o, s) => HlsCommand(h, o, s).Replace("-codec:v:0 h264_qsv", "-c:v h264_qsv", StringComparison.Ordinal));
        Assert.Equal("skipped:no-match:encoder", hls.Outcome);
    }

    [Fact]
    public void ACommandStillForTheServersGpuIsNotForTheTentacle()
    {
        var qsv = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hls-h264_qsv.args")).TrimEnd();
        Assert.False(CrossVendorPlanner.IsFor(qsv, Amd));
        Assert.False(CrossVendorPlanner.IsFor(qsv, Nvidia));
        Assert.False(CrossVendorPlanner.IsFor("-i a.mkv -c:v libx264 out.mp4", Amd));
    }

    [Fact]
    public void HdrSourcesAreNotRebuilt()
    {
        var result = Rebuild(Amd, TranscodingJobType.Hls, HlsCommand, colorTransfer: "smpte2084");
        Assert.Equal("skipped:hdr", result.Outcome);
    }

    [Fact]
    public void WorkerSettingsAreTheTentaclesGpu()
    {
        var options = WorkerEncodingOptions.For(ServerOptions(), Amd)!;
        Assert.Equal(HardwareAccelerationType.vaapi, options.HardwareAccelerationType);
        Assert.Equal("/dev/dri/renderD128", options.VaapiDevice);
        Assert.Equal(["h264", "hevc", "vp9"], options.HardwareDecodingCodecs);
        Assert.True(options.EnableDecodingColorDepth10Hevc);
        Assert.True(options.AllowHevcEncoding);
        Assert.False(options.AllowAv1Encoding);
        Assert.False(options.EnableIntelLowPowerH264HwEncoder);
        Assert.False(options.EnableTonemapping);

        // Unchanged: everything that is not about the GPU.
        var server = ServerOptions();
        server.H264Crf = 21;
        server.EncoderPreset = EncoderPreset.fast;
        var copy = WorkerEncodingOptions.For(server, Amd)!;
        Assert.Equal(21, copy.H264Crf);
        Assert.Equal(EncoderPreset.fast, copy.EncoderPreset);

        Assert.Null(WorkerEncodingOptions.For(ServerOptions(), new GpuInfo { Vendor = Hardware.Amd, Api = Hardware.NoApi }));
        Assert.True(WorkerEncodingOptions.CanEncode(Amd, "h265"));
        Assert.False(WorkerEncodingOptions.CanEncode(Amd, "av1"));
    }
}
