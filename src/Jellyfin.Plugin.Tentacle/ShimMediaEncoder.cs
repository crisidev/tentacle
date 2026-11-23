using MediaBrowser.Controller.MediaEncoding;
#if NET10_0_OR_GREATER
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Tentacle.CrossVendor;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
#endif

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Jellyfin's media encoder, unchanged except for one step: once Jellyfin has found
/// and validated its ffmpeg, the shim takes its place. Jellyfin calls
/// <see cref="SetFFmpegPath"/> after the plugin's hosted services start and before
/// it runs any job, so every ffmpeg it starts from then on is the shim.
/// </summary>
public sealed class ShimMediaEncoder : MediaEncoderDecorator
{
    private readonly ShimInstaller _installer;
#if NET10_0_OR_GREATER
    private readonly Lazy<CrossVendorPlanner?>? _planner;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="ShimMediaEncoder"/> class.
    /// </summary>
    /// <param name="inner">Jellyfin's own media encoder.</param>
    /// <param name="installer">Puts the shim in front of the encoder's ffmpeg.</param>
    public ShimMediaEncoder(IMediaEncoder inner, ShimInstaller installer)
        : base(inner)
    {
        _installer = installer;
    }
#if NET10_0_OR_GREATER

    /// <summary>
    /// Initializes a new instance of the <see cref="ShimMediaEncoder"/> class that also
    /// plans trickplay jobs for tentacles with another vendor's GPU.
    /// </summary>
    /// <param name="inner">Jellyfin's own media encoder.</param>
    /// <param name="installer">Puts the shim in front of the encoder's ffmpeg.</param>
    /// <param name="planner">The cross-vendor planner, when it is registered (resolved on first use).</param>
    public ShimMediaEncoder(IMediaEncoder inner, ShimInstaller installer, Lazy<CrossVendorPlanner?> planner)
        : this(inner, installer)
    {
        _planner = planner;
    }
#endif

    /// <inheritdoc />
    public override bool SetFFmpegPath()
    {
        // Jellyfin validates and interrogates the real ffmpeg; the shim only replaces
        // it once that has succeeded.
        var valid = Inner.SetFFmpegPath();
        if (valid)
        {
            _installer.Redirect(Inner);
        }

        return valid;
    }
#if NET10_0_OR_GREATER

    /// <inheritdoc />
    public override Task<string> ExtractVideoImagesOnIntervalAccelerated(
        string inputFile,
        string container,
        MediaSourceInfo mediaSource,
        MediaStream imageStream,
        int maxWidth,
        TimeSpan interval,
        bool allowHwAccel,
        bool enableHwEncoding,
        int? threads,
        int? qualityScale,
        ProcessPriorityClass? priority,
        bool enableKeyFrameOnlyExtraction,
        EncodingHelper encodingHelper,
        CancellationToken cancellationToken)
    {
        // Jellyfin builds and starts the command line in the call below: predict it first,
        // so the broker can run it rebuilt for a tentacle with another vendor's GPU.
        if (_planner?.Value is { } planner && encodingHelper is not null && mediaSource is not null && imageStream is not null)
        {
            planner.PlanTrickplay(
                new TrickplayRequest(inputFile, container, mediaSource, imageStream, maxWidth, interval, allowHwAccel, enableHwEncoding, threads, qualityScale, enableKeyFrameOnlyExtraction),
                encodingHelper,
                Inner.EncoderVersion);
        }

        return base.ExtractVideoImagesOnIntervalAccelerated(inputFile, container, mediaSource!, imageStream!, maxWidth, interval, allowHwAccel, enableHwEncoding, threads, qualityScale, priority, enableKeyFrameOnlyExtraction, encodingHelper!, cancellationToken);
    }
#endif
}
