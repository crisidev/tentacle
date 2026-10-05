using System;
using System.IO;
using System.Text;

namespace Tentacle.Cli;

/// <summary>
/// What the binary was invoked as.
/// </summary>
internal enum ToolMode
{
    /// <summary>
    /// `tentacle &lt;command&gt;`.
    /// </summary>
    Tentacle,

    /// <summary>
    /// The ffmpeg shim (Jellyfin's EncoderPath).
    /// </summary>
    Ffmpeg,

    /// <summary>
    /// The ffprobe shim. Jellyfin derives it as a sibling of the ffmpeg path.
    /// </summary>
    Ffprobe
}

/// <summary>
/// Resolves the invocation mode from argv[0].
/// </summary>
internal static class Invocation
{
    /// <summary>
    /// Resolves the mode from an argv[0] value. Jellyfin replaces the last path
    /// component of the ffmpeg path with "ffprobe" (keeping any extension), so match on prefixes.
    /// </summary>
    /// <param name="argv0">The raw argv[0].</param>
    /// <returns>The mode.</returns>
    public static ToolMode Resolve(string argv0)
    {
        var name = Path.GetFileName(argv0);
        if (name.StartsWith("ffprobe", StringComparison.Ordinal))
        {
            return ToolMode.Ffprobe;
        }

        if (name.StartsWith("ffmpeg", StringComparison.Ordinal))
        {
            return ToolMode.Ffmpeg;
        }

        return ToolMode.Tentacle;
    }

    /// <summary>
    /// Reads argv[0] as the kernel saw it. NativeAOT resolves symlinks for the
    /// process path, so the ffmpeg/ffprobe symlink name is only visible here.
    /// </summary>
    /// <returns>argv[0], or an empty string when /proc is unavailable.</returns>
    public static string ReadArgv0()
    {
        try
        {
            var cmdline = File.ReadAllBytes("/proc/self/cmdline");
            var end = Array.IndexOf(cmdline, (byte)0);
            return Encoding.UTF8.GetString(cmdline, 0, end < 0 ? cmdline.Length : end);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
