using System;
using MediaBrowser.Controller.MediaEncoding;
using Tentacle.Protocol;

namespace Jellyfin.Plugin.Tentacle.CrossVendor;

/// <summary>
/// Jellyfin's media encoder as it would describe a tentacle's GPU instead of the
/// server's. Jellyfin's EncodingHelper reads these device facts (which VAAPI driver,
/// whether Vulkan can map its frames) while it builds a command line, so building one
/// for a tentacle needs the tentacle's facts. Everything else (the ffmpeg build, its
/// encoders and filters) is the server's: the tentacle runs the same jellyfin-ffmpeg.
/// </summary>
/// <remarks>
/// Vulkan interop is reported as unavailable: the agent does not measure it, and
/// without it Jellyfin builds the plain VAAPI pipeline, which is the one tentacles'
/// self-tests prove. HDR sources are not rebuilt, so the Vulkan and OpenCL tonemapping
/// paths it would unlock are not needed.
/// </remarks>
public sealed class WorkerMediaEncoder : MediaEncoderDecorator
{
    private readonly GpuInfo _gpu;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerMediaEncoder"/> class.
    /// </summary>
    /// <param name="inner">The server's media encoder.</param>
    /// <param name="gpu">The tentacle's GPU.</param>
    public WorkerMediaEncoder(IMediaEncoder inner, GpuInfo gpu)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
    }

    /// <inheritdoc />
    public override bool IsVaapiDeviceAmd => IsVaapi && string.Equals(_gpu.Vendor, Hardware.Amd, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool IsVaapiDeviceInteliHD => IsVaapi && string.Equals(_gpu.Vendor, Hardware.Intel, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool IsVaapiDeviceInteli965 => false;

    /// <inheritdoc />
    public override bool IsVaapiDeviceSupportVulkanDrmModifier => false;

    /// <inheritdoc />
    public override bool IsVaapiDeviceSupportVulkanDrmInterop => false;

    /// <inheritdoc />
    public override bool IsVideoToolboxAv1DecodeAvailable => false;

    private bool IsVaapi => string.Equals(_gpu.Api, "vaapi", StringComparison.Ordinal);
}
