using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Tentacle.Protocol;

/// <summary>
/// Finds the GPUs this agent can use and measures each one: vendor, PCI id and
/// kernel driver from sysfs, the model from the VAAPI driver, then a short hardware
/// encode per codec and a hardware decode of a software-made sample per codec.
/// Runs once at agent (and server) start; a few seconds per GPU.
/// </summary>
public static class GpuProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The GPUs to register a tentacle for.
    /// </summary>
    /// <param name="ffmpeg">The real ffmpeg.</param>
    /// <param name="selection">TENTACLE_GPUS: empty or "auto" for all, "none" for none, or device paths separated by commas.</param>
    /// <param name="log">Where to report.</param>
    /// <param name="stop">Cancellation.</param>
    /// <returns>The GPUs, measured; empty for a CPU-only host.</returns>
    public static async Task<IReadOnlyList<GpuInfo>> DetectAsync(string ffmpeg, string? selection, Action<string> log, CancellationToken stop)
    {
        var mode = (selection ?? string.Empty).Trim();
        if (string.Equals(mode, "none", StringComparison.Ordinal))
        {
            return [];
        }

        var devices = Devices();
        if (mode.Length > 0 && !string.Equals(mode, "auto", StringComparison.Ordinal))
        {
            var wanted = mode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var missing in wanted.Except(devices, StringComparer.Ordinal))
            {
                log($"TENTACLE_GPUS: {missing} is not in this container");
            }

            devices = devices.Where(wanted.Contains).ToList();
        }

        var gpus = new List<GpuInfo>();
        foreach (var device in devices)
        {
            var gpu = Identify(device);
            if (gpu is null)
            {
                continue;
            }

            await MeasureAsync(gpu, ffmpeg, stop).ConfigureAwait(false);
            gpus.Add(gpu);
        }

        return gpus;
    }

    /// <summary>
    /// The CPU (or board) model.
    /// </summary>
    /// <returns>The model.</returns>
    public static string CpuModel() => HostInfo.CpuModel();

    /// <summary>
    /// The kernel release.
    /// </summary>
    /// <returns>The release, or empty.</returns>
    public static string Kernel() => HostInfo.Kernel();

    /// <summary>
    /// A one-line description for the log: "/dev/dri/renderD128 intel 8086:9a49 i915 qsv: encode h264 hevc ...".
    /// </summary>
    /// <param name="gpu">The GPU.</param>
    /// <returns>The description.</returns>
    public static string Describe(GpuInfo gpu)
    {
        var enc = gpu.Capabilities.Where(c => c.StartsWith("encode:", StringComparison.Ordinal)).Select(c => c[7..]);
        var dec = gpu.Capabilities.Where(c => c.StartsWith("decode:", StringComparison.Ordinal)).Select(c => c[7..]);
        var model = gpu.Model.Length > 0 ? $" \"{gpu.Model}\"" : string.Empty;
        var detail = gpu.Detail.Length > 0 ? $" ({gpu.Detail})" : string.Empty;
        return $"{gpu.Device}: {gpu.Vendor} {gpu.PciId} {gpu.Driver}{model}, api {gpu.Api}, encode [{string.Join(' ', enc)}], decode [{string.Join(' ', dec)}]{detail}";
    }

    /// <summary>
    /// GPU device nodes in this container: DRM render nodes, and NVIDIA's own nodes for
    /// the proprietary driver (whose render nodes are left out: CUDA addresses the card).
    /// </summary>
    private static List<string> Devices()
    {
        var devices = new List<string>();
        try
        {
            if (Directory.Exists("/dev/dri"))
            {
                devices.AddRange(Directory.EnumerateFileSystemEntries("/dev/dri", "renderD*")
                    .Where(d => !string.Equals(Driver(Path.GetFileName(d)), "nvidia", StringComparison.Ordinal)));
            }

            devices.AddRange(Directory.EnumerateFileSystemEntries("/dev", "nvidia*")
                .Where(d => Path.GetFileName(d).AsSpan("nvidia".Length) is { Length: > 0 } n && n.IndexOfAnyExceptInRange('0', '9') < 0));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        devices.Sort(StringComparer.Ordinal);
        return devices;
    }

    private static GpuInfo? Identify(string device)
    {
        var name = Path.GetFileName(device);
        if (name.StartsWith("nvidia", StringComparison.Ordinal))
        {
            return new GpuInfo { Device = device, Vendor = Hardware.Nvidia, Driver = "nvidia", Api = "nvenc" };
        }

        var sys = $"/sys/class/drm/{name}/device";
        var vendorId = ReadQuietly($"{sys}/vendor").Trim();
        var deviceId = ReadQuietly($"{sys}/device").Trim();
        var driver = Driver(name);
        var vendor = Hardware.VendorOf(vendorId, driver);
        return new GpuInfo
        {
            Device = device,
            Vendor = vendor,
            PciId = vendorId.Length > 0 && deviceId.Length > 0 ? $"{vendorId.Replace("0x", string.Empty, StringComparison.Ordinal)}:{deviceId.Replace("0x", string.Empty, StringComparison.Ordinal)}" : string.Empty,
            Driver = driver,
            Api = Hardware.ApiFor(vendor, driver),
        };
    }

    private static string Driver(string renderNode)
    {
        try
        {
            var link = new FileInfo($"/sys/class/drm/{renderNode}/device/driver").LinkTarget;
            return link is null ? string.Empty : Path.GetFileName(link);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static async Task MeasureAsync(GpuInfo gpu, string ffmpeg, CancellationToken stop)
    {
        if (gpu.Api == Hardware.NoApi)
        {
            gpu.Detail = $"no video API Jellyfin can use with {gpu.Vendor}/{(gpu.Driver.Length > 0 ? gpu.Driver : "unknown driver")}";
            return;
        }

        // Open the device first: without it every probe fails the same way.
        if (gpu.Api is "qsv" or "vaapi")
        {
            var open = Hardware.DeviceArgs("vaapi", gpu.Device)!;
            if (gpu.Api == "qsv")
            {
                open = ["-init_hw_device", $"vaapi=va:{gpu.Device},driver=iHD"];
            }

            var (ok, stderr) = await RunAsync(ffmpeg, ["-hide_banner", "-v", "verbose", .. open, "-f", "lavfi", "-i", "nullsrc=s=64x64:d=0.1", "-f", "null", "-"], stop).ConfigureAwait(false);
            gpu.Model = Hardware.ModelFromVaapi(stderr);
            if (!ok)
            {
                gpu.Detail = "cannot open the device: " + Tail(stderr);
                return;
            }
        }
        else if (gpu.Api == "nvenc")
        {
            gpu.Model = await NvidiaModelAsync(gpu.Device, stop).ConfigureAwait(false);
        }

        var found = new List<string>();
        foreach (var codec in new[] { "h264", "hevc", "hevc10", "av1" })
        {
            if (Hardware.EncodeProbe(gpu.Api, gpu.Device, codec) is { } args && await ProbeAsync(ffmpeg, args, stop).ConfigureAwait(false))
            {
                found.Add("encode:" + codec);
            }
        }

        var dir = Path.Combine(Path.GetTempPath(), "tentacle-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var codec in new[] { "h264", "hevc", "hevc10", "vp9", "av1" })
            {
                var sample = Path.Combine(dir, codec + ".mkv");
                if (!(await RunAsync(ffmpeg, Hardware.SampleArgs(codec, sample), stop).ConfigureAwait(false)).Ok)
                {
                    continue;
                }

                if (Hardware.DecodeProbe(gpu.Api, gpu.Device, sample) is { } args && await ProbeAsync(ffmpeg, args, stop).ConfigureAwait(false))
                {
                    found.Add("decode:" + codec);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        gpu.Capabilities = Hardware.Capabilities.Where(found.Contains).ToArray();
        if (gpu.Capabilities.Length == 0)
        {
            gpu.Detail = "the device opens, but no encode or decode probe passed";
        }
    }

    /// <summary>
    /// A capability probe. A run killed by a signal is tried again, up to three times: the iHD driver
    /// sometimes crashes tearing down an AV1 decode that worked (seen on Alder Lake,
    /// 2026-10), which says nothing about the GPU being able to do the job.
    /// </summary>
    private static async Task<bool> ProbeAsync(string ffmpeg, string[] args, CancellationToken stop)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (ok, _, code) = await RunWithCodeAsync(ffmpeg, args, stop).ConfigureAwait(false);
            if (ok || code <= 128)
            {
                return ok;
            }
        }

        return false;
    }

    private static async Task<string> NvidiaModelAsync(string device, CancellationToken stop)
    {
        var index = Path.GetFileName(device)["nvidia".Length..];
        var (ok, output) = await RunAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader", "-i", index], stop, stdout: true).ConfigureAwait(false);
        return ok ? output.Trim() : string.Empty;
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string file, string[] args, CancellationToken stop, bool stdout = false)
    {
        var (ok, output, _) = await RunWithCodeAsync(file, args, stop, stdout).ConfigureAwait(false);
        return (ok, output);
    }

    private static async Task<(bool Ok, string Output, int Code)> RunWithCodeAsync(string file, string[] args, CancellationToken stop, bool stdout = false)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var err = process.StandardError.ReadToEndAsync(stop);
            var output = process.StandardOutput.ReadToEndAsync(stop);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(ProbeTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (false, "timed out", -1);
            }

            var text = stdout ? await output.ConfigureAwait(false) : await err.ConfigureAwait(false);
            return (process.ExitCode == 0, text, process.ExitCode);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (false, e.Message, -1);
        }
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("[AVHWDeviceContext", StringComparison.Ordinal) || l.Contains("fail", StringComparison.OrdinalIgnoreCase))
            .TakeLast(2);
        var tail = string.Join(" / ", lines);
        return tail.Length > 300 ? tail[^300..] : tail;
    }

    private static string ReadQuietly(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
