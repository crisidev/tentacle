using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Entities;
using Tentacle.Protocol;
using EncodingOptions = MediaBrowser.Model.Configuration.EncodingOptions;

namespace Jellyfin.Plugin.Tentacle.CrossVendor;

/// <summary>
/// A part of a command line that depends on the GPU: what Jellyfin's EncodingHelper
/// builds for it with the server's settings, and with a tentacle's.
/// </summary>
/// <param name="Name">What it is (for the skip reason).</param>
/// <param name="Server">The text built with the server's settings.</param>
/// <param name="Worker">The text built with the tentacle's settings.</param>
public sealed record Fragment(string Name, string Server, string Worker);

/// <summary>
/// The outcome of rebuilding a command line for a tentacle's GPU.
/// </summary>
/// <param name="Arguments">The rebuilt arguments, or null when it was not rebuilt.</param>
/// <param name="Outcome">rebuilt, or skipped:&lt;why&gt; (no-video, copy, hdr, codec, no-match:&lt;part&gt;, ambiguous:&lt;part&gt;, no-anchor:&lt;part&gt;, job-type).</param>
public sealed record RebuildResult(string? Arguments, string Outcome)
{
    /// <summary>Gets a value indicating whether the command line was rebuilt.</summary>
    public bool Rebuilt => Arguments is not null;

    /// <summary>
    /// A skipped result.
    /// </summary>
    /// <param name="why">Why.</param>
    /// <returns>The result.</returns>
    public static RebuildResult Skipped(string why) => new(null, "skipped:" + why);
}

/// <summary>
/// Rebuilds a transcode the server built for its own GPU for a tentacle's GPU of
/// another vendor, with Jellyfin's own EncodingHelper, and only when the result is
/// provably the same job: everything that does not depend on the GPU is left as the
/// server built it, and every part that does is first rebuilt with the server's
/// settings and must be found, exactly once and in order, in the command line
/// Jellyfin actually built. Anything else (another Jellyfin version, a pipeline this
/// does not know) leaves the job as built.
/// </summary>
public static partial class CommandRebuilder
{
    // The options Jellyfin's EncodingHelper writes differently for another GPU, each with
    // one value: hardware setup and decoders, the video encoder, its quality, bitrate and
    // keyframe options, the filter chain, threads. Nothing else may differ.
    private static readonly FrozenSet<string> HardwareOptions = new[]
    {
        "-init_hw_device", "-filter_hw_device", "-hwaccel", "-hwaccel_output_format", "-hwaccel_device", "-hwaccel_flags",
        "-qsv_device", "-vaapi_device", "-c:v", "-codec:v", "-codec:v:0", "-vf", "-filter_complex", "-threads",
        "-low_power", "-preset", "-mbbrc", "-b:v", "-maxrate", "-bufsize", "-rc_init_occupancy", "-rc_mode", "-rc", "-qmin", "-qmax",
        "-profile:v:0", "-level", "-sei", "-async_depth", "-crf", "-cq", "-qp", "-global_quality", "-global_quality:v", "-qscale:v", "-tune", "-compression_level",
        "-gops_per_idr", "-header_insertion_mode", "-prio_speed", "-quality", "-look_ahead", "-look_ahead_depth", "-extbrc",
        "-b_strategy", "-bf", "-refs", "-spatial-aq", "-temporal-aq", "-multipass", "-tier", "-forced-idr", "-r",
        "-g:v:0", "-keyint_min:v:0", "-force_key_frames", "-force_key_frames:0", "-sc_threshold:v:0", "-flags:v", "-bsf:v",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// What a rebuilt command line changes besides the GPU's parts, or null when nothing:
    /// the same inputs, output and maps, no file the original did not use, and, once the
    /// GPU-dependent options and their values are set aside, the same argv token for
    /// token. The splice already matched each part; this checks the result as a whole.
    /// </summary>
    /// <param name="original">The argv Jellyfin built.</param>
    /// <param name="rebuilt">The rebuilt argv.</param>
    /// <returns>What changed (inputs, output, paths, control, options), or null.</returns>
    public static string? WhatChanged(IReadOnlyList<string> original, IReadOnlyList<string> rebuilt)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(rebuilt);
        if (rebuilt.Any(a => a.Any(c => char.IsControl(c))))
        {
            return "control";
        }

        var before = ArgvAnalysis.Analyze("ffmpeg", original, "/");
        var after = ArgvAnalysis.Analyze("ffmpeg", rebuilt, "/");
        if (!after.Inputs.SequenceEqual(before.Inputs, StringComparer.Ordinal))
        {
            return "inputs";
        }

        if (!string.Equals(after.Output, before.Output, StringComparison.Ordinal))
        {
            return "output";
        }

        if (after.Paths.Except(before.Paths, StringComparer.Ordinal).Any() || after.Urls.Except(before.Urls, StringComparer.Ordinal).Any())
        {
            return "paths";
        }

        return WithoutHardware(original).SequenceEqual(WithoutHardware(rebuilt), StringComparer.Ordinal) ? null : "options";
    }

