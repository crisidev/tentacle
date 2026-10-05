using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Tentacle.Protocol;

/// <summary>
/// What a machine is, for the dashboard: the agent reports it for each tentacle,
/// the server for itself.
/// </summary>
public static class HostInfo
{
    /// <summary>
    /// The CPU (or board) model.
    /// </summary>
    /// <returns>The model, or the architecture when nothing names it.</returns>
    public static string CpuModel()
    {
        // x86: "model name"; Raspberry Pi kernels: "Model" (the board), since containers
        // do not see the device tree (/sys/firmware is masked).
        var lines = ReadQuietly("/proc/cpuinfo").Split('\n');
        foreach (var key in new[] { "model name", "Model" })
        {
            var model = lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal) && l.AsSpan(key.Length).TrimStart(" \t").StartsWith(":"));
            if (model is not null)
            {
                return model[(model.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }

        // Other ARM boards outside a container: the device tree names the board.
        var board = ReadQuietly("/proc/device-tree/model").TrimEnd('\0').Trim();
        return board.Length > 0 ? board : Arch();
    }

    /// <summary>
    /// The kernel release.
    /// </summary>
    /// <returns>The release, or empty.</returns>
    public static string Kernel() => ReadQuietly("/proc/sys/kernel/osrelease").Trim();

    /// <summary>
    /// The CPU architecture: x64, arm64...
    /// </summary>
    /// <returns>The architecture.</returns>
    public static string Arch() => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>
    /// The machine's name: TENTACLE_NODE_NAME (in Kubernetes, the node name from the
    /// downward API; a pod's hostname is the pod's), else the hostname.
    /// </summary>
    /// <returns>The name.</returns>
    public static string Name()
    {
        var name = Environment.GetEnvironmentVariable("TENTACLE_NODE_NAME");
        return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
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
