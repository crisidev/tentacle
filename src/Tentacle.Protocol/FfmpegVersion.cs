using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tentacle.Protocol;

/// <summary>
/// How two ffmpeg builds compare.
/// </summary>
public enum FfmpegMatch
{
    /// <summary>The same version line.</summary>
    Same,

    /// <summary>Same major.minor, different patch or build suffix: arguments are compatible.</summary>
    PatchDiffers,

    /// <summary>Different major.minor: Jellyfin builds arguments for the server's ffmpeg, they may not work.</summary>
    Incompatible,

    /// <summary>One side could not be parsed.</summary>
    Unknown,
}

/// <summary>
/// Compares `ffmpeg -version` first lines ("ffmpeg version 8.1.3-Jellyfin Copyright ...").
/// </summary>
public static partial class FfmpegVersion
{
    /// <summary>
    /// Parses the version out of a `-version` first line.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The version, or null.</returns>
    public static Version? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var m = VersionPattern().Match(line);
        if (!m.Success)
        {
            return null;
        }

        var patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), patch);
    }

    /// <summary>
    /// Compares a node's ffmpeg with the server's.
    /// </summary>
    /// <param name="server">The server's line.</param>
    /// <param name="node">The node's line.</param>
    /// <returns>How they compare.</returns>
    public static FfmpegMatch Compare(string? server, string? node)
    {
        if (!string.IsNullOrEmpty(server) && string.Equals(server, node, StringComparison.Ordinal))
        {
            return FfmpegMatch.Same;
        }

        var a = Parse(server);
        var b = Parse(node);
        if (a is null || b is null)
        {
            return FfmpegMatch.Unknown;
        }

        return a.Major == b.Major && a.Minor == b.Minor ? FfmpegMatch.PatchDiffers : FfmpegMatch.Incompatible;
    }

    [GeneratedRegex(@"version\s+n?(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
