using System;
using System.Collections.Generic;
using System.IO;

namespace Tentacle.Protocol;

/// <summary>
/// A message that checks what JSON nullability cannot: no null array elements or
/// dictionary values. <see cref="Frame.Read{T}"/> calls it, so a decoded message
/// is safe to use as typed.
/// </summary>
public interface IValidated
{
    /// <summary>
    /// Throws <see cref="InvalidDataException"/> when the message is unusable.
    /// </summary>
    void Validate();
}

/// <summary>
/// Validation helpers.
/// </summary>
internal static class Validation
{
    public static void NoNulls<T>(T[] items, string name)
        where T : class
    {
        if (Array.IndexOf(items, null) >= 0)
        {
            throw new InvalidDataException($"null in {name}");
        }
    }

    public static void NoNulls(Dictionary<string, string> items, string name)
    {
        if (items.ContainsValue(null!))
        {
            throw new InvalidDataException($"null in {name}");
        }
    }
}
