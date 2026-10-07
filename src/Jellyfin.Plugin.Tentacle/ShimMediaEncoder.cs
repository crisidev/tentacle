using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Jellyfin's media encoder, unchanged except for one step: once Jellyfin has found
/// and validated its ffmpeg, the shim takes its place. Jellyfin calls
/// <see cref="SetFFmpegPath"/> after the plugin's hosted services start and before
/// it runs any job, so every ffmpeg it starts from then on is the shim.
/// </summary>
/// <remarks>Written out rather than generated: DispatchProxy needs runtime code
/// generation, which plugins' collectible load contexts do not reliably support.</remarks>
public sealed class ShimMediaEncoder : IMediaEncoder
{
    private readonly IMediaEncoder _inner;
    private readonly ShimInstaller _installer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShimMediaEncoder"/> class.
    /// </summary>
    /// <param name="inner">Jellyfin's own media encoder.</param>
    /// <param name="installer">Puts the shim in front of the encoder's ffmpeg.</param>
    public ShimMediaEncoder(IMediaEncoder inner, ShimInstaller installer)
    {
        _inner = inner;
        _installer = installer;
    }

    /// <inheritdoc />
    public string EncoderPath => _inner.EncoderPath;

    /// <inheritdoc />
    public string ProbePath => _inner.ProbePath;

    /// <inheritdoc />
    public Version EncoderVersion => _inner.EncoderVersion;

    /// <inheritdoc />
    public bool IsPkeyPauseSupported => _inner.IsPkeyPauseSupported;

    /// <inheritdoc />
    public bool IsVaapiDeviceAmd => _inner.IsVaapiDeviceAmd;

    /// <inheritdoc />
    public bool IsVaapiDeviceInteliHD => _inner.IsVaapiDeviceInteliHD;

    /// <inheritdoc />
    public bool IsVaapiDeviceInteli965 => _inner.IsVaapiDeviceInteli965;

    /// <inheritdoc />
    public bool IsVaapiDeviceSupportVulkanDrmModifier => _inner.IsVaapiDeviceSupportVulkanDrmModifier;

    /// <inheritdoc />
    public bool IsVaapiDeviceSupportVulkanDrmInterop => _inner.IsVaapiDeviceSupportVulkanDrmInterop;

    /// <inheritdoc />
    public bool IsVideoToolboxAv1DecodeAvailable => _inner.IsVideoToolboxAv1DecodeAvailable;

    /// <inheritdoc />
    public bool SetFFmpegPath()
    {
        // Jellyfin validates and interrogates the real ffmpeg; the shim only replaces
        // it once that has succeeded.
        var valid = _inner.SetFFmpegPath();
        if (valid)
        {
            _installer.Redirect(_inner);
        }

        return valid;
    }

    /// <inheritdoc />
    public bool CanEncodeToAudioCodec(string codec) => _inner.CanEncodeToAudioCodec(codec);

    /// <inheritdoc />
    public bool CanEncodeToSubtitleCodec(string codec) => _inner.CanEncodeToSubtitleCodec(codec);

    /// <inheritdoc />
    public bool CanExtractSubtitles(string codec) => _inner.CanExtractSubtitles(codec);

    /// <inheritdoc />
    public bool SupportsEncoder(string encoder) => _inner.SupportsEncoder(encoder);

    /// <inheritdoc />
    public bool SupportsDecoder(string decoder) => _inner.SupportsDecoder(decoder);

    /// <inheritdoc />
    public bool SupportsHwaccel(string hwaccel) => _inner.SupportsHwaccel(hwaccel);

    /// <inheritdoc />
    public bool SupportsFilter(string filter) => _inner.SupportsFilter(filter);

    /// <inheritdoc />
    public bool SupportsFilterWithOption(FilterOptionType option) => _inner.SupportsFilterWithOption(option);

    /// <inheritdoc />
    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => _inner.SupportsBitStreamFilterWithOption(option);

    /// <inheritdoc />
    public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken)
        => _inner.ExtractAudioImage(path, imageStreamIndex, cancellationToken);

    /// <inheritdoc />
    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
        => _inner.ExtractVideoImage(inputFile, container, mediaSource, videoStream, threedFormat, offset, cancellationToken);

    /// <inheritdoc />
    public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, ImageFormat? targetFormat, CancellationToken cancellationToken)
        => _inner.ExtractVideoImage(inputFile, container, mediaSource, imageStream, imageStreamIndex, targetFormat, cancellationToken);

    /// <inheritdoc />
    public Task<string> ExtractVideoImagesOnIntervalAccelerated(
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
        => _inner.ExtractVideoImagesOnIntervalAccelerated(
            inputFile,
            container,
            mediaSource,
            imageStream,
            maxWidth,
            interval,
            allowHwAccel,
            enableHwEncoding,
            threads,
            qualityScale,
            priority,
            enableKeyFrameOnlyExtraction,
            encodingHelper,
            cancellationToken);

    /// <inheritdoc />
    public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken) => _inner.GetMediaInfo(request, cancellationToken);

    /// <inheritdoc />
    public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource) => _inner.GetInputArgument(inputFile, mediaSource);

    /// <inheritdoc />
    public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource) => _inner.GetInputArgument(inputFiles, mediaSource);

    /// <inheritdoc />
    public string GetExternalSubtitleInputArgument(string inputFile) => _inner.GetExternalSubtitleInputArgument(inputFile);

    /// <inheritdoc />
    public string GetTimeParameter(long ticks) => _inner.GetTimeParameter(ticks);

    /// <inheritdoc />
    public Task ConvertImage(string inputPath, string outputPath) => _inner.ConvertImage(inputPath, outputPath);

    /// <inheritdoc />
    public string EscapeSubtitleFilterPath(string path) => _inner.EscapeSubtitleFilterPath(path);

    /// <inheritdoc />
    public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber) => _inner.GetPrimaryPlaylistVobFiles(path, titleNumber);

    /// <inheritdoc />
    public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path) => _inner.GetPrimaryPlaylistM2tsFiles(path);

    /// <inheritdoc />
    public string GetInputPathArgument(EncodingJobInfo state) => _inner.GetInputPathArgument(state);

    /// <inheritdoc />
    public string GetInputPathArgument(string path, MediaSourceInfo mediaSource) => _inner.GetInputPathArgument(path, mediaSource);

    /// <inheritdoc />
    public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath) => _inner.GenerateConcatConfig(source, concatFilePath);
}
