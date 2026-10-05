using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Protocol.Tests;

public class ArgvAnalysisTests
{
    private static ArgvAnalysis Analyze(string args, string binary = "ffmpeg", string? cwd = null)
        => ArgvAnalysis.Analyze(binary, Split(args), cwd);

    // Good enough for test fixtures: whitespace split, "" means an empty argument.
    private static string[] Split(string args)
        => System.Array.ConvertAll(args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries), a => a == "\"\"" ? string.Empty : a);

    [Theory]
    [InlineData("-version")]
    [InlineData("-hide_banner -encoders")]
    [InlineData("-h filter=scale_vaapi")]
    public void InfoQueriesAreRecognised(string args)
    {
        Assert.True(Analyze(args).InfoQuery);
    }

    [Fact]
    public void ValidationProbesHaveNoRealInput()
    {
        // EncoderValidator's pkey-pause probe and hwaccel probe.
        Assert.False(Analyze("-hide_banner -re -f lavfi -i nullsrc=s=1x1:r=1:d=5 -f null -").HasRealInput);
        Assert.False(Analyze("-loglevel quiet -hwaccel_flags +low_priority -f lavfi -i nullsrc -f null -").HasRealInput);
    }

    [Fact]
    public void HlsTranscodeIsATranscodeWithItsPaths()
    {
        var a = Analyze("-analyzeduration 200M -probesize 1G -f matroska -i file:/data/movies/A/a.mkv -map_metadata -1 -codec:v:0 libx264 "
            + "-f hls -max_delay 5000000 -hls_time 6 -hls_segment_type mpegts -start_number 0 "
            + "-hls_segment_filename /config/cache/transcodes/abc%d.ts -y /config/cache/transcodes/abc.m3u8");
        Assert.Equal(JobKind.Transcode, a.Kind);
        Assert.True(a.HasRealInput);
        Assert.False(a.UsesHardware);
        Assert.Contains("/data/movies/A/a.mkv", a.Paths);
        Assert.Contains("/config/cache/transcodes/abc%d.ts", a.Paths);
        Assert.Contains("/config/cache/transcodes/abc.m3u8", a.Paths);
    }

    [Fact]
    public void HardwareAndDevicesAreDetectedButDevicesAreNotPaths()
    {
        var a = Analyze("-init_hw_device vaapi=va:/dev/dri/renderD128 -init_hw_device qsv=qs@va -filter_hw_device qs -hwaccel qsv -i /data/tv/x.mkv -f mp4 /config/cache/transcodes/x.mp4");
        Assert.True(a.UsesHardware);
        Assert.DoesNotContain(a.Paths, p => p.StartsWith("/dev/", System.StringComparison.Ordinal));
    }

    [Fact]
    public void PathsInsideFiltersAreFound()
    {
        var a = Analyze("-i /data/movies/m.mkv -vf subtitles=f='/config/data/data/subtitles/ab/123.ass':fontsdir='/config/data/data/attachments/ab/123' -f hls /config/cache/transcodes/t.m3u8");
        Assert.Contains("/config/data/data/subtitles/ab/123.ass", a.Paths);
        Assert.Contains("/config/data/data/attachments/ab/123", a.Paths);
    }

    [Fact]
    public void ImagesSplitIntoTrickplayAndSingle()
    {
        Assert.Equal(JobKind.Trickplay, Analyze("-loglevel error -i /data/m.mkv -an -sn -vf fps=1/10 -f image2 /config/cache/temp/jellyfin/g/%08d.jpg").Kind);
        Assert.Equal(JobKind.ImageSingle, Analyze("-ss 10 -i /data/m.mkv -vframes 1 -f image2 /config/cache/temp/jellyfin/g.jpg").Kind);
    }

    [Fact]
    public void ExtractionAnalysisAndProbe()
    {
        Assert.Equal(JobKind.Extract, Analyze("-nostdin -y -i /data/m.mkv -map 0:3 -an -vn -c:s srt /config/data/data/subtitles/a/b.srt").Kind);
        Assert.Equal(JobKind.Analysis, Analyze("-hide_banner -i /data/music/a.flac -af ebur128=framelog=verbose -f null -").Kind);
        Assert.Equal(JobKind.Probe, Analyze("-i /data/m.mkv -print_format json -show_streams", "ffprobe").Kind);
    }

    [Fact]
    public void DumpAttachmentIntoCwdCountsTheCwd()
    {
        var a = Analyze("-dump_attachment:t \"\" -y -i /data/m.mkv -t 0 -f null null", cwd: "/config/data/data/attachments/ab/123");
        Assert.Equal(JobKind.Extract, a.Kind);
        Assert.True(a.NeedsCwd);
        Assert.Contains("/config/data/data/attachments/ab/123", a.Paths);
        Assert.False(Analyze("-i /data/m.mkv -f hls /config/cache/transcodes/t.m3u8", cwd: "/run/s6").NeedsCwd);
    }

    [Fact]
    public void LoopbackInputsAreFlagged()
    {
        Assert.True(Analyze("-i http://127.0.0.1:8096/LiveTv/LiveStreamFiles/x/stream.ts -f mpegts /config/cache/transcodes/l.ts").UsesLoopback);
        Assert.False(Analyze("-i http://10.42.0.15:8096/LiveTv/LiveStreamFiles/x/stream.ts -f mpegts /config/cache/transcodes/l.ts").UsesLoopback);
    }

    [Theory]
    [InlineData("/config/cache/transcodes/a.ts", "/config/cache/transcodes", true)]
    [InlineData("/config/cache/transcodes", "/config/cache/transcodes/", true)]
    [InlineData("/config/cache/transcodes2/a.ts", "/config/cache/transcodes", false)]
    [InlineData("/data/movies/x.mkv", "/data", true)]
    [InlineData("/data/media/../../etc/passwd", "/data/media", false)]
    [InlineData("/data/media/..", "/data/media", false)]
    [InlineData("/data/media/./x.mkv", "/data/media", false)]
    [InlineData("/data/media/..x/a.mkv", "/data/media", true)]
    public void IsUnder(string path, string root, bool expected)
    {
        Assert.Equal(expected, ArgvAnalysis.IsUnder(path, root));
    }

    [Fact]
    public void InputsAndOutputAreKnown()
    {
        var hls = Analyze("-f matroska -i file:/data/movies/A/a.mkv -i /data/movies/A/a.en.srt -codec:v:0 libx264 -f hls "
            + "-hls_segment_filename /config/cache/transcodes/abc%d.ts -y /config/cache/transcodes/abc.m3u8");
        Assert.Equal(["/data/movies/A/a.mkv", "/data/movies/A/a.en.srt"], hls.Inputs);
        Assert.Equal("/config/cache/transcodes/abc.m3u8", hls.Output);

        // lavfi inputs and stdout outputs are not files.
        var probe = Analyze("-f lavfi -i nullsrc -f null -");
        Assert.Empty(probe.Inputs);
        Assert.Null(probe.Output);
    }
}

