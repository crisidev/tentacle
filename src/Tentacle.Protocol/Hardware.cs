using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tentacle.Protocol;

/// <summary>
/// One GPU as an agent found it: what it is, which Jellyfin hardware type drives
/// it, and what it proved it can encode and decode.
/// </summary>
public sealed class GpuInfo : IValidated
{
    /// <summary>Gets or sets the device node (/dev/dri/renderD128, /dev/nvidia0).</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Gets or sets the vendor: intel, amd, nvidia, or what the PCI id / driver says.</summary>
    public string Vendor { get; set; } = string.Empty;

    /// <summary>Gets or sets the PCI vendor:device id (8086:9a49), empty for platform devices.</summary>
    public string PciId { get; set; } = string.Empty;

    /// <summary>Gets or sets the kernel driver (i915, xe, amdgpu, nvidia, v3d...).</summary>
    public string Driver { get; set; } = string.Empty;

    /// <summary>Gets or sets the model, as the VAAPI driver or nvidia-smi names it; may be empty.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin hardware acceleration type that drives it (qsv, vaapi, nvenc), or none.</summary>
    public string Api { get; set; } = Hardware.NoApi;

    /// <summary>Gets or sets what it proved it can do: encode:h264, decode:hevc10... (see <see cref="Hardware.Capabilities"/>).</summary>
    public string[] Capabilities { get; set; } = [];

    /// <summary>Gets or sets why a probe failed, when one did (a missing driver, a permission).</summary>
    public string Detail { get; set; } = string.Empty;

    /// <inheritdoc />
    public void Validate()
    {
        Validation.NoNulls(Capabilities, nameof(Capabilities));
    }
}

/// <summary>
/// GPU vendors, the Jellyfin hardware types that drive them, and the probes that
/// measure them. Pure functions: the agent runs the commands, tests check them.
/// </summary>
public static partial class Hardware
{
    /// <summary>No hardware acceleration type.</summary>
    public const string NoApi = "none";

    /// <summary>Intel.</summary>
    public const string Intel = "intel";

    /// <summary>AMD.</summary>
    public const string Amd = "amd";

    /// <summary>NVIDIA.</summary>
    public const string Nvidia = "nvidia";

    /// <summary>
    /// Every capability a GPU is probed for, in display order: "encode:" or "decode:"
    /// plus a codec; "10" means 10-bit.
    /// </summary>
    public static readonly IReadOnlyList<string> Capabilities =
    [
        "encode:h264", "encode:hevc", "encode:hevc10", "encode:av1",
        "decode:h264", "decode:hevc", "decode:hevc10", "decode:vp9", "decode:av1",
    ];

