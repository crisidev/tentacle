using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tentacle.Broker;
using Tentacle.Protocol;

namespace Jellyfin.Plugin.Tentacle.CrossVendor;

/// <summary>
/// Decides, for each transcode Jellyfin is about to start, whether it should run on
/// a tentacle with another vendor's GPU, and if so rebuilds it for that GPU
/// (<see cref="CommandRebuilder"/>). In shadow mode it only records what it would
/// have done. Every doubt leaves the job as Jellyfin built it.
/// </summary>
public sealed partial class CrossVendorPlanner
{
    private readonly TentacleRuntime _runtime;
    private readonly IServiceProvider _services;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<CrossVendorPlanner> _logger;
    private readonly ConcurrentDictionary<string, EncodingHelper> _helpers = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="CrossVendorPlanner"/> class.
    /// </summary>
    /// <param name="runtime">The broker's runtime.</param>
    /// <param name="services">Jellyfin's services (its EncodingHelper and what one needs).</param>
    /// <param name="configurationManager">Jellyfin's configuration.</param>
    /// <param name="logger">The logger.</param>
    public CrossVendorPlanner(TentacleRuntime runtime, IServiceProvider services, IConfigurationManager configurationManager, ILogger<CrossVendorPlanner> logger)
    {
        _runtime = runtime;
        _services = services;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>
    /// The arguments to start a transcode with: rebuilt for a tentacle's GPU, or null
    /// to start it as Jellyfin built it.
    /// </summary>
    /// <param name="state">The job's state.</param>
    /// <param name="outputPath">Its output (the playlist, or the file).</param>
    /// <param name="original">The arguments Jellyfin built.</param>
    /// <param name="type">The job's type.</param>
    /// <returns>The rebuilt arguments, or null.</returns>
    public string? Plan(StreamState state, string outputPath, string original, TranscodingJobType type)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Plugin.Instance?.Configuration is not { CrossVendor: true } || type is not (TranscodingJobType.Hls or TranscodingJobType.Progressive) || state.VideoStream is null)
        {
            return null;
        }

        var broker = _runtime.Broker;
        var analysis = ArgvAnalysis.Analyze("ffmpeg", SplitArguments(original), "/");
        var codec = state.ActualOutputVideoCodec;
        var node = broker.PlanCrossVendor(analysis, n => n.Gpu is { } gpu && WorkerEncodingOptions.TypeOf(gpu) is not null && WorkerEncodingOptions.CanEncode(gpu, codec));
        if (node?.Gpu is not { } target)
        {
            return null;
        }

        var serverOptions = _configurationManager.GetEncodingOptions();
        RebuildResult result;
        try
        {
            var workerOptions = WorkerEncodingOptions.For(serverOptions, target)!;
            result = CommandRebuilder.Rebuild(_services.GetRequiredService<EncodingHelper>(), serverOptions, HelperFor(target), workerOptions, state, original, type);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Jellyfin's helpers on another GPU's settings: any surprise leaves the job alone.
            LogFailed(e, node.Name);
            broker.Metrics.CrossVendor(node.Name, "skipped:error");
            return null;
        }

        if (result.Arguments is not { } rebuilt || !IsFor(rebuilt, target))
        {
            var outcome = result.Rebuilt ? "skipped:vendor" : result.Outcome;
            LogSkipped(node.Name, outcome);
            broker.Metrics.CrossVendor(node.Name, Label(outcome));
            return null;
        }

        var shadow = broker.Options.Placement == PlacementMode.DryRun;
        broker.NoteCrossVendor(outputPath, node, "ffmpeg " + original, "ffmpeg " + rebuilt, shadow);
        LogRebuilt(node.Name, Scheduler.CrossVendorReason(target), shadow ? "would run there (shadow mode)" : "runs there");
        return shadow ? null : rebuilt;
    }

    /// <summary>
    /// Splits arguments the way .NET passes a ProcessStartInfo.Arguments string to a
    /// process on Linux (Jellyfin starts ffmpeg that way): spaces separate, double
    /// quotes group, and a backslash escapes a double quote.
    /// </summary>
    /// <param name="arguments">The arguments string.</param>
    /// <returns>The argv.</returns>
    public static IReadOnlyList<string> SplitArguments(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '\\' && i + 1 < arguments.Length && arguments[i + 1] == '"')
            {
                current.Append('"');
                i++;
                any = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>
    /// Whether a rebuilt command line is for a tentacle's GPU: it names that GPU's vendor,
    /// or (plain VAAPI, which names none) drives only that GPU's hardware API. Anything
    /// still naming the server's hardware (h264_qsv, driver=iHD) is not.
    /// </summary>
    /// <param name="rebuilt">The rebuilt arguments.</param>
    /// <param name="gpu">The tentacle's GPU.</param>
    /// <returns>True if it is.</returns>
    public static bool IsFor(string rebuilt, GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        var analysis = ArgvAnalysis.Analyze("ffmpeg", SplitArguments(rebuilt), "/");
        return analysis.UsesHardware && (analysis.HardwareVendor is { } vendor
            ? string.Equals(vendor, gpu.Vendor, StringComparison.Ordinal)
            : string.Equals(analysis.HardwareApi, gpu.Api, StringComparison.Ordinal));
    }

    // Metric labels: the reason, without the part name after a second colon.
    private static string Label(string outcome)
    {
        var second = outcome.IndexOf(':', outcome.IndexOf(':', StringComparison.Ordinal) + 1);
        return second < 0 ? outcome : outcome[..second];
    }

    private EncodingHelper HelperFor(GpuInfo gpu)
        => _helpers.GetOrAdd($"{gpu.Vendor}|{gpu.Api}|{gpu.Device}", _ =>
            ActivatorUtilities.CreateInstance<EncodingHelper>(_services, new WorkerMediaEncoder(_services.GetRequiredService<IMediaEncoder>(), gpu)));

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle: transcode rebuilt for {Node} ({Reason}): {What}")]
    private partial void LogRebuilt(string node, string reason, string what);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tentacle: transcode not rebuilt for {Node}: {Outcome}")]
    private partial void LogSkipped(string node, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tentacle: rebuilding a transcode for {Node} failed; it runs as Jellyfin built it")]
    private partial void LogFailed(Exception e, string node);
}
