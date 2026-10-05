using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tentacle.Protocol;

/// <summary>
/// What kind of work an invocation is. Mirrors the classes of the rffmpeg
/// exporter it replaces, with single images split from trickplay.
/// </summary>
public enum JobKind
{
    /// <summary>ffprobe.</summary>
    Probe,

    /// <summary>Playback: HLS or progressive transcode/remux.</summary>
    Transcode,

    /// <summary>Subtitle or attachment extraction.</summary>
    Extract,

    /// <summary>Trickplay tiles (an image2 sequence).</summary>
    Trickplay,

    /// <summary>One image (chapter or primary image extraction); latency-bound.</summary>
    ImageSingle,

    /// <summary>Audio/video analysis (ebur128, chromaprint, blackframe, ...).</summary>
    Analysis,

    /// <summary>Anything else, including Live TV recording.</summary>
    Other,
}

/// <summary>
/// Facts about an ffmpeg/ffprobe invocation, derived from its argv alone.
/// </summary>
public sealed partial class ArgvAnalysis
{
    private static readonly HashSet<string> InfoFlags = new(StringComparer.Ordinal)
    {
        "-version", "-buildconf", "-formats", "-muxers", "-demuxers", "-devices", "-codecs",
        "-decoders", "-encoders", "-bsfs", "-protocols", "-filters", "-pix_fmts", "-layouts",
        "-sample_fmts", "-colors", "-hwaccels", "-h", "-help", "--help", "-?", "-sources", "-sinks", "-L",
    };

    private static readonly string[] AnalysisMarkers = ["chromaprint", "blackframe", "silencedetect", "showinfo", "signalstats", "ebur128"];

    private static readonly string[] ApiPrecedence = ["qsv", "cuda", "nvenc", "amf", "vaapi", "vulkan", "opencl", "drm", "v4l2m2m", "rkmpp"];

    private ArgvAnalysis(JobKind kind, bool infoQuery, bool hasRealInput, bool usesHardware, bool needsCwd, IReadOnlyList<string> paths, IReadOnlyList<string> urls, IReadOnlyList<string> inputs, string? output)
    {
        Inputs = inputs;
        Output = output;
        NeedsCwd = needsCwd;
        Kind = kind;
        InfoQuery = infoQuery;
        HasRealInput = hasRealInput;
        UsesHardware = usesHardware;
        Paths = paths;
        Urls = urls;
    }

    /// <summary>Gets the job kind.</summary>
    public JobKind Kind { get; }

    /// <summary>Gets a value indicating whether this is an information query (-version, -encoders, ...).</summary>
    public bool InfoQuery { get; }

    /// <summary>Gets a value indicating whether there is an input other than lavfi (Jellyfin's validation probes have none).</summary>
    public bool HasRealInput { get; }

    /// <summary>Gets a value indicating whether the job initialises or uses a hardware device.</summary>
    public bool UsesHardware { get; }

    /// <summary>
    /// Gets the hardware API the job drives (qsv, cuda, amf, vaapi, vulkan...), or null.
    /// QSV wins over the VAAPI device it is derived from, CUDA over Vulkan, and so on.
    /// </summary>
    public string? HardwareApi { get; private init; }

    /// <summary>
    /// Gets the GPU vendor the job's command line can only run on (intel, amd, nvidia),
    /// or null when it uses no hardware or does not say (VAAPI without a driver name).
    /// </summary>
    public string? HardwareVendor { get; private init; }

    /// <summary>Gets a value indicating whether the job writes relative to its working directory.</summary>
    public bool NeedsCwd { get; }

    /// <summary>Gets every absolute filesystem path the job names (devices excluded), including the cwd when relevant.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>Gets the files given with -i (file: prefix removed), in order.</summary>
    public IReadOnlyList<string> Inputs { get; }

    /// <summary>Gets the output file (the last argument), when it is a path. For HLS, the playlist.</summary>
    public string? Output { get; }

    /// <summary>Gets every URL the job names.</summary>
    public IReadOnlyList<string> Urls { get; }

    /// <summary>Gets a value indicating whether an input is on loopback, so only the server can read it.</summary>
    public bool UsesLoopback => Urls.Any(IsLoopbackUrl);