    private static readonly Dictionary<string, string> PciVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x8086"] = Intel,
        ["0x1002"] = Amd,
        ["0x1022"] = Amd,
        ["0x10de"] = Nvidia,
    };

    private static readonly Dictionary<string, string> DriverVendors = new(StringComparer.Ordinal)
    {
        ["i915"] = Intel,
        ["xe"] = Intel,
        ["amdgpu"] = Amd,
        ["radeon"] = Amd,
        ["nvidia"] = Nvidia,
        ["nouveau"] = Nvidia,
        ["v3d"] = "broadcom",
        ["vc4"] = "broadcom",
    };

    /// <summary>
    /// The vendor of a GPU, from its PCI vendor id (sysfs "0x8086") or else its kernel driver.
    /// </summary>
    /// <param name="pciVendor">The PCI vendor id, or empty.</param>
    /// <param name="driver">The kernel driver, or empty.</param>
    /// <returns>intel, amd, nvidia, broadcom, the driver name, or "unknown".</returns>
    public static string VendorOf(string? pciVendor, string? driver)
    {
        if (!string.IsNullOrEmpty(pciVendor) && PciVendors.TryGetValue(pciVendor.Trim(), out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrEmpty(driver))
        {
            return DriverVendors.TryGetValue(driver, out var byDriver) ? byDriver : driver;
        }

        return "unknown";
    }

    /// <summary>
    /// The vendor of the GPU behind a render node on this machine, from sysfs.
    /// </summary>
    /// <param name="renderNode">/dev/dri/renderD128.</param>
    /// <returns>The vendor, or null when sysfs does not say.</returns>
    public static string? VendorOfRenderNode(string? renderNode)
    {
        if (string.IsNullOrWhiteSpace(renderNode))
        {
            return null;
        }

        try
        {
            var sys = $"/sys/class/drm/{Path.GetFileName(renderNode)}/device";
            var id = File.Exists($"{sys}/vendor") ? File.ReadAllText($"{sys}/vendor").Trim() : string.Empty;
            var link = new FileInfo($"{sys}/driver").LinkTarget;
            var vendor = VendorOf(id, link is null ? null : Path.GetFileName(link));
            return vendor == "unknown" ? null : vendor;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Jellyfin hardware acceleration type that drives a GPU on Linux: QSV for
    /// Intel, NVENC for NVIDIA (the proprietary driver only), VAAPI for AMD (AMF is
    /// Windows-only). Anything else has no video API Jellyfin can use.
    /// </summary>
    /// <param name="vendor">The vendor.</param>
    /// <param name="driver">The kernel driver.</param>
    /// <returns>qsv, nvenc, vaapi or none.</returns>
    public static string ApiFor(string vendor, string driver) => vendor switch
    {
        Intel => "qsv",
        Amd when driver == "amdgpu" => "vaapi",
        Nvidia when driver == "nvidia" => "nvenc",
        _ => NoApi,
    };

    /// <summary>
    /// The vendor a Jellyfin hardware acceleration type needs, or null when it
    /// depends on the device (VAAPI drives Intel and AMD alike).
    /// </summary>
    /// <param name="api">qsv, nvenc, vaapi, amf...</param>
    /// <returns>The vendor, or null.</returns>
    public static string? VendorOfApi(string? api) => api switch
    {
        "qsv" => Intel,
        "nvenc" or "cuda" => Nvidia,
        "amf" => Amd,
        _ => null,
    };

    /// <summary>
    /// The model in the VAAPI driver line that `ffmpeg -v verbose -init_hw_device vaapi`
    /// prints, e.g. "Mesa Gallium driver 25.0.7 for AMD Radeon Graphics (radeonsi, renoir,
    /// ...)" → "AMD Radeon Graphics (renoir)"; "Intel iHD driver for Intel(R) Gen Graphics -
    /// 25.2.6 ()" → "Intel GPU (iHD 25.2.6)".
    /// </summary>
    /// <param name="stderr">ffmpeg's stderr.</param>
    /// <returns>The model, or empty.</returns>
    public static string ModelFromVaapi(string stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        var line = VaapiDriverLine().Match(stderr);
        if (!line.Success)
        {
            return string.Empty;
        }

        var text = line.Groups["d"].Value.Trim().TrimEnd('.');
        var mesa = MesaDriver().Match(text);
        if (mesa.Success)
        {
            var parts = mesa.Groups["extra"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var chip = parts.Length > 1 ? $" ({parts[1]})" : string.Empty;
            return mesa.Groups["model"].Value.Trim() + chip;
        }

        var ihd = IntelDriver().Match(text);
        // The iHD driver does not name the chip: say so rather than pass the driver off as a model.
        return ihd.Success ? $"Intel GPU ({ihd.Groups["drv"].Value} {ihd.Groups["ver"].Value})" : text;
    }

    /// <summary>
    /// The ffmpeg arguments (after -hide_banner -v error) that open a GPU's device the
    /// way Jellyfin does, ready for a filter graph or an encoder.
    /// </summary>
    /// <param name="api">qsv, vaapi or nvenc.</param>
    /// <param name="device">The device node.</param>
    /// <returns>The arguments, or null for an API without a device.</returns>
    public static string[]? DeviceArgs(string api, string device) => api switch
    {
        "qsv" => ["-init_hw_device", $"vaapi=va:{device},driver=iHD", "-init_hw_device", "qsv=qs@va", "-filter_hw_device", "qs"],
        "vaapi" => ["-init_hw_device", $"vaapi=va:{device}", "-filter_hw_device", "va"],
        "nvenc" => ["-init_hw_device", $"cuda=cu:{NvidiaIndex(device)}", "-filter_hw_device", "cu"],
        _ => null,
    };

    /// <summary>
    /// A one-second hardware encode of a test pattern: the encoder must accept the frames.
    /// </summary>
    /// <param name="api">qsv, vaapi or nvenc.</param>
    /// <param name="device">The device node.</param>
    /// <param name="codec">h264, hevc, hevc10 or av1.</param>
    /// <returns>The arguments, or null when the API has no such encoder.</returns>
    public static string[]? EncodeProbe(string api, string device, string codec)
    {
        if (DeviceArgs(api, device) is not { } open)
        {
            return null;
        }

        var tenBit = codec == "hevc10";
        var name = (tenBit ? "hevc" : codec) + "_" + api;
        var upload = api switch
        {
            "qsv" => $"format={(tenBit ? "p010le" : "nv12")},hwupload=extra_hw_frames=16,format=qsv",
            "vaapi" => $"format={(tenBit ? "p010le" : "nv12")},hwupload",
            _ => $"format={(tenBit ? "p010le" : "nv12")},hwupload_cuda",
        };
        string[] profile = tenBit ? ["-profile:v", "main10"] : [];
        return
        [
            "-hide_banner", "-v", "error", .. open,
            "-f", "lavfi", "-i", "testsrc2=s=640x360:r=10:d=1",
            "-vf", upload, "-c:v", name, .. profile, "-f", "null", "-",
        ];
    }

    /// <summary>
    /// A software encode of a short sample, for the decode probes to read.
    /// </summary>
    /// <param name="codec">h264, hevc, hevc10, vp9 or av1.</param>
    /// <param name="file">Where to write it (.mkv).</param>
    /// <returns>The arguments.</returns>
    public static string[] SampleArgs(string codec, string file)
    {
        string[] encoder = codec switch
        {
            "h264" => ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"],
            "hevc" => ["-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-x265-params", "log-level=error"],
            "hevc10" => ["-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p10le", "-x265-params", "log-level=error"],
            "vp9" => ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-pix_fmt", "yuv420p"],
            "av1" => ["-c:v", "libsvtav1", "-preset", "12", "-pix_fmt", "yuv420p"],
            _ => throw new ArgumentException($"no sample for {codec}", nameof(codec)),
        };
        return ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=10:d=1", .. encoder, file];
    }

    /// <summary>
    /// A hardware decode of a sample. The frames are downloaded from the GPU, so a
    /// decoder that silently falls back to software fails the probe instead of passing it.
    /// </summary>
    /// <param name="api">qsv, vaapi or nvenc.</param>
    /// <param name="device">The device node.</param>
    /// <param name="sample">The sample file.</param>
    /// <returns>The arguments, or null for an API without hardware decoding.</returns>
    public static string[]? DecodeProbe(string api, string device, string sample)
    {
        // Jellyfin decodes for QSV through VAAPI on Linux (-hwaccel vaapi, as in its
        // command lines), so Intel is probed the same way.
        string[]? hwaccel = api switch
        {
            "qsv" => ["-init_hw_device", $"vaapi=va:{device},driver=iHD", "-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"],
            "vaapi" => ["-init_hw_device", $"vaapi=va:{device}", "-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"],
            "nvenc" => ["-init_hw_device", $"cuda=cu:{NvidiaIndex(device)}", "-hwaccel", "cuda", "-hwaccel_device", "cu", "-hwaccel_output_format", "cuda"],
            _ => null,
        };
        return hwaccel is null
            ? null
            : ["-hide_banner", "-v", "error", .. hwaccel, "-i", sample, "-vf", "hwdownload,format=nv12|p010le", "-f", "null", "-"];
    }

    /// <summary>
    /// Points the hardware device options of a command line Jellyfin built for its own
    /// GPU at this tentacle's: render nodes in -init_hw_device, -hwaccel_device,
    /// -qsv_device and -vaapi_device values become <paramref name="device"/>, and a
    /// device left empty (libva picks one by vendor) is named, so a host with two GPUs
    /// runs the job on the one the tentacle stands for. Nothing else is touched.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <param name="device">This tentacle's render node.</param>
    /// <returns>The arguments, rewritten where needed.</returns>
    public static string[] PointAt(IReadOnlyList<string> args, string device)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(device);
        var result = args.ToArray();
        if (!device.StartsWith("/dev/dri/renderD", StringComparison.Ordinal))
        {
            return result;
        }

        for (var i = 0; i + 1 < result.Length; i++)
        {
            switch (result[i])
            {
                case "-init_hw_device":
                    var value = RenderNode().Replace(result[i + 1], device);

                    // "vaapi=va:,vendor_id=0x8086,..." or "vaapi=va:" → "vaapi=va:<device>,..."
                    value = EmptyVaapiDevice().Replace(value, m => m.Groups["head"].Value + device + m.Groups["tail"].Value);
                    result[i + 1] = value;
                    break;
                case "-hwaccel_device" or "-qsv_device" or "-vaapi_device":
                    result[i + 1] = RenderNode().Replace(result[i + 1], device);
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Display name for a capability ("decode:hevc10" → "HEVC 10-bit").
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <returns>The codec part, for people.</returns>
    public static string CodecLabel(string capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var codec = capability[(capability.IndexOf(':', StringComparison.Ordinal) + 1)..];
        return codec switch
        {
            "h264" => "H.264",
            "hevc" => "HEVC",
            "hevc10" => "HEVC 10-bit",
            "vp9" => "VP9",
            "av1" => "AV1",
            _ => codec.ToUpperInvariant(),
        };
    }

    /// <summary>
    /// The CUDA device index of /dev/nvidiaN (0 when it is not of that form).
    /// </summary>
    private static int NvidiaIndex(string device)
        => int.TryParse(Path.GetFileName(device).AsSpan("nvidia".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && Path.GetFileName(device).StartsWith("nvidia", StringComparison.Ordinal) ? i : 0;

    [GeneratedRegex(@"VAAPI driver:\s*(?<d>[^\r\n]+)")]
    private static partial Regex VaapiDriverLine();

    [GeneratedRegex(@"^Mesa Gallium driver \S+ for (?<model>.+?) \((?<extra>[^)]*)\)$")]
    private static partial Regex MesaDriver();

    [GeneratedRegex(@"^Intel (?<drv>\S+) driver .*?- (?<ver>[\d.]+)")]
    private static partial Regex IntelDriver();

    [GeneratedRegex(@"/dev/dri/renderD\d+")]
    private static partial Regex RenderNode();

    [GeneratedRegex(@"(?<head>^vaapi=[^:,@]*:)(?<tail>,|$)")]
    private static partial Regex EmptyVaapiDevice();
}
