using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using EncodingOptions = MediaBrowser.Model.Configuration.EncodingOptions;

namespace Jellyfin.Plugin.Tentacle.CrossVendor;

/// <summary>
/// The command lines Jellyfin 12's <c>MediaEncoder.ExtractVideoImagesOnIntervalAccelerated</c>
/// starts for trickplay, built the same way from the same public <see cref="EncodingHelper"/>
/// calls: once with the server's helper and settings (what the shim will bring, which the
/// broker checks token for token), once with a tentacle's (what it runs instead). The
/// output directory, a fresh GUID per run, is <see cref="OutputPlaceholder"/>.
/// </summary>
public static class TrickplayCommand
{
    /// <summary>
    /// The output directory name standing for the GUID Jellyfin picks when it starts the job.
    /// </summary>
    public const string OutputPlaceholder = "00000000000000000000000000000000";

    /// <summary>
    /// The command lines Jellyfin would start for one trickplay extraction, in the order it
    /// tries them: with key-frame-only extraction, the <c>-skip_frame nokey</c> one first
    /// and the plain one it falls back to; otherwise just the plain one.
    /// </summary>
    /// <param name="helper">The EncodingHelper (the server's, or one that sees a tentacle's GPU).</param>
    /// <param name="configured">The encoding settings it builds for (the server's or a tentacle's).</param>
    /// <param name="encoderVersion">The ffmpeg version (Jellyfin's video sync option depends on it).</param>
    /// <param name="defaultThreads">The threads Jellyfin's media encoder uses when the request names none.</param>
    /// <param name="tempDirectory">Jellyfin's temp directory (the output goes below it).</param>
    /// <param name="request">What Jellyfin was asked to extract.</param>
    /// <returns>The arguments (ffmpeg's, without the binary), or none for a hardware type it does not build here.</returns>
    public static IReadOnlyList<string> Build(EncodingHelper helper, EncodingOptions configured, Version encoderVersion, int defaultThreads, string tempDirectory, TrickplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(request);
        var allowHwAccel = request.AllowHwAccel;
        var options = allowHwAccel ? configured : new EncodingOptions();
        var threads = request.Threads ?? defaultThreads;
        if (allowHwAccel && request.EnableKeyFrameOnlyExtraction && !KeyFramesDecodeInHardware(options))
        {
            allowHwAccel = false;
            options = new EncodingOptions();
        }

        if (!allowHwAccel)
        {
            options.EnableHardwareEncoding = false;
            options.HardwareAccelerationType = HardwareAccelerationType.none;
            options.EnableTonemapping = false;
        }

        if (options.HardwareAccelerationType == HardwareAccelerationType.videotoolbox)
        {
            // Jellyfin adds a low-priority flag there when its ffmpeg supports it: not known here.
            return [];
        }

        var stream = request.ImageStream;
        if (stream.Width.HasValue && stream.Height.HasValue && !string.IsNullOrEmpty(stream.AspectRatio))
        {
            // Jellyfin corrects the stream's height to its aspect ratio first (on the same
            // object, so doing it here too changes nothing it would not change anyway).
            var ratio = stream.AspectRatio.Split(':');
            var width = double.Parse(ratio[0], CultureInfo.InvariantCulture);
            var height = double.Parse(ratio[1], CultureInfo.InvariantCulture);
            if (Math.Abs((stream.Width.Value * height) - (stream.Height.Value * width)) > 0.05)
            {
                stream.Height = Convert.ToInt32(stream.Width.Value * height / width);
            }
        }

        var job = new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            IsVideoRequest = true,
            MediaSource = request.MediaSource,
            VideoStream = stream,
            BaseRequest = new BaseEncodingJobOptions { MaxWidth = request.MaxWidth, MaxFramerate = (float)(1.0 / request.Interval.TotalSeconds) },
            MediaPath = request.InputFile,
            OutputVideoCodec = "mjpeg",
        };
        var encoder = request.EnableHwEncoding ? helper.GetVideoEncoder(job, options) : job.OutputVideoCodec;
        var input = helper.GetInputArgument(job, options, request.Container).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        if (!allowHwAccel)
        {
            input = string.Create(CultureInfo.InvariantCulture, $"-threads {threads} {input}");
        }

