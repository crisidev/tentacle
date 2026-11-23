using System;
using System.IO;
using Jellyfin.Plugin.Tentacle.CrossVendor;
using Tentacle.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Tentacle.Tests;

/// <summary>
/// The splice on command lines Jellyfin 12.4 really built (QSV, from a homelab's
/// FFmpeg.Transcode logs; paths and ids anonymised), with the parts an AMD (VAAPI)
/// tentacle's settings change.
/// </summary>
public class SpliceTests
{
    private const string QsvInput = "-init_hw_device vaapi=va:/dev/dri/renderD128,driver=iHD -init_hw_device qsv=qs@va -filter_hw_device qs -hwaccel vaapi -hwaccel_output_format vaapi -noautorotate -i file:\"/data/movies/Test (2026)/Test (2026).mkv\"";
    private const string AmdInput = "-init_hw_device vaapi=va:/dev/dri/renderD128,driver=radeonsi -filter_hw_device va -hwaccel vaapi -hwaccel_output_format vaapi -noautorotate -i file:\"/data/movies/Test (2026)/Test (2026).mkv\"";
    private const string QsvQuality = " -low_power 1 -preset veryfast -mbbrc 1 -b:v 6124743 -maxrate 6124744 -rc_init_occupancy 6124743 -bufsize 12249486 -profile:v:0 high -level 42";
    private const string AmdQuality = " -rc_mode VBR -b:v 6124743 -maxrate 6124744 -bufsize 12249486 -profile:v:0 high -level 42";
    private const string QsvFilters = " -vf \"setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale_vaapi=format=nv12:extra_hw_frames=24,hwmap=derive_device=qsv,format=qsv\"";
    private const string AmdFilters = " -vf \"setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale_vaapi=format=nv12:extra_hw_frames=24\"";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".args")).TrimEnd();

    private static Fragment[] H264QsvToAmd() =>
    [
        new("input-options", "-analyzeduration 200M -probesize 1G -f matroska", "-analyzeduration 200M -probesize 1G -f matroska"),
        new("input", QsvInput, AmdInput),
        new("threads", "-threads 0", "-threads 0"),
        new("filter-maps", string.Empty, string.Empty),
        new("encoder", "-codec:v:0 h264_qsv", "-codec:v:0 h264_vaapi"),
        new("quality", QsvQuality, AmdQuality),
        new("keyframes", " -g:v:0 72 -keyint_min:v:0 72", " -g:v:0 72 -keyint_min:v:0 72"),
        new("filters", QsvFilters, AmdFilters),
    ];

    [Fact]
    public void EveryGpuPartIsReplacedAndNothingElse()
    {
        var original = Fixture("hls-h264_qsv");
        var result = CommandRebuilder.Splice(original, H264QsvToAmd());

        Assert.Equal("rebuilt", result.Outcome);
        var rebuilt = result.Arguments!;
        Assert.DoesNotContain("qsv", rebuilt, StringComparison.Ordinal);
        Assert.DoesNotContain("driver=iHD", rebuilt, StringComparison.Ordinal);
        Assert.Contains("-codec:v:0 h264_vaapi", rebuilt, StringComparison.Ordinal);

        // Audio, muxer and HLS options: byte for byte what Jellyfin built.
        var tail = original[original.IndexOf(" -codec:a:0", StringComparison.Ordinal)..];
        Assert.EndsWith(tail, rebuilt, StringComparison.Ordinal);
        Assert.StartsWith("-analyzeduration 200M -probesize 1G -f matroska ", rebuilt, StringComparison.Ordinal);

        // What the broker sees: an AMD job with the same inputs and output.
        var before = ArgvAnalysis.Analyze("ffmpeg", ProcessArguments.Split(original), "/");
        var after = ArgvAnalysis.Analyze("ffmpeg", ProcessArguments.Split(rebuilt), "/");
        Assert.Equal(Hardware.Intel, before.HardwareVendor);
        Assert.Equal(Hardware.Amd, after.HardwareVendor);
        Assert.Equal(before.Output, after.Output);
        Assert.Equal(before.Inputs, after.Inputs);
        Assert.Equal("/config/cache/transcodes/0123456789abcdef0123456789abcdef.m3u8", after.Output);
    }

    [Fact]
    public void APartNotFoundLeavesTheJobAlone()
    {
        // Another Jellyfin version writes the encoder differently: no guess.
        var fragments = H264QsvToAmd();
        fragments[4] = new("encoder", "-c:v h264_qsv", "-c:v h264_vaapi");
        Assert.Equal("skipped:no-match:encoder", CommandRebuilder.Splice(Fixture("hls-h264_qsv"), fragments).Outcome);
    }

    [Fact]
    public void PartsMustComeInJellyfinsOrder()
    {
        // The filters are written after the encoder; looked for before it, they are not found.
        var fragments = H264QsvToAmd();
        (fragments[4], fragments[7]) = (fragments[7], fragments[4]);
        Assert.StartsWith("skipped:no-match:", CommandRebuilder.Splice(Fixture("hls-h264_qsv"), fragments).Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void APartFoundTwiceLeavesTheJobAlone()
    {
        var fragments = H264QsvToAmd();
        fragments[2] = new("threads", "-map 0:", "-map 0:");
        Assert.Equal("skipped:ambiguous:threads", CommandRebuilder.Splice(Fixture("hls-h264_qsv"), fragments).Outcome);
    }

    [Fact]
    public void ANewPartWithNothingToReplaceLeavesTheJobAlone()
    {
        var fragments = H264QsvToAmd();
        fragments[3] = new("filter-maps", string.Empty, "-map -0:s");
        Assert.Equal("skipped:no-anchor:filter-maps", CommandRebuilder.Splice(Fixture("hls-h264_qsv"), fragments).Outcome);
    }

    [Fact]
    public void ArgumentsSplitAsTheShimReceivesThem()
    {
        var args = ProcessArguments.Split(Fixture("hls-h264_qsv-subs"));
        Assert.Contains("file:/data/movies/Test (2026)/Test (2026).mkv", args);
        Assert.Contains("-filter_complex", args);
        var filter = args[IndexOf(args, "-filter_complex") + 1];
        Assert.StartsWith("alphasrc=", filter, StringComparison.Ordinal);
        Assert.Contains("subtitles=f='/config/data/data/subtitles/", filter, StringComparison.Ordinal);
        Assert.Equal("/config/cache/transcodes/0123456789abcdef0123456789abcdef.m3u8", args[^1]);

        Assert.Equal(["a", "b c", "say \"hi\""], ProcessArguments.Split("a  \"b c\" \"say \\\"hi\\\"\""));
        Assert.Equal([string.Empty], ProcessArguments.Split("\"\""));
    }

    [Theory]
    [InlineData("hls-h264_qsv")]
    [InlineData("hls-h264_qsv-subs")]
    [InlineData("hls-hevc_qsv")]
    [InlineData("progressive-hevc_qsv")]
    public void RecordedCommandsAreIntelJobs(string name)
    {
        var analysis = ArgvAnalysis.Analyze("ffmpeg", ProcessArguments.Split(Fixture(name)), "/");
        Assert.Equal(JobKind.Transcode, analysis.Kind);
        Assert.True(analysis.UsesHardware);
        Assert.Equal(Hardware.Intel, analysis.HardwareVendor);
    }

    private static int IndexOf(System.Collections.Generic.IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
