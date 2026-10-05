using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tentacle.Protocol;

namespace Tentacle.Broker;

/// <summary>
/// How the broker places jobs.
/// </summary>
public enum PlacementMode
{
    /// <summary>
    /// Every job runs locally and nothing is recorded.
    /// </summary>
    Disabled,

    /// <summary>
    /// Shadow mode: record where each job would have run, but run everything locally.
    /// </summary>
    DryRun,

    /// <summary>
    /// Jobs run on tentacles according to the scheduler.
    /// </summary>
    Active,
}

/// <summary>
/// Which token an agent authenticated with.
/// </summary>
public enum TokenMatch
{
    /// <summary>Not a valid token.</summary>
    None,

    /// <summary>The current token.</summary>
    Current,

    /// <summary>The previous token, inside its overlap window.</summary>
    Previous,
}

/// <summary>
/// A directory the server expects every tentacle to see at the same path.
/// </summary>
/// <param name="Path">The directory.</param>
/// <param name="Writable">Whether ffmpeg writes there (proven with a round-trip); otherwise it is only read (proven with a stat).</param>
/// <param name="Required">Whether a tentacle missing it is degraded. Optional roots (concat, server-only
/// libraries, fonts) only narrow which jobs a tentacle can take.</param>
public sealed record SharedRoot(string Path, bool Writable, bool Required = false);

/// <summary>
/// The hardware self-test every tentacle runs, built from the server's encoding settings.
/// </summary>
/// <param name="Type">The hardware acceleration type (qsv, vaapi, nvenc...).</param>
/// <param name="Args">The ffmpeg arguments.</param>
/// <param name="Vendor">The GPU vendor the server's command lines are for (intel, amd, nvidia), when known.</param>
public sealed record HardwareTest(string Type, string[] Args, string? Vendor = null);

/// <summary>
/// What the host knows about a job: who it is for and what it works on.
/// </summary>
/// <param name="User">The Jellyfin user (playback only).</param>
/// <param name="Client">The client app and device.</param>
/// <param name="Item">A display name of the item.</param>
/// <param name="ItemId">The item id.</param>
/// <param name="PlaySessionId">The play session id (playback only).</param>
public sealed record JobAttribution(string? User, string? Client, string? Item, string? ItemId, string? PlaySessionId);

/// <summary>
/// Broker settings. Listen addresses are read once at start; the rest is read live.
/// </summary>
public sealed class BrokerOptions
{
    /// <summary>
    /// Gets or sets the unix socket the shims connect to.
    /// </summary>
    public string SocketPath { get; set; } = ProtocolInfo.DefaultSocketPath;

    /// <summary>
    /// Gets or sets the TCP port the agents connect to.
    /// </summary>
    public int AgentPort { get; set; } = ProtocolInfo.DefaultAgentPort;

    /// <summary>
    /// Gets or sets the shared token agents authenticate with. Read live: changing it
    /// disconnects agents still using the old one (after the previous token's overlap).
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token before the last rotation, still accepted until
    /// <see cref="PreviousTokenExpires"/> so agents can move over one by one.
    /// </summary>
    public string PreviousToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the previous token stops working; null = while it is set.
    /// </summary>
    public DateTimeOffset? PreviousTokenExpires { get; set; }

    /// <summary>
    /// Gets or sets the certificate for the agent port: null = plain WebSockets.
    /// Read on every TLS handshake, so a renewed certificate applies to new connections.
    /// </summary>
    public Func<X509Certificate2?> Certificate { get; set; } = () => null;

    /// <summary>
    /// Gets or sets the placement mode.
    /// </summary>
    public PlacementMode Placement { get; set; } = PlacementMode.DryRun;

    /// <summary>
    /// Gets or sets the ping interval for agents.
    /// </summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets how long an assigned agent has to attach the job socket.
    /// </summary>
    public TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets how often a tentacle's shares and hardware are proven again
    /// (catches a share that went away under a running agent).
    /// </summary>
    public TimeSpan VerifyInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets or sets a value indicating whether a different ffmpeg patch version makes a tentacle incompatible (not just degraded).
    /// </summary>
    public bool StrictFfmpegVersion { get; set; }

    /// <summary>
    /// Gets or sets the nice value of background jobs (trickplay, analysis) on tentacles.
    /// </summary>
    public int BackgroundNice { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether running jobs' metrics carry the user, app and item.
    /// </summary>
    public bool MetricsNameViewers { get; set; }

    /// <summary>
    /// Gets or sets the job slots this server offers in placement, competing with the
    /// tentacles by load. 0: it only runs what no tentacle can take.
    /// </summary>
    public int LocalSlots { get; set; }

    /// <summary>
    /// Gets or sets this server's scheduling weight (a tentacle's is TENTACLE_WEIGHT, default 1).
    /// </summary>
    public double LocalWeight { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether this server runs a job no tentacle can
    /// take right now (all busy, none connected). Off, such a job waits up to
    /// <see cref="NoFallbackWait"/> for a tentacle and is then refused. Jobs that only
    /// this server can run (short kinds, a directory no tentacle shares, a local
    /// stream) run here either way.
    /// </summary>
    public bool LocalFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets how long a job waits for a tentacle when <see cref="LocalFallback"/> is off.
    /// </summary>
    public TimeSpan NoFallbackWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets this server's name on the dashboard: TENTACLE_NODE_NAME, else the hostname.
    /// </summary>
    public string ServerName { get; set; } = HostInfo.Name();

    /// <summary>
    /// Gets or sets the server's ffmpeg version line. Read when needed: Jellyfin
    /// resolves its ffmpeg path after the plugin starts.
    /// </summary>
    public Func<string> ServerFfmpegVersion { get; set; } = () => string.Empty;

    /// <summary>
    /// Gets or sets the uid the server (and so Jellyfin's local ffmpeg) runs as.
    /// </summary>
    public int ServerUid { get; set; } = -1;

    /// <summary>
    /// Gets or sets the directories every tentacle should share with the server.
    /// A job runs on a tentacle only if every path it names is under a root that
    /// tentacle proved it shares.
    /// </summary>
    public Func<IReadOnlyList<SharedRoot>> SharedRoots { get; set; } = () => [];

    /// <summary>
    /// Gets or sets the hardware self-test, or null when the server encodes in software.
    /// </summary>
    public Func<HardwareTest?> HardwareTest { get; set; } = () => null;

    /// <summary>
    /// Gets or sets how to find out who and what a job is for (the host's sessions
    /// and library); null when it does not know yet. Called when the job starts and
    /// retried briefly while it runs.
    /// </summary>
    public Func<ArgvAnalysis, JobAttribution?> Attribute { get; set; } = _ => null;

    /// <summary>
    /// Which configured token a presented one is, compared in constant time.
    /// </summary>
    /// <param name="presented">The token an agent presented.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The match.</returns>
    public TokenMatch Match(string? presented, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return TokenMatch.None;
        }

        if (Token.Length > 0 && FixedEquals(presented, Token))
        {
            return TokenMatch.Current;
        }

        var previousValid = PreviousToken.Length > 0 && (PreviousTokenExpires is not { } until || now < until);
        return previousValid && FixedEquals(presented, PreviousToken) ? TokenMatch.Previous : TokenMatch.None;
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
