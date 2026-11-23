using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tentacle.Protocol;

namespace Tentacle.Broker;

/// <summary>
/// How a kind of job is placed.
/// </summary>
/// <param name="Remote">Whether it may run on a tentacle.</param>
/// <param name="Background">Whether it is background work (lower priority, capped slots).</param>
/// <param name="Cost">Its slot cost.</param>
public sealed record JobPolicy(bool Remote, bool Background, double Cost);

/// <summary>
/// The scheduler's verdict for one job.
/// </summary>
/// <param name="Node">The tentacle to run on, or null for local.</param>
/// <param name="Reason">Why.</param>
/// <param name="Policy">The job's policy.</param>
/// <param name="WouldRunOn">In shadow mode, the tentacle it would have used.</param>
public sealed record PlacementDecision(TentacleNode? Node, string Reason, JobPolicy Policy, string? WouldRunOn = null);

/// <summary>
/// A transcode the host rebuilt (or would have) for a tentacle's GPU of another
/// vendor, waiting for its shim to connect.
/// </summary>
/// <param name="Node">The tentacle it was rebuilt for.</param>
/// <param name="Vendor">That tentacle's GPU vendor.</param>
/// <param name="Device">The device the rebuilt command line opens.</param>
/// <param name="Reason">The placement reason (<c>cross-vendor:amd-vaapi</c>).</param>
/// <param name="Original">The command line the server built.</param>
/// <param name="Rewritten">The command line rebuilt for the tentacle.</param>
/// <param name="Argv">The rebuilt argv the shim must send.</param>
/// <param name="Shadow">Whether it only would have been: the original runs.</param>
/// <param name="Expires">When the note is dropped if no shim came for it.</param>
public sealed record CrossVendorNote(string Node, string Vendor, string Device, string Reason, string Original, string Rewritten, IReadOnlyList<string> Argv, bool Shadow, DateTimeOffset Expires);

/// <summary>
/// Places jobs: each kind has a policy; a job goes to the tentacle with the lowest
/// weighted load among those that are schedulable, proved they share every path
/// the job names, have working hardware if the job uses it, and have room.
/// Anything else, and every doubt, runs locally.
/// </summary>
public static class Scheduler
{
    /// <summary>The node name of this server in decisions, records and metrics.</summary>
    public const string LocalName = "local";

    /// <summary>The node name of a job no node ran (this server does not fall back).</summary>
    public const string RefusedName = "none";

    private static readonly JobPolicy LocalOnly = new(false, false, 0);

    private static long _turn;

    /// <summary>
    /// The policy of a job kind.
    /// Probes and single images are latency-bound (single images have a 10 s timeout
    /// in Jellyfin) and stay local; playback and subtitle extraction are interactive;
    /// trickplay and analysis are background work.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The policy.</returns>
    public static JobPolicy PolicyFor(JobKind kind) => kind switch
    {
        JobKind.Transcode => new JobPolicy(true, false, 1),
        JobKind.Extract => new JobPolicy(true, false, 0.25),
        JobKind.Trickplay => new JobPolicy(true, true, 0.5),
        JobKind.Analysis => new JobPolicy(true, true, 0.5),
        _ => LocalOnly,
    };

    /// <summary>
    /// The next placement turn: nodes remember the turn of their last job, and ties
    /// go to the oldest.
    /// </summary>
    /// <returns>The turn.</returns>
    internal static long NextTurn() => Interlocked.Increment(ref _turn);

    /// <summary>
    /// Whether a job went local only because no tentacle had room right now: none
    /// connected, ready, with the hardware, or with a free slot. Waiting can change
    /// that; it cannot for a directory no tentacle shares or a local stream.
    /// </summary>
    /// <param name="reason">The decision's reason.</param>
    /// <returns>True for a capacity reason.</returns>
    public static bool IsCapacity(string reason)
        => reason is "no-tentacle" or "no-ready-tentacle" or "no-hardware-tentacle" or "all-busy" or "background-slots-full" or "attach-timeout";

