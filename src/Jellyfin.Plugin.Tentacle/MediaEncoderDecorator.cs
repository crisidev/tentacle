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
/// Jellyfin's media encoder, forwarded member by member: the base of the wrappers
/// that change one aspect of it (the shim's ffmpeg, a tentacle's GPU).
/// </summary>
/// <remarks>Written out rather than generated: DispatchProxy needs runtime code
/// generation, which plugins' collectible load contexts do not reliably support.</remarks>
public abstract class MediaEncoderDecorator : IMediaEncoder
{
    private readonly IMediaEncoder _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaEncoderDecorator"/> class.
    /// </summary>
    /// <param name="inner">The encoder to forward to.</param>
    protected MediaEncoderDecorator(IMediaEncoder inner)
    {
        _inner = inner;
    }

    /// <summary>Gets the encoder this forwards to.</summary>
    protected IMediaEncoder Inner => _inner;

    /// <inheritdoc />
    public virtual string EncoderPath => _inner.EncoderPath;

    /// <inheritdoc />
    public virtual string ProbePath => _inner.ProbePath;

    /// <inheritdoc />
    public virtual Version EncoderVersion => _inner.EncoderVersion;

    /// <inheritdoc />
    public virtual bool IsPkeyPauseSupported => _inner.IsPkeyPauseSupported;

    /// <inheritdoc />
    public virtual bool IsVaapiDeviceAmd => _inner.IsVaapiDeviceAmd;

    /// <inheritdoc />
    public virtual bool IsVaapiDeviceInteliHD => _inner.IsVaapiDeviceInteliHD;

    /// <inheritdoc />
    public virtual bool IsVaapiDeviceInteli965 => _inner.IsVaapiDeviceInteli965;

    /// <inheritdoc />
    public virtual bool IsVaapiDeviceSupportVulkanDrmModifier => _inner.IsVaapiDeviceSupportVulkanDrmModifier;

    /// <inheritdoc />
    public virtual bool IsVaapiDeviceSupportVulkanDrmInterop => _inner.IsVaapiDeviceSupportVulkanDrmInterop;

    /// <inheritdoc />
    public virtual bool IsVideoToolboxAv1DecodeAvailable => _inner.IsVideoToolboxAv1DecodeAvailable;

    /// <inheritdoc />
    public virtual bool SetFFmpegPath() => _inner.SetFFmpegPath();

    /// <inheritdoc />
    public virtual bool CanEncodeToAudioCodec(string codec) => _inner.CanEncodeToAudioCodec(codec);

    /// <inheritdoc />
    public virtual bool CanEncodeToSubtitleCodec(string codec) => _inner.CanEncodeToSubtitleCodec(codec);

    /// <inheritdoc />
    public virtual bool CanExtractSubtitles(string codec) => _inner.CanExtractSubtitles(codec);

    /// <inheritdoc />
    public virtual bool SupportsEncoder(string encoder) => _inner.SupportsEncoder(encoder);

    /// <inheritdoc />
    public virtual bool SupportsDecoder(string decoder) => _inner.SupportsDecoder(decoder);

    /// <inheritdoc />
    public virtual bool SupportsHwaccel(string hwaccel) => _inner.SupportsHwaccel(hwaccel);

    /// <inheritdoc />
    public virtual bool SupportsFilter(string filter) => _inner.SupportsFilter(filter);

    /// <inheritdoc />
    public virtual bool SupportsFilterWithOption(FilterOptionType option) => _inner.SupportsFilterWithOption(option);

    /// <inheritdoc />
    public virtual bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => _inner.SupportsBitStreamFilterWithOption(option);

    /// <inheritdoc />
    public virtual Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken)
        => _inner.ExtractAudioImage(path, imageStreamIndex, cancellationToken);

    /// <inheritdoc />
    public virtual Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
        => _inner.ExtractVideoImage(inputFile, container, mediaSource, videoStream, threedFormat, offset, cancellationToken);

    /// <inheritdoc />
    public virtual Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, ImageFormat? targetFormat, CancellationToken cancellationToken)
        => _inner.ExtractVideoImage(inputFile, container, mediaSource, imageStream, imageStreamIndex, targetFormat, cancellationToken);

    /// <inheritdoc />
    public virtual Task<string> ExtractVideoImagesOnIntervalAccelerated(
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
    public virtual Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken) => _inner.GetMediaInfo(request, cancellationToken);

    /// <inheritdoc />
    public virtual string GetInputArgument(string inputFile, MediaSourceInfo mediaSource) => _inner.GetInputArgument(inputFile, mediaSource);

    /// <inheritdoc />
    public virtual string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource) => _inner.GetInputArgument(inputFiles, mediaSource);

    /// <inheritdoc />
    public virtual string GetExternalSubtitleInputArgument(string inputFile) => _inner.GetExternalSubtitleInputArgument(inputFile);

    /// <inheritdoc />
    public virtual string GetTimeParameter(long ticks) => _inner.GetTimeParameter(ticks);

    /// <inheritdoc />
    public virtual Task ConvertImage(string inputPath, string outputPath) => _inner.ConvertImage(inputPath, outputPath);

    /// <inheritdoc />
    public virtual string EscapeSubtitleFilterPath(string path) => _inner.EscapeSubtitleFilterPath(path);

    /// <inheritdoc />
    public virtual IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber) => _inner.GetPrimaryPlaylistVobFiles(path, titleNumber);

    /// <inheritdoc />
    public virtual IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path) => _inner.GetPrimaryPlaylistM2tsFiles(path);

    /// <inheritdoc />
    public virtual string GetInputPathArgument(EncodingJobInfo state) => _inner.GetInputPathArgument(state);

    /// <inheritdoc />
    public virtual string GetInputPathArgument(string path, MediaSourceInfo mediaSource) => _inner.GetInputPathArgument(path, mediaSource);

    /// <inheritdoc />
    public virtual void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath) => _inner.GenerateConcatConfig(source, concatFilePath);
}
