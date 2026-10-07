using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tentacle.Broker;
using Tentacle.Protocol;

namespace Jellyfin.Plugin.Tentacle.Api;

/// <summary>
/// Admin API behind the dashboard page: tentacles, jobs, broker status.
/// </summary>
[ApiController]
[Route("Tentacle")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TentacleController : ControllerBase
{
    private readonly TentacleRuntime _runtime;
    private readonly ShimInstaller _shim;

    /// <summary>
    /// Initializes a new instance of the <see cref="TentacleController"/> class.
    /// </summary>
    /// <param name="runtime">The runtime.</param>
    /// <param name="shim">The shim in front of Jellyfin's ffmpeg.</param>
    public TentacleController(TentacleRuntime runtime, ShimInstaller shim)
    {
        _runtime = runtime;
        _shim = shim;
    }

    /// <summary>
    /// Gets the broker status.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<BrokerStatus> GetStatus()
    {
        var options = _runtime.Options;
        var certificate = _runtime.Certificate;
        return new BrokerStatus(
            ProtocolInfo.ProductVersion,
            options.Placement.ToString(),
            options.SocketPath,
            options.AgentPort,
            _runtime.CertificateError ?? _runtime.Host.StartError,
            _shim.ShimPath,
            _shim.RealPath,
            _shim.Error,
            options.ServerFfmpegVersion(),
            _runtime.SharedRoots(),
            _runtime.MetricsEnabled,
            certificate is not null,
            certificate?.Fingerprint,
            certificate?.SelfSigned ?? false,
            certificate?.Current.NotAfter.ToUniversalTime(),
            TentacleRuntime.TokenFromEnvironment,
            options.PreviousToken.Length > 0 ? options.PreviousTokenExpires ?? DateTimeOffset.MaxValue : null,
            _runtime.Broker.Local.Slots,
            _runtime.Broker.Local.Weight,
            Math.Round(_runtime.Broker.Local.Load, 2),
            options.LocalFallback,
            _runtime.Broker.Local.BackgroundSlots,
            Math.Round(_runtime.Broker.Local.BackgroundLoad, 2),
            new ServerMachine(options.ServerName, HostInfo.Arch(), HostInfo.CpuModel(), Environment.ProcessorCount, HostInfo.Kernel(), options.HardwareTest()?.Type ?? string.Empty, _runtime.ServerGpu(), _runtime.ServerGpusProbed));
    }

    /// <summary>
    /// Replaces the agent token, keeping the old one valid for a while.
    /// </summary>
    /// <param name="overlapMinutes">How long the old token keeps working (default a day).</param>
    /// <returns>The new token, or a conflict when TENTACLE_TOKEN pins it.</returns>
    [HttpPost("Token/Rotate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<TokenRotation> RotateToken([FromQuery] double overlapMinutes = 24 * 60)
    {
        var overlap = TimeSpan.FromMinutes(Math.Clamp(overlapMinutes, 0, 30 * 24 * 60));
        return _runtime.RotateToken(overlap) is { } token
            ? new TokenRotation(token, DateTimeOffset.UtcNow + overlap)
            : Conflict("the token comes from TENTACLE_TOKEN: rotate it there (TENTACLE_PREVIOUS_TOKEN keeps the old one valid)");
    }

    /// <summary>
    /// Gets the connected tentacles.
    /// </summary>
    /// <returns>The tentacles.</returns>
    [HttpGet("Nodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NodeSnapshot>> GetNodes() => Ok(_runtime.Broker.Nodes.Snapshot());

    /// <summary>
    /// Makes a tentacle prove its shares and hardware again, within a heartbeat.
    /// </summary>
    /// <param name="name">The tentacle.</param>
    /// <returns>No content, or not found.</returns>
    [HttpPost("Nodes/{name}/Verify")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult VerifyNode([FromRoute] string name) => _runtime.Broker.RequestVerify(name) ? NoContent() : NotFound();

    /// <summary>
    /// Gets the recent jobs, newest first.
    /// </summary>
    /// <param name="limit">How many.</param>
    /// <returns>The jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<JobRecord>> GetJobs([FromQuery] int limit = 100) => Ok(_runtime.Broker.History.Snapshot(limit));
}

/// <summary>
/// The broker as the dashboard shows it.
/// </summary>
/// <param name="Version">The Tentacle version.</param>
/// <param name="Placement">The placement mode.</param>
/// <param name="SocketPath">The shim socket.</param>
/// <param name="AgentPort">The agent port.</param>
/// <param name="StartError">Why the broker is not listening, if it is not.</param>
/// <param name="ShimPath">The ffmpeg Jellyfin runs, when it is the shim.</param>
/// <param name="RealFfmpeg">The real ffmpeg behind the shim.</param>
/// <param name="ShimError">Why Jellyfin runs its own ffmpeg instead of the shim, if it does.</param>
/// <param name="ServerFfmpegVersion">The server's ffmpeg version line.</param>
/// <param name="SharedRoots">The directories ffmpeg uses, which tentacles must share: path, whether ffmpeg writes there, whether a tentacle needs it to take jobs at all.</param>
/// <param name="MetricsEnabled">Whether Jellyfin serves /metrics (where the Tentacle metrics are).</param>
/// <param name="Tls">Whether the agent port speaks TLS (wss://).</param>
/// <param name="TlsFingerprint">The certificate's SHA-256 fingerprint, for TENTACLE_BROKER_FINGERPRINT.</param>
/// <param name="TlsSelfSigned">Whether the certificate is the generated self-signed one.</param>
/// <param name="TlsNotAfter">When the certificate expires.</param>
/// <param name="TokenFromEnvironment">Whether TENTACLE_TOKEN pins the token.</param>
/// <param name="PreviousTokenExpires">When the token before the last rotation stops working, while it does.</param>
/// <param name="LocalSlots">Slots this server offers in placement (0: only what no tentacle can take).</param>
/// <param name="LocalWeight">This server's scheduling weight.</param>
/// <param name="LocalLoad">Cost of the jobs running here that could have gone to a tentacle.</param>
/// <param name="LocalFallback">Whether this server runs jobs no tentacle can take right now.</param>
/// <param name="LocalBackgroundSlots">Slots background jobs may use here.</param>
/// <param name="LocalBackgroundLoad">Cost of the background jobs running here.</param>
/// <param name="Server">This server as a machine, for its card next to the tentacles.</param>
public sealed record BrokerStatus(
    string Version,
    string Placement,
    string SocketPath,
    int AgentPort,
    string? StartError,
    string? ShimPath,
    string? RealFfmpeg,
    string? ShimError,
    string ServerFfmpegVersion,
    IReadOnlyList<SharedRoot> SharedRoots,
    bool MetricsEnabled,
    bool Tls,
    string? TlsFingerprint,
    bool TlsSelfSigned,
    DateTime? TlsNotAfter,
    bool TokenFromEnvironment,
    DateTimeOffset? PreviousTokenExpires,
    int LocalSlots,
    double LocalWeight,
    double LocalLoad,
    bool LocalFallback,
    int LocalBackgroundSlots,
    double LocalBackgroundLoad,
    ServerMachine Server);

/// <summary>
/// The Jellyfin server as a machine.
/// </summary>
/// <param name="Name">TENTACLE_NODE_NAME, else the hostname.</param>
/// <param name="Arch">The CPU architecture.</param>
/// <param name="CpuModel">The CPU model.</param>
/// <param name="Cores">CPUs.</param>
/// <param name="Kernel">The kernel release.</param>
/// <param name="Hardware">Jellyfin's hardware acceleration (qsv, vaapi, nvenc), or empty for software.</param>
/// <param name="Gpu">The GPU that uses, measured like a tentacle's, or null.</param>
/// <param name="GpuProbed">Whether the measurement is done (it runs once, shortly after start).</param>
public sealed record ServerMachine(string Name, string Arch, string CpuModel, int Cores, string Kernel, string Hardware, GpuInfo? Gpu, bool GpuProbed);

/// <summary>
/// A rotated token.
/// </summary>
/// <param name="Token">The new token.</param>
/// <param name="PreviousTokenExpires">When the old one stops working.</param>
public sealed record TokenRotation(string Token, DateTimeOffset PreviousTokenExpires);