    /// <summary>
    /// Places a job.
    /// </summary>
    /// <param name="analysis">The job's argv analysis.</param>
    /// <param name="mode">The placement mode.</param>
    /// <param name="nodes">The connected tentacles.</param>
    /// <param name="local">This server's seat, when it competes for jobs (slots above 0).</param>
    /// <param name="serverVendor">The GPU vendor the server's own hardware command lines are for, when known.</param>
    /// <param name="rebuiltFor">The vendor the host rebuilt this command line for, when it did: a plain VAAPI
    /// command line names no vendor, so the host's word decides.</param>
    /// <returns>The decision.</returns>
    public static PlacementDecision Place(ArgvAnalysis analysis, PlacementMode mode, IReadOnlyCollection<TentacleNode> nodes, LocalSeat? local = null, string? serverVendor = null, string? rebuiltFor = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(nodes);

        var policy = PolicyFor(analysis.Kind);
        var foreign = rebuiltFor is { Length: > 0 } || IsForeignJob(analysis, serverVendor);
        var vendor = rebuiltFor is { Length: > 0 } ? rebuiltFor : analysis.HardwareVendor;
        if (foreign)
        {
            // Rebuilt for another vendor's GPU: this server cannot run it.
            local = null;
        }

        if (mode == PlacementMode.Disabled)
        {
            return new PlacementDecision(null, "disabled", policy);
        }

        if (!policy.Remote)
        {
            return new PlacementDecision(null, "kind-local", policy);
        }

        if (analysis.UsesLoopback)
        {
            return new PlacementDecision(null, "loopback-input", policy);
        }

        if (nodes.Count == 0)
        {
            return new PlacementDecision(null, "no-tentacle", policy);
        }

        var candidates = nodes.Where(n => n.IsSchedulable).ToArray();
        if (candidates.Length == 0)
        {
            return new PlacementDecision(null, "no-ready-tentacle", policy);
        }

        var sharing = candidates.Where(n => n.Covers(analysis.Paths)).ToArray();
        if (sharing.Length == 0)
        {
            var unshared = analysis.Paths.FirstOrDefault(p => !candidates.Any(n => n.Covers([p]))) ?? "?";
            return new PlacementDecision(null, $"path-ineligible:{unshared}", policy);
        }

        if (foreign)
        {
            // Only tentacles of that vendor whose own self-test passed: the server's test
            // (HardwareOk) says nothing about them.
            sharing = sharing.Where(n => n.ForeignHardwareOk && n.Gpu is { } gpu && string.Equals(gpu.Vendor, vendor, StringComparison.Ordinal)).ToArray();
            if (sharing.Length == 0)
            {
                return new PlacementDecision(null, "no-hardware-tentacle", policy);
            }
        }
        else if (analysis.UsesHardware)
        {
            // The self-test proved the server's own hardware type works there; the vendor
            // check is the safety net for command lines that name theirs (h264_qsv, driver=iHD).
            sharing = sharing.Where(n => n.HardwareOk && (analysis.HardwareVendor is null || n.Gpu is null || string.Equals(n.Gpu.Vendor, analysis.HardwareVendor, StringComparison.Ordinal))).ToArray();
            if (sharing.Length == 0)
            {
                return new PlacementDecision(null, "no-hardware-tentacle", policy);
            }
        }

        // Equal scores go to whichever node took a job least recently, so idle or equally
        // loaded nodes take turns instead of the first name winning every tie.
        var best = sharing
            .Where(n => n.CanAccept(policy.Cost, policy.Background))
            .OrderBy(n => Math.Round(n.ScoreWith(policy.Cost), 9))
            .ThenBy(n => n.LastTurn)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        // This server competes as one more node when it has slots, taking its turn on ties.
        if (local?.ScoreWith(policy.Cost, policy.Background) is { } here && (best is null || Wins(here, local.LastTurn, best.ScoreWith(policy.Cost), best.LastTurn)))
        {
            return mode == PlacementMode.DryRun
                ? new PlacementDecision(null, "dry-run", policy, LocalName)
                : new PlacementDecision(null, "least-loaded-local", policy);
        }

        if (best is null)
        {
            return new PlacementDecision(null, policy.Background ? "background-slots-full" : "all-busy", policy);
        }

        return mode == PlacementMode.DryRun
            ? new PlacementDecision(null, "dry-run", policy, best.Name)
            : new PlacementDecision(best, foreign ? CrossVendorReason(best.Gpu!) : "least-loaded", policy);
    }

