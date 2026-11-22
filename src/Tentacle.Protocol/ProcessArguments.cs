using System;
using System.Collections.Generic;
using System.Text;

namespace Tentacle.Protocol;

/// <summary>
/// How .NET turns a <c>ProcessStartInfo.Arguments</c> string into the argv a process
/// receives on Unix (Jellyfin starts ffmpeg, and so the shim, that way). The same rules
/// as .NET's own parser, so the broker can compare the argv a shim sends with a command
/// line the plugin built as a string.
/// </summary>
public static class ProcessArguments
{
    /// <summary>
    /// Splits an arguments string: spaces and tabs separate arguments outside double
    /// quotes; 2N backslashes before a quote are N backslashes and the quote toggles
    /// quoting, 2N+1 are N backslashes and a literal quote; other backslashes are
    /// literal; two quotes inside quotes are one literal quote.
    /// </summary>
    /// <param name="arguments">The arguments string.</param>
    /// <returns>The argv, without the program.</returns>
    public static IReadOnlyList<string> Split(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var results = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            while (i < arguments.Length && (arguments[i] == ' ' || arguments[i] == '\t'))
            {
                i++;
            }

            if (i == arguments.Length)
            {
                break;
            }

            results.Add(Next(arguments, ref i));
        }

        return results;
    }

    private static string Next(string arguments, ref int i)
    {
        var current = new StringBuilder();
        var quoted = false;
        while (i < arguments.Length)
        {
            var backslashes = 0;
            while (i < arguments.Length && arguments[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (backslashes > 0)
            {
                if (i >= arguments.Length || arguments[i] != '"')
                {
                    current.Append('\\', backslashes);
                }
                else
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 != 0)
                    {
                        current.Append('"');
                        i++;
                    }
                }

                continue;
            }

            var c = arguments[i];
            if (c == '"')
            {
                if (quoted && i < arguments.Length - 1 && arguments[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                i++;
                continue;
            }

            if ((c == ' ' || c == '\t') && !quoted)
            {
                break;
            }

            current.Append(c);
            i++;
        }

        return current.ToString();
    }
}