    /// <summary>
    /// Rebuilds a command line.
    /// </summary>
    /// <param name="server">The server's EncodingHelper (the one Jellyfin's controllers use).</param>
    /// <param name="serverOptions">The server's encoding settings.</param>
    /// <param name="worker">An EncodingHelper that sees the tentacle's GPU (<see cref="WorkerMediaEncoder"/>).</param>
    /// <param name="workerOptions">The settings for the tentacle's GPU (<see cref="WorkerEncodingOptions.For"/>).</param>
    /// <param name="state">The job's state, as Jellyfin built the command line from it.</param>
    /// <param name="original">The arguments Jellyfin built.</param>
    /// <param name="type">The job's type.</param>
    /// <returns>The result.</returns>
    public static RebuildResult Rebuild(EncodingHelper server, EncodingOptions serverOptions, EncodingHelper worker, EncodingOptions workerOptions, StreamState state, string original, TranscodingJobType type)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(original);
        if (state.VideoStream is null || !state.IsOutputVideo)
        {
            return RebuildResult.Skipped("no-video");
        }

        if (state.VideoStream.VideoRange != VideoRange.SDR)
        {
            // Tonemapping takes other filters (OpenCL, Vulkan) per vendor: not rebuilt.
            return RebuildResult.Skipped("hdr");
        }

        var serverEncoder = server.GetVideoEncoder(state, serverOptions);
        if (EncodingHelper.IsCopyCodec(serverEncoder))
        {
            return RebuildResult.Skipped("copy");
        }

        var workerEncoder = worker.GetVideoEncoder(state, workerOptions);
        var api = workerOptions.HardwareAccelerationType == HardwareAccelerationType.nvenc ? "_nvenc" : "_" + workerOptions.HardwareAccelerationType;
        if (!workerEncoder.EndsWith(api, StringComparison.Ordinal))
        {
            // Its GPU would not encode it (codec, settings): no point moving it there.
            return RebuildResult.Skipped("codec");
        }