    /// <summary>
    /// Whether a hardware job's command line is for another vendor's GPU than the
    /// server's: one the host rebuilt for a tentacle (cross-vendor transcoding).
    /// </summary>
    /// <param name="analysis">The job's argv analysis.</param>
    /// <param name="serverVendor">The vendor the server's command lines are for.</param>
    /// <returns>True for a rebuilt command line.</returns>
    public static bool IsForeignJob(ArgvAnalysis analysis, string? serverVendor)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis.UsesHardware
            && analysis.HardwareVendor is { } vendor
            && serverVendor is { Length: > 0 }
            && !string.Equals(vendor, serverVendor, StringComparison.Ordinal);
    }

    /// <summary>
    /// The placement reason of a job rebuilt for a tentacle's GPU: <c>cross-vendor:amd-vaapi</c>.
    /// </summary>
    /// <param name="gpu">The GPU it was rebuilt for.</param>
    /// <returns>The reason.</returns>
    public static string CrossVendorReason(GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        return $"cross-vendor:{gpu.Vendor}-{gpu.Api}";
    }

    /// <summary>
    /// Picks the tentacle with another vendor's GPU that should take a hardware job,
    /// when the host can rebuild its command line for it: one of the vendors the job
    /// could not otherwise use, whose own self-test passed, that shares the job's paths,
    /// has room, and beats where the job would go anyway (its tentacle or this server).
    /// </summary>
    /// <param name="analysis">The analysis of the command line the server built.</param>
    /// <param name="mode">The placement mode.</param>
    /// <param name="nodes">The connected tentacles.</param>
    /// <param name="local">This server's seat, when it competes for jobs.</param>
    /// <param name="serverVendor">The vendor the server's command lines are for.</param>
    /// <param name="canTake">Whether the host can rebuild this job for a tentacle (its codecs, its pipeline).</param>
    /// <returns>The tentacle, or null to leave the job as the server built it.</returns>
    public static TentacleNode? PlaceForeign(ArgvAnalysis analysis, PlacementMode mode, IReadOnlyCollection<TentacleNode> nodes, LocalSeat? local, string? serverVendor, Func<TentacleNode, bool> canTake)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(canTake);
        var policy = PolicyFor(analysis.Kind);
        if (mode == PlacementMode.Disabled || analysis.Kind != JobKind.Transcode || !analysis.UsesHardware || analysis.UsesLoopback || serverVendor is not { Length: > 0 })
        {
            return null;
        }

        var best = nodes
            .Where(n => n.IsSchedulable && n.ForeignHardwareOk && n.Gpu is { } gpu && !string.Equals(gpu.Vendor, serverVendor, StringComparison.Ordinal))
            .Where(n => n.Covers(analysis.Paths) && n.CanAccept(policy.Cost, policy.Background) && canTake(n))
            .OrderBy(n => Math.Round(n.ScoreWith(policy.Cost), 9))
            .ThenBy(n => n.LastTurn)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        // Where the job would go as built: in shadow mode a decision names no node, so
        // compare with an active placement.
        var regular = Place(analysis, PlacementMode.Active, nodes, local, serverVendor);
        if (regular.Node is { } other)
        {
            return Wins(best.ScoreWith(policy.Cost), best.LastTurn, other.ScoreWith(policy.Cost), other.LastTurn) ? best : null;
        }

        // Otherwise it runs here: chosen on load, or for want of a tentacle with the
        // server's GPU. When this server competes for jobs it keeps them on load and
        // ties; when it is only the backup, the foreign GPU takes it.
        if (local?.ScoreWith(policy.Cost, policy.Background) is { } here)
        {
            return Wins(best.ScoreWith(policy.Cost), best.LastTurn, here, local.LastTurn) ? best : null;
        }

        return best;
    }

    private static bool Wins(double here, long hereTurn, double best, long bestTurn)
    {
        const double Epsilon = 1e-9;
        return here < best - Epsilon || (here <= best + Epsilon && hereTurn < bestTurn);
    }
}
