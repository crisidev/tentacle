using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tentacle;

/// <summary>
/// Puts the shim in front of Jellyfin's ffmpeg, so the plugin alone is enough on the
/// server. The plugin ships the `tentacle` binary for each architecture; at startup,
/// before Jellyfin validates ffmpeg, <see cref="Install"/> copies the right one to
/// <c>&lt;data&gt;/tentacle/bin</c> with its <c>ffmpeg</c> and <c>ffprobe</c> links.
/// Then either:
/// <list type="bullet">
/// <item>Jellyfin's ffmpeg path (JELLYFIN_FFMPEG, FFMPEG_PATH, --ffmpeg) already names
/// the shim: the supported way, which works with any image;</item>
/// <item>or <see cref="Redirect"/> swaps it in after Jellyfin validated the real
/// ffmpeg. That needs a private field of Jellyfin's encoder: when a Jellyfin release
/// changes it, the dashboard says so and every job runs on the server, as without
/// Tentacle.</item>
/// </list>
/// </summary>
public sealed partial class ShimInstaller
{
    /// <summary>
    /// The field behind <see cref="IMediaEncoder.EncoderPath"/> in Jellyfin's MediaEncoder:
    /// the path every ffmpeg it starts uses, trickplay and image extraction included.
    /// </summary>
    private const string FfmpegPathField = "_ffmpegPath";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);
    private readonly ILogger<ShimInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShimInstaller"/> class.
    /// </summary>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ShimInstaller(IApplicationPaths paths, ILogger<ShimInstaller> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        BinDirectory = Path.Combine(paths.DataPath, "tentacle", "bin");
        _logger = logger;
    }

    /// <summary>Gets the directory the shim is installed into.</summary>
    public string BinDirectory { get; }

    /// <summary>Gets the ffmpeg Jellyfin runs when it is the shim.</summary>
    public string? ShimPath { get; private set; }

    /// <summary>Gets the real ffmpeg behind the shim.</summary>
    public string? RealPath { get; private set; }

    /// <summary>Gets the installed shim, the path to give JELLYFIN_FFMPEG or FFMPEG_PATH.</summary>
    public string InstalledPath => Path.Combine(BinDirectory, "ffmpeg");

    /// <summary>Gets why Jellyfin runs its own ffmpeg instead of the shim, if it does.</summary>
    public string? Error { get; private set; } = "Jellyfin has not set up ffmpeg yet";

    private string? InstallError { get; set; } = "not installed yet";

    /// <summary>
    /// The binary name the plugin ships for an architecture.
    /// </summary>
    /// <param name="architecture">The architecture.</param>
    /// <returns>The file name, or null for an architecture Tentacle is not built for.</returns>
    public static string? BinaryName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "tentacle-amd64",
        Architecture.Arm64 => "tentacle-arm64",
        _ => null,
    };

    /// <summary>
    /// Makes the shim the ffmpeg of <paramref name="encoder"/>, which has just found
    /// and validated the real one.
    /// </summary>
    /// <param name="encoder">Jellyfin's media encoder.</param>
    public void Redirect(IMediaEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        try
        {
            Error = RedirectCore(encoder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or ArgumentException or FieldAccessException or TargetException)
        {
            Error = e.Message;
        }

        if (Error is not null)
        {
            ShimPath = null;
            LogNotRedirected(Error);
        }
    }

    private string? RedirectCore(IMediaEncoder encoder)
    {
        var current = encoder.EncoderPath;
        if (string.IsNullOrEmpty(current))
        {
            return "Jellyfin has no ffmpeg path (is FFmpeg validation skipped?)";
        }

        var real = ResolveExecutable(current);
        if (real is null)
        {
            return $"cannot find Jellyfin's ffmpeg '{current}'";
        }

        // Jellyfin's ffmpeg path (JELLYFIN_FFMPEG, FFMPEG_PATH) names a shim, this
        // plugin's or the old server mod's: the supported way, nothing to swap.
        if (IsTentacle(real))
        {
            ShimPath = current;
            RealPath = Path.Combine(Environment.GetEnvironmentVariable("TENTACLE_REAL_DIR") ?? "/usr/lib/jellyfin-ffmpeg", "ffmpeg");
            LogAlreadyShim(current);
            return null;
        }

        // The shim runs <TENTACLE_REAL_DIR>/ffmpeg and ffprobe: the real binaries must
        // carry those names, as Jellyfin's own ffprobe lookup also assumes.
        if (!string.Equals(Path.GetFileName(real), "ffmpeg", StringComparison.Ordinal))
        {
            return $"Jellyfin's ffmpeg {real} is not named 'ffmpeg'";
        }

        var realDir = Path.GetDirectoryName(real)!;
        var configured = Environment.GetEnvironmentVariable("TENTACLE_REAL_DIR");
        if (string.IsNullOrEmpty(configured))
        {
            // The shims Jellyfin starts inherit it.
            Environment.SetEnvironmentVariable("TENTACLE_REAL_DIR", realDir);
        }
        else if (!string.Equals(configured.TrimEnd('/'), realDir, StringComparison.Ordinal))
        {
            return $"TENTACLE_REAL_DIR is {configured} but Jellyfin's ffmpeg is {real}";
        }

        if (InstallError is not null)
        {
            Install();
        }

        if (InstallError is not null)
        {
            return InstallError;
        }

        var shim = InstalledPath;

        // The shim runs `-version` itself with the real binary: the same first line
        // proves it starts here and finds the right ffmpeg.
        var want = VersionLine(real);
        var got = VersionLine(shim);
        if (want is null || !string.Equals(want, got, StringComparison.Ordinal))
        {
            return $"the shim {shim} does not answer like {real} (got '{got ?? "nothing"}', want '{want ?? "nothing"}'): is {BinDirectory} mounted noexec?";
        }

        var field = encoder.GetType().GetField(FfmpegPathField, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null || field.FieldType != typeof(string) || field.IsInitOnly)
        {
            return $"this Jellyfin cannot be switched to the shim on its own: set JELLYFIN_FFMPEG (FFMPEG_PATH in the LinuxServer image) to {shim} and restart";
        }

        field.SetValue(encoder, shim);
        ShimPath = shim;
        RealPath = real;
        LogRedirected(shim, real);
        return null;
    }

    /// <summary>
    /// Copies this architecture's binary from the plugin into <see cref="BinDirectory"/>
    /// (only when it changed) and links ffmpeg and ffprobe to it. Runs when the plugin
    /// starts, before Jellyfin validates its ffmpeg, so a JELLYFIN_FFMPEG pointing at
    /// the shim works from the first start. Never throws.
    /// </summary>
    public void Install()
    {
        try
        {
            InstallError = InstallCore();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
        {
            InstallError = e.Message;
        }

        if (InstallError is null)
        {
            LogInstalled(InstalledPath);
        }
        else
        {
            LogInstallFailed(InstallError);
        }
    }

    private string? InstallCore()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Tentacle runs on Linux only";
        }

        var name = BinaryName(RuntimeInformation.OSArchitecture);
        if (name is null)
        {
            return $"Tentacle is not built for {RuntimeInformation.OSArchitecture}";
        }

        var pluginDir = Path.GetDirectoryName(typeof(ShimInstaller).Assembly.Location);
        if (string.IsNullOrEmpty(pluginDir))
        {
            pluginDir = Path.GetDirectoryName(Plugin.Instance?.AssemblyFilePath);
        }

        var source = string.IsNullOrEmpty(pluginDir) ? null : Path.Combine(pluginDir, name);
        if (source is null || !File.Exists(source))
        {
            return $"the plugin has no {name} binary (expected next to {typeof(ShimInstaller).Assembly.GetName().Name}.dll)";
        }

        Directory.CreateDirectory(BinDirectory);
        var target = Path.Combine(BinDirectory, "tentacle");
        if (!SameContent(source, target))
        {
            // Replace by rename: a shim still running from the old file keeps its inode.
            var staging = target + ".new";
            File.Copy(source, staging, overwrite: true);
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(staging, target, overwrite: true);
        }

        foreach (var tool in new[] { "ffmpeg", "ffprobe" })
        {
            var link = Path.Combine(BinDirectory, tool);
            if (!string.Equals(new FileInfo(link).LinkTarget, "tentacle", StringComparison.Ordinal))
            {
                File.Delete(link);
                File.CreateSymbolicLink(link, "tentacle");
            }
        }

        return null;
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (!fb.Exists || fa.Length != fb.Length)
        {
            return false;
        }

        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    /// <summary>
    /// A command as Process.Start would find it: absolute, or looked up in PATH.
    /// </summary>
    private static string? ResolveExecutable(string path)
    {
        if (path.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, path))
            .FirstOrDefault(File.Exists);
    }

    private static bool IsTentacle(string path)
    {
        var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        return string.Equals(Path.GetFileName(target), "tentacle", StringComparison.Ordinal);
    }

    private static string? VersionLine(string ffmpeg)
    {
        var start = new ProcessStartInfo(ffmpeg, "-version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        var line = process.StandardOutput.ReadLine()?.Trim();
        if (!process.WaitForExit(VersionTimeout))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        return process.ExitCode == 0 ? line : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle: shim installed at {Path}")]
    private partial void LogInstalled(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tentacle: could not install the shim: {Reason}")]
    private partial void LogInstallFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle: Jellyfin runs ffmpeg through the shim {Shim} (real ffmpeg {Real})")]
    private partial void LogRedirected(string shim, string real);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle: Jellyfin's ffmpeg {Path} is a Tentacle shim (set by its ffmpeg path)")]
    private partial void LogAlreadyShim(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tentacle: Jellyfin keeps its own ffmpeg, so every job runs on this server: {Reason}")]
    private partial void LogNotRedirected(string reason);
}