    /// <summary>
    /// Analyses an invocation.
    /// </summary>
    /// <param name="binary">"ffmpeg" or "ffprobe".</param>
    /// <param name="args">The arguments, without argv[0].</param>
    /// <param name="cwd">The working directory, counted as a path when the job writes relative to it.</param>
    /// <returns>The analysis.</returns>
    public static ArgvAnalysis Analyze(string binary, IReadOnlyList<string> args, string? cwd = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var info = args.Any(InfoFlags.Contains);
        var realInput = false;
        var hardware = false;
        var paths = new List<string>();
        var urls = new List<string>();
        var inputs = new List<string>();
        string? format = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-f" when i + 1 < args.Count:
                    format = args[i + 1];
                    break;
                case "-i" when i + 1 < args.Count:
                    if (!string.Equals(format, "lavfi", StringComparison.Ordinal))
                    {
                        realInput = true;
                        if (AsPath(args[i + 1]) is { } input)
                        {
                            inputs.Add(input);
                        }
                    }

                    format = null;
                    break;
                case "-hwaccel" or "-init_hw_device" or "-filter_hw_device" or "-hwaccel_device" or "-qsv_device" or "-vaapi_device":
                    hardware = true;
                    break;
                default:
                    break;
            }

            Collect(arg, paths, urls);
        }

        // `-dump_attachment:t ""` writes every attachment into the working directory.
        var needsCwd = false;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i].StartsWith("-dump_attachment", StringComparison.Ordinal) && args[i + 1].Length == 0)
            {
                needsCwd = true;
                if (!string.IsNullOrEmpty(cwd))
                {
                    paths.Add(cwd);
                }

                break;
            }
        }

        var joined = string.Join(' ', args);

        var kind = Classify(binary, args, joined);
        var output = args.Count > 1 && !inputs.Contains(AsPath(args[^1]) ?? string.Empty) ? AsPath(args[^1]) : null;
        var (api, vendor) = HardwareOf(args);
        return new ArgvAnalysis(kind, info, realInput, hardware || api is not null, needsCwd, paths.Distinct(StringComparer.Ordinal).ToArray(), urls.Distinct(StringComparer.Ordinal).ToArray(), inputs, output)
        {
            HardwareApi = api,
            HardwareVendor = vendor,
        };
    }

    /// <summary>
    /// The hardware API and vendor a command line needs: device types given to
    /// -init_hw_device and -hwaccel, and hardware codecs (h264_qsv, hevc_nvenc...).
    /// A VAAPI device names its vendor through driver= (iHD, i965, radeonsi) or vendor_id=.
    /// </summary>
    private static (string? Api, string? Vendor) HardwareOf(IReadOnlyList<string> args)
    {
        var apis = new HashSet<string>(StringComparer.Ordinal);
        string? vaapiVendor = null;
        for (var i = 0; i < args.Count; i++)
        {
            var next = i + 1 < args.Count ? args[i + 1] : string.Empty;
            switch (args[i])
            {
                case "-init_hw_device":
                    var type = next.Split('=', 2)[0];
                    apis.Add(type);
                    if (type == "vaapi")
                    {
                        vaapiVendor ??= VaapiVendor(next);
                    }

                    break;
                case "-hwaccel" when next is not ("none" or "auto" or ""):
                    apis.Add(next);
                    break;
                default:
                    if (args[i].StartsWith("-c", StringComparison.Ordinal) || args[i].StartsWith("-vcodec", StringComparison.Ordinal))
                    {
                        var suffix = next.LastIndexOf('_');
                        if (suffix > 0 && Array.IndexOf(ApiPrecedence, next[(suffix + 1)..]) >= 0)
                        {
                            apis.Add(next[(suffix + 1)..]);
                        }
                        else if (next.EndsWith("_cuvid", StringComparison.Ordinal))
                        {
                            apis.Add("cuda");
                        }
                    }

                    break;
            }
        }

        var api = ApiPrecedence.FirstOrDefault(apis.Contains);
        if (api is null)
        {
            return (null, null);
        }

        var vendor = Hardware.VendorOfApi(api) ?? (apis.Contains("cuda") || apis.Contains("nvenc") ? Hardware.Nvidia : null) ?? (api == "vaapi" ? vaapiVendor : null);
        return (api, vendor);
    }

    private static string? VaapiVendor(string device)
    {
        foreach (var option in device.Split(','))
        {
            switch (option)
            {
                case "driver=iHD" or "driver=i965" or "vendor_id=0x8086" or "kernel_driver=i915" or "kernel_driver=xe":
                    return Hardware.Intel;
                case "driver=radeonsi" or "vendor_id=0x1002" or "kernel_driver=amdgpu":
                    return Hardware.Amd;
                case "driver=nvidia" or "vendor_id=0x10de":
                    return Hardware.Nvidia;
                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or below it.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <param name="root">An absolute directory.</param>
    /// <returns>Whether the path is under the root.</returns>
    public static bool IsUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        // "/data/media/../../etc" is not under /data/media: refuse any "." or ".."
        // segment rather than resolve it (a job with one runs on the server).
        if (path.Split('/').Any(segment => segment is "." or ".."))
        {
            return false;
        }

        var r = root.TrimEnd('/');
        return path.Length >= r.Length
            && path.StartsWith(r, StringComparison.Ordinal)
            && (path.Length == r.Length || path[r.Length] == '/' || r.Length == 0);
    }

    private static JobKind Classify(string binary, IReadOnlyList<string> args, string joined)
    {
        if (binary.StartsWith("ffprobe", StringComparison.Ordinal))
        {
            return JobKind.Probe;
        }

        if (AnalysisMarkers.Any(m => joined.Contains(m, StringComparison.Ordinal)))
        {
            return JobKind.Analysis;
        }

        if (joined.Contains("-f image2", StringComparison.Ordinal))
        {
            return args.Any(a => a.Contains("%0", StringComparison.Ordinal) && a.EndsWith(".jpg", StringComparison.Ordinal))
                ? JobKind.Trickplay
                : JobKind.ImageSingle;
        }

        if (ExtractPattern().IsMatch(joined))
        {
            return JobKind.Extract;
        }

        if (TranscodePattern().IsMatch(joined))
        {
            return JobKind.Transcode;
        }

        return JobKind.Other;
    }

    private static void Collect(string arg, List<string> paths, List<string> urls)
    {
        if (arg.Contains("://", StringComparison.Ordinal))
        {
            urls.Add(arg);
            return;
        }

        var candidate = arg.StartsWith("file:", StringComparison.Ordinal) ? arg[5..] : arg;
        if (candidate.StartsWith('/') && !candidate.StartsWith("/dev/", StringComparison.Ordinal))
        {
            paths.Add(candidate);
            return;
        }

        // Paths embedded in option values: filter args (subtitles=f=/x:fontsdir=/y),
        // concat lists, device specs. Take every "/..." run after '=' or ':'.
        foreach (Match m in EmbeddedPathPattern().Matches(arg))
        {
            var p = m.Groups["p"].Value.Replace("\\:", ":", StringComparison.Ordinal).TrimEnd('\'', '"');
            if (!p.StartsWith("/dev/", StringComparison.Ordinal) && p.Length > 1)
            {
                paths.Add(p);
            }
        }
    }

    private static string? AsPath(string arg)
    {
        var candidate = arg.StartsWith("file:", StringComparison.Ordinal) ? arg[5..] : arg;
        return candidate.StartsWith('/') && !candidate.StartsWith("/dev/", StringComparison.Ordinal) ? candidate : null;
    }

    private static bool IsLoopbackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"-dump_attachment|-f (ass|ssa|srt|webvtt)\b|-c:s (copy|srt|ass|ssa|webvtt)\b")]
    private static partial Regex ExtractPattern();

    [GeneratedRegex(@"-f (hls|mp4|matroska|mpegts|webm)\b|-hls_")]
    private static partial Regex TranscodePattern();

    [GeneratedRegex(@"(?<=[=:'])(?<p>/(?:[^:,;'\[\]]|\\:)+)")]
    private static partial Regex EmbeddedPathPattern();
}
