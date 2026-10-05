using System.Reflection;

namespace Tentacle.Protocol;

/// <summary>
/// Version information shared by the broker, the shim and the agent.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>
    /// The wire protocol version. Bumped on any incompatible change; JSON fields are only ever added.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The default TCP port the broker listens on for agents.
    /// </summary>
    public const int DefaultAgentPort = 8097;

    /// <summary>
    /// The default path of the broker's unix socket for shims.
    /// </summary>
    public const string DefaultSocketPath = "/run/tentacle/broker.sock";

    /// <summary>
    /// Gets the product version, identical for the plugin, the shim and the agent of one build.
    /// </summary>
    public static string ProductVersion { get; } =
        typeof(ProtocolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
