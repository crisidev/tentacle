using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Model.Entities;
using Tentacle.Protocol;
using EncodingOptions = MediaBrowser.Model.Configuration.EncodingOptions;

namespace Jellyfin.Plugin.Tentacle.CrossVendor;

/// <summary>
/// The encoding settings Jellyfin would have for a tentacle's GPU: the server's own,
/// with the hardware type, device and codecs of that GPU, as its agent measured them.
/// </summary>
public static class WorkerEncodingOptions
{
    /// <summary>
    /// The hardware acceleration type a tentacle's GPU is driven with, if Tentacle can
    /// rebuild command lines for it: VAAPI (AMD, Intel) and NVENC (NVIDIA).
    /// </summary>
    /// <param name="gpu">The tentacle's GPU.</param>
    /// <returns>The type, or null.</returns>
    public static HardwareAccelerationType? TypeOf(GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        return gpu.Api switch
        {
            "vaapi" => HardwareAccelerationType.vaapi,
            "nvenc" or "cuda" => HardwareAccelerationType.nvenc,
            _ => null,
        };
    }

    /// <summary>
    /// Whether a tentacle's GPU can encode a codec in hardware (its agent's measurement).
    /// </summary>
    /// <param name="gpu">The tentacle's GPU.</param>
    /// <param name="codec">Jellyfin's output codec (h264, hevc/h265, av1).</param>
    /// <returns>True if it can.</returns>
    public static bool CanEncode(GpuInfo gpu, string? codec)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        var name = codec?.ToLowerInvariant() switch
        {
            "h264" or "avc" => "h264",
            "hevc" or "h265" => "hevc",
            "av1" => "av1",
            _ => null,
        };
        return name is not null && gpu.Capabilities.Contains("encode:" + name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds the settings for a tentacle's GPU from the server's.
    /// </summary>
    /// <param name="server">The server's encoding settings.</param>
    /// <param name="gpu">The tentacle's GPU.</param>
    /// <returns>The settings, or null for a GPU Tentacle cannot drive.</returns>
    public static EncodingOptions? For(EncodingOptions server, GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(gpu);
        if (TypeOf(gpu) is not { } type)
        {
            return null;
        }

        // A copy: everything not about the GPU (bitrates, presets, threads, deinterlacing,
        // audio) stays the server's, so the rest of the command line is unchanged.
        var options = JsonSerializer.Deserialize<EncodingOptions>(JsonSerializer.Serialize(server))!;
        var decode = gpu.Capabilities.Where(c => c.StartsWith("decode:", StringComparison.Ordinal)).Select(c => c[7..]).ToHashSet(StringComparer.Ordinal);
        var encode = gpu.Capabilities.Where(c => c.StartsWith("encode:", StringComparison.Ordinal)).Select(c => c[7..]).ToHashSet(StringComparer.Ordinal);

        options.HardwareAccelerationType = type;
        options.VaapiDevice = type == HardwareAccelerationType.vaapi ? gpu.Device : options.VaapiDevice;
        options.QsvDevice = string.Empty;
        options.EnableHardwareEncoding = true;
        options.HardwareDecodingCodecs = DecodingCodecs(decode);
        options.EnableDecodingColorDepth10Hevc = decode.Contains("hevc10");
        options.EnableDecodingColorDepth10Vp9 = false;
        options.EnableDecodingColorDepth10HevcRext = false;
        options.EnableDecodingColorDepth12HevcRext = false;
        options.AllowHevcEncoding = server.AllowHevcEncoding && encode.Contains("hevc");
        options.AllowAv1Encoding = server.AllowAv1Encoding && encode.Contains("av1");

        // Intel-only encoder modes, and tonemapping (HDR sources are not rebuilt).
        options.EnableIntelLowPowerH264HwEncoder = false;
        options.EnableIntelLowPowerHevcHwEncoder = false;
        options.EnableTonemapping = false;
        options.EnableVppTonemapping = false;
        options.EnableVideoToolboxTonemapping = false;
        return options;
    }

    private static string[] DecodingCodecs(HashSet<string> decode)
    {
        // Jellyfin's names for what the agent measured; vc1, mpeg2 and vp8 are not
        // measured, so they decode in software there.
        var codecs = new List<string>();
        foreach (var (measured, jellyfin) in new[] { ("h264", "h264"), ("hevc", "hevc"), ("vp9", "vp9"), ("av1", "av1") })
        {
            if (decode.Contains(measured))
            {
                codecs.Add(jellyfin);
            }
        }

        return codecs.ToArray();
    }
}