public class EnvPolicyTests
{
    [Theory]
    [InlineData("LIBVA_DRIVER_NAME_JELLYFIN", true)]
    [InlineData("TZ", true)]
    [InlineData("LC_ALL", true)]
    [InlineData("LD_PRELOAD", false)]
    [InlineData("PATH", false)]
    [InlineData("GCONV_PATH", false)]
    [InlineData("TENTACLE_JOB_ID", false)]
    [InlineData("LIBVA_DRIVERS_PATH", false)]
    [InlineData("OCL_ICD_FILENAMES", false)]
    [InlineData("OCL_ICD_VENDORS", false)]
    [InlineData("FONTCONFIG_FILE", false)]
    [InlineData("FONTCONFIG_PATH", false)]
    public void OnlyAllowListedVariablesTravel(string name, bool allowed)
    {
        Assert.Equal(allowed, EnvPolicy.IsAllowed(name));
    }

    [Theory]
    [InlineData("LIBVA_DRIVER_NAME", "iHD", true)]
    [InlineData("LIBVA_DRIVER_NAME_JELLYFIN", "i965", true)]
    [InlineData("LIBVA_DRIVER_NAME", "radeonsi", true)]
    [InlineData("LIBVA_DRIVER_NAME", "../../../config/cache/transcodes/x", false)]
    [InlineData("LIBVA_DRIVER_NAME", "/tmp/x", false)]
    [InlineData("LIBVA_DRIVER_NAME", "iHD.so", false)]
    [InlineData("LIBVA_DRIVER_NAME", "", false)]
    [InlineData("TZ", "Europe/London", true)]
    [InlineData("TMPDIR", "/config/cache/temp", true)]
    [InlineData("TZ", "UTC\nX=1", false)]
    [InlineData("TZ", "UTC\u0000", false)]
    [InlineData("LANG", "en_US.UTF-8\n", false)]
    public void DriverNamesAreBareWords(string name, string value, bool allowed)
    {
        Assert.Equal(allowed, EnvPolicy.IsAllowed(name, value));
        Assert.Equal(allowed, EnvPolicy.Filter([new(name, value)]).ContainsKey(name));
    }
}

public class FfmpegVersionTests
{
    [Theory]
    [InlineData("ffmpeg version 8.1.3-Jellyfin Copyright (c) 2000-2026", "ffmpeg version 8.1.3-Jellyfin Copyright (c) 2000-2026", FfmpegMatch.Same)]
    [InlineData("ffmpeg version 8.1.3-Jellyfin Copyright", "ffmpeg version 8.1.2-Jellyfin Copyright", FfmpegMatch.PatchDiffers)]
    [InlineData("ffmpeg version 8.1.3-Jellyfin Copyright", "ffmpeg version 7.1.1-Jellyfin Copyright", FfmpegMatch.Incompatible)]
    [InlineData("ffmpeg version 8.1.3-Jellyfin", "unknown", FfmpegMatch.Unknown)]
    public void ComparesBuilds(string server, string node, FfmpegMatch expected)
    {
        Assert.Equal(expected, FfmpegVersion.Compare(server, node));
    }

}