        var index = EncodingHelper.FindIndex(request.MediaSource.MediaStreams, stream);
        if (index < 0)
        {
            return [];
        }

        input += string.Create(CultureInfo.InvariantCulture, $" -map 0:{index}");
        var filter = helper.GetVideoProcessingFilterParam(job, options, encoder).Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return [];
        }

        if (!request.EnableKeyFrameOnlyExtraction)
        {
            var fps = filter.IndexOf("fps=", StringComparison.Ordinal);
            if (fps < 0)
            {
                return [];
            }

            var rate = stream.ReferenceFrameRate is > 0 ? stream.ReferenceFrameRate.Value : 30f;
            filter = filter.Insert(fps, string.Create(CultureInfo.InvariantCulture, $"setpts=N/{rate:F3}/TB,"));
        }

        var plain = Arguments(input, filter, encoder, threads, request.QualityScale, encoderVersion, tempDirectory);
        return request.EnableKeyFrameOnlyExtraction
            ? [Arguments("-skip_frame nokey " + input, filter, encoder, threads, request.QualityScale, encoderVersion, tempDirectory), plain]
            : [plain];
    }

    // Jellyfin keeps hardware decoding for key-frame-only extraction only where the
    // decoder honours -skip_frame nokey; elsewhere it decodes in software.
    private static bool KeyFramesDecodeInHardware(EncodingOptions options) => options.HardwareAccelerationType switch
    {
        HardwareAccelerationType.nvenc => options.EnableEnhancedNvdecDecoder,
        HardwareAccelerationType.amf => OperatingSystem.IsWindows(),
        HardwareAccelerationType.qsv => options.PreferSystemNativeHwDecoder,
        HardwareAccelerationType.vaapi or HardwareAccelerationType.videotoolbox or HardwareAccelerationType.rkmpp => true,
        _ => false,
    };

    private static string Arguments(string input, string filter, string encoder, int threads, int? qualityScale, Version encoderVersion, string tempDirectory)
    {
        var quality = Math.Clamp(qualityScale ?? 4, 1, 31);
        var qualityOption = "-qscale:v ";
        if (encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase) || encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
        {
            quality = 100 - ((quality - 1) * 3);
            qualityOption = "-global_quality:v ";
        }

        if (encoder.Contains("rkmpp", StringComparison.OrdinalIgnoreCase))
        {
            quality = 99 - ((quality - 1) * 3);
            qualityOption = "-qp_init:v ";
        }

        var output = Path.Combine(tempDirectory, OutputPlaceholder, "%08d.jpg");
        var sync = EncodingHelper.GetVideoSyncOption("0", encoderVersion).Trim() + " ";
        return string.Format(
            CultureInfo.InvariantCulture,
            "-loglevel error {0} -an -sn {1} -threads {2} -c:v {3} {4}{5}{6}-f {7} \"{8}\"",
            input,
            filter,
            threads,
            encoder,
            qualityOption + quality.ToString(CultureInfo.InvariantCulture) + " ",
            string.Empty,
            sync,
            "image2",
            output);
    }
}

/// <summary>
/// One trickplay extraction, as Jellyfin's trickplay manager asks for it.
/// </summary>
/// <param name="InputFile">The media file.</param>
/// <param name="Container">Its container.</param>
/// <param name="MediaSource">Its media source.</param>
/// <param name="ImageStream">The video stream the images come from.</param>
/// <param name="MaxWidth">The images' width.</param>
/// <param name="Interval">The time between images.</param>
/// <param name="AllowHwAccel">Whether trickplay may use hardware decoding.</param>
/// <param name="EnableHwEncoding">Whether it may use a hardware JPEG encoder.</param>
/// <param name="Threads">ffmpeg's threads, or null for Jellyfin's default.</param>
/// <param name="QualityScale">The JPEG quality scale, or null for Jellyfin's default.</param>
/// <param name="EnableKeyFrameOnlyExtraction">Whether only key frames are decoded.</param>
public sealed record TrickplayRequest(
    string InputFile,
    string Container,
    MediaSourceInfo MediaSource,
    MediaStream ImageStream,
    int MaxWidth,
    TimeSpan Interval,
    bool AllowHwAccel,
    bool EnableHwEncoding,
    int? Threads,
    int? QualityScale,
    bool EnableKeyFrameOnlyExtraction);