        switch (type)
        {
            case TranscodingJobType.Progressive:
                // Jellyfin builds the whole command line in one public call: it must match exactly.
                var expected = server.GetProgressiveVideoFullCommandLine(state, serverOptions, EncoderPreset.superfast);
                return string.Equals(expected, original, StringComparison.Ordinal)
                    ? new RebuildResult(PinVaapiDevice(worker.GetProgressiveVideoFullCommandLine(state, workerOptions, EncoderPreset.superfast), workerOptions), "rebuilt")
                    : RebuildResult.Skipped("no-match:command");
            case TranscodingJobType.Hls:
                return Splice(original, HlsFragments(server, serverOptions, serverEncoder, worker, workerOptions, workerEncoder, state, original));
            default:
                return RebuildResult.Skipped("job-type");
        }
    }

    /// <summary>
    /// Replaces each fragment's server text with its tentacle text. Each server text
    /// must occur exactly once in the command line, after the previous one; an empty
    /// one may only be replaced by an empty one (there is nothing to anchor it to).
    /// </summary>
    /// <param name="original">The command line.</param>
    /// <param name="fragments">The fragments, in the order they appear.</param>
    /// <returns>The result.</returns>
    public static RebuildResult Splice(string original, IReadOnlyList<Fragment> fragments)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(fragments);
        var result = new StringBuilder(original.Length + 64);
        var cursor = 0;
        foreach (var fragment in fragments)
        {
            if (fragment.Server.Length == 0)
            {
                if (fragment.Worker.Length != 0)
                {
                    return RebuildResult.Skipped("no-anchor:" + fragment.Name);
                }

                continue;
            }

            var at = original.IndexOf(fragment.Server, cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                return RebuildResult.Skipped("no-match:" + fragment.Name);
            }

            if (original.IndexOf(fragment.Server, at + 1, StringComparison.Ordinal) >= 0 || original.IndexOf(fragment.Server, 0, at, StringComparison.Ordinal) >= 0)
            {
                return RebuildResult.Skipped("ambiguous:" + fragment.Name);
            }

            result.Append(original, cursor, at - cursor).Append(fragment.Worker);
            cursor = at + fragment.Server.Length;
        }

        return new RebuildResult(result.Append(original, cursor, original.Length - cursor).ToString(), "rebuilt");
    }

    /// <summary>
    /// Names the tentacle's render node in every <c>-init_hw_device vaapi=&lt;alias&gt;</c>
    /// that has none. Jellyfin writes the node only when that file exists where it builds
    /// the command line, which for a tentacle's command is the server: without this, a
    /// tentacle whose GPU is not at a path the server also has (renderD129, or a server
    /// with no /dev/dri) would get no device, and its ffmpeg would open the default one.
    /// A node Jellyfin did write is left as it is. Only a validated render node is pinned.
    /// </summary>
    /// <param name="arguments">The tentacle's arguments, as Jellyfin built them.</param>
    /// <param name="workerOptions">The settings for the tentacle's GPU.</param>
    /// <returns>The arguments, with the render node named.</returns>
    public static string PinVaapiDevice(string arguments, EncodingOptions workerOptions)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workerOptions);
        if (workerOptions.HardwareAccelerationType != HardwareAccelerationType.vaapi || !Hardware.IsRenderNode(workerOptions.VaapiDevice))
        {
            return arguments;
        }

        return VaapiDeviceWithoutNode().Replace(arguments, m => m.Groups[1].Value + ":" + workerOptions.VaapiDevice);
    }

    /// <summary>
    /// The GPU-dependent parts of an HLS transcode, in the order Jellyfin's HLS
    /// controller writes them: input options, input and hardware setup, threads, the
    /// streams the filters drop, the encoder, its quality and keyframe options, and the
    /// video filter chain.
    /// </summary>
    private static Fragment[] HlsFragments(EncodingHelper server, EncodingOptions serverOptions, string serverEncoder, EncodingHelper worker, EncodingOptions workerOptions, string workerEncoder, StreamState state, string original)
    {
        var container = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer).TrimStart('.');
        var isEvent = original.Contains("-hls_playlist_type event", StringComparison.Ordinal);
        var startNumber = StartNumber().Match(original) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var preset = isEvent ? EncoderPreset.superfast : EncoderPreset.veryfast;
        var serverFilter = server.GetVideoProcessingFilterParam(state, serverOptions, serverEncoder);
        var workerFilter = worker.GetVideoProcessingFilterParam(state, workerOptions, workerEncoder);
        return
        [
            new("input-options", server.GetInputModifier(state, serverOptions, container), worker.GetInputModifier(state, workerOptions, container)),
            new("input", server.GetInputArgument(state, serverOptions, container), PinVaapiDevice(worker.GetInputArgument(state, workerOptions, container), workerOptions)),
            new("threads", Threads(state, serverOptions, serverEncoder), Threads(state, workerOptions, workerEncoder)),
            new("filter-maps", server.GetNegativeMapArgsByFilters(state, serverFilter), worker.GetNegativeMapArgsByFilters(state, workerFilter)),
            new("encoder", "-codec:v:0 " + serverEncoder, "-codec:v:0 " + workerEncoder),
            new("quality", server.GetVideoQualityParam(state, serverEncoder, serverOptions, preset), worker.GetVideoQualityParam(state, workerEncoder, workerOptions, preset)),
            new("keyframes", server.GetHlsVideoKeyFrameArguments(state, serverEncoder, state.SegmentLength, isEvent, startNumber), worker.GetHlsVideoKeyFrameArguments(state, workerEncoder, state.SegmentLength, isEvent, startNumber)),
            new("filters", serverFilter, workerFilter),
        ];
    }

    private static List<string> WithoutHardware(IReadOnlyList<string> argv)
    {
        var kept = new List<string>(argv.Count);
        for (var i = 0; i < argv.Count; i++)
        {
            if (HardwareOptions.Contains(argv[i]) && i + 1 < argv.Count)
            {
                i++;
                continue;
            }

            kept.Add(argv[i]);
        }

        return kept;
    }

    private static string Threads(StreamState state, EncodingOptions options, string encoder)
        => "-threads " + EncodingHelper.GetNumberOfThreads(state, options, encoder).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"-start_number (\d+)")]
    private static partial Regex StartNumber();

    // "vaapi=va" or "vaapi=va:" (then a space, a comma or the end): no render node.
    // "vaapi=va:/dev/dri/renderD128" does not match.
    [GeneratedRegex(@"(-init_hw_device vaapi=\w+):?(?=[\s,]|$)")]
    private static partial Regex VaapiDeviceWithoutNode();
}
