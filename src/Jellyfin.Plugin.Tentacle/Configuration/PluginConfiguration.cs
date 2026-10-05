using System;
using MediaBrowser.Model.Plugins;
using Tentacle.Broker;
using Tentacle.Protocol;

namespace Jellyfin.Plugin.Tentacle.Configuration;

/// <summary>
/// How the agent port is secured.
/// </summary>
public enum TlsMode
{
    /// <summary>Plain WebSockets (ws://): the token crosses the network in cleartext.</summary>
    Off,

    /// <summary>TLS with a self-signed certificate kept in the data directory; agents pin its fingerprint.</summary>
    SelfSigned,

    /// <summary>TLS with a certificate and key from files (cert-manager, a CA); reloaded when they change.</summary>
    Files,
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the placement mode. A first deploy starts in shadow mode.
    /// </summary>
    public PlacementMode Placement { get; set; } = PlacementMode.DryRun;

    /// <summary>
    /// Gets or sets the TCP port the broker listens on for agents.
    /// </summary>
    public int AgentPort { get; set; } = ProtocolInfo.DefaultAgentPort;

    /// <summary>
    /// Gets or sets the unix socket the shims connect to.
    /// </summary>
    public string SocketPath { get; set; } = ProtocolInfo.DefaultSocketPath;

    /// <summary>
    /// Gets or sets the token agents authenticate with. Generated on first start;
    /// the TENTACLE_TOKEN environment variable overrides it.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the token before the last rotation, accepted until
    /// <see cref="PreviousTokenExpires"/>.
    /// </summary>
    public string PreviousToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the previous token stops working (UTC).
    /// </summary>
    public DateTime? PreviousTokenExpires { get; set; }

    /// <summary>
    /// Gets or sets how the agent port is secured. Needs a restart. The
    /// TENTACLE_TLS_CERT (and TENTACLE_TLS_KEY) environment variables select Files.
    /// </summary>
    public TlsMode Tls { get; set; } = TlsMode.SelfSigned;

    /// <summary>
    /// Gets or sets the certificate file for <see cref="TlsMode.Files"/>: PEM (chain,
    /// optionally with the key) or PKCS#12.
    /// </summary>
    public string TlsCertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PEM key file for <see cref="TlsMode.Files"/>, when the key is
    /// not in the certificate file.
    /// </summary>
    public string TlsKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets directories shared with every tentacle at the same path, on top of
    /// the ones Tentacle derives (transcodes, temp, subtitles, attachments, libraries).
    /// </summary>
    public string[] ExtraRoots { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a tentacle whose ffmpeg differs from the
    /// server's only in the patch version is refused (by default it is only degraded).
    /// </summary>
    public bool StrictFfmpegVersion { get; set; }

    /// <summary>
    /// Gets or sets how often tentacles prove their shares and hardware again, in minutes.
    /// </summary>
    public int VerifyIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the nice value background jobs (trickplay, analysis) run with on
    /// tentacles, 0 (normal) to 19 (lowest). Jellyfin's own process priority for these
    /// jobs only reaches the local shim, so the tentacle applies this instead.
    /// </summary>
    public int BackgroundNice { get; set; } = 10;

    /// <summary>
    /// Gets or sets the job slots this server offers, competing with the tentacles by
    /// load (a transcode costs 1). 0: it only runs what no tentacle can take.
    /// </summary>
    public int LocalSlots { get; set; }

    /// <summary>
    /// Gets or sets this server's scheduling weight: 2 takes twice the load of a
    /// tentacle with weight 1 before it stops winning placements.
    /// </summary>
    public double LocalWeight { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether this server runs a job no tentacle can
    /// take right now. Off, it waits up to 10 s for one and then fails.
    /// </summary>
    public bool LocalFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether tentacle_running_job_start_time_seconds
    /// names the user, app and item of each job. Off by default: anyone who can read
    /// /metrics would see who watches what.
    /// </summary>
    public bool MetricsNameViewers { get; set; }
}
