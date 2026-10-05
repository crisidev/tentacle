using System;
using System.Collections.Generic;
using System.Linq;

namespace Tentacle.Protocol;

/// <summary>
/// The environment a job may carry to a tentacle: locale, timezone, temp dir and
/// GPU driver *selection*. Exact names only: no prefix families, so nothing that
/// names a file or a search path (LD_PRELOAD, PATH, LIBVA_DRIVERS_PATH,
/// OCL_ICD_FILENAMES, FONTCONFIG_FILE, ...) can make ffmpeg load code. Driver
/// search paths come from the tentacle's own image. Values are checked too: a
/// driver name is a bare word, since libva puts it in a file name
/// (<c>&lt;dir&gt;/&lt;name&gt;_drv_video.so</c>) and "../" would load any library.
/// Enforced by the shim (collect), the broker (forward) and the agent (apply).
/// </summary>
public static class EnvPolicy
{
    private const int MaxValueLength = 256;

    private static readonly string[] Names =
    [
        "TZ", "LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE", "LC_MESSAGES", "LC_NUMERIC", "LC_TIME",
        "TMPDIR", "LIBVA_DRIVER_NAME", "LIBVA_DRIVER_NAME_JELLYFIN", "NEOReadDebugKeys", "AMD_DEBUG",
    ];

    // Variables whose value names a driver: a bare word, nothing path-like.
    private static readonly string[] DriverNames = ["LIBVA_DRIVER_NAME", "LIBVA_DRIVER_NAME_JELLYFIN"];

    /// <summary>
    /// Whether a variable may be forwarded, by name.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <returns>True if allowed.</returns>
    public static bool IsAllowed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Names.Contains(name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a variable may be forwarded with this value.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>True if allowed.</returns>
    public static bool IsAllowed(string name, string? value)
    {
        if (!IsAllowed(name) || value is null || value.Length > MaxValueLength || value.Any(char.IsControl))
        {
            return false;
        }

        return !DriverNames.Contains(name, StringComparer.Ordinal) || (value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
    }

    /// <summary>
    /// The allowed subset of an environment.
    /// </summary>
    /// <param name="env">The environment.</param>
    /// <returns>A filtered copy.</returns>
    public static Dictionary<string, string> Filter(IEnumerable<KeyValuePair<string, string>> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return env.Where(kv => IsAllowed(kv.Key, kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}
