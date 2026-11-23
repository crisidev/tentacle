using System;
using System.Collections.Generic;
using System.Reflection;

namespace Jellyfin.Plugin.Tentacle.Tests;

/// <summary>
/// Stand-ins for Jellyfin's services: members without a handler return a neutral
/// value (false, "", 0, null).
/// </summary>
public static class Fakes
{
    public static T Create<T>(params (string Member, Func<object?[]?, object?> Handler)[] handlers)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, FakeProxy>();
        var fake = (FakeProxy)(object)proxy;
        foreach (var (member, handler) in handlers)
        {
            fake.Handlers[member] = handler;
        }

        return proxy;
    }
}

/// <summary>
/// The proxy behind <see cref="Fakes.Create{T}"/>.
/// </summary>
public class FakeProxy : DispatchProxy
{
    internal Dictionary<string, Func<object?[]?, object?>> Handlers { get; } = new(StringComparer.Ordinal);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        if (Handlers.TryGetValue(method.Name, out var handler))
        {
            return handler(args);
        }

        var type = method.ReturnType;
        if (type == typeof(void))
        {
            return null;
        }

        if (type == typeof(string))
        {
            return string.Empty;
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
