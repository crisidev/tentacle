using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Tentacle.Broker;

/// <content>
/// Cross-vendor trickplay. Jellyfin builds and starts a trickplay command line inside its
/// media encoder, with a fresh output directory each run, so the host cannot hand it a
/// rebuilt one the way it does for transcodes. It predicts instead what Jellyfin will
/// start (<see cref="NoteTrickplay"/>), and when a shim brings exactly that, token for
/// token apart from the output directory's name, the broker runs the rebuilt one instead.
/// </content>
public sealed partial class Broker
{
    /// <summary>
    /// The output directory name a trickplay prediction uses for the GUID Jellyfin picks.
    /// </summary>
    public const string TrickplayOutputPlaceholder = "00000000000000000000000000000000";

    // Jellyfin may wait for its image extraction lock, and retries a key-frame-only
    // extraction without it after a failure: predictions live long enough for both.
    private static readonly TimeSpan TrickplayNoteLife = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, TrickplayNote> _trickplayNotes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, TrickplayPlan> _trickplayPlans = new();
    private long _trickplayPlanCounter;

    /// <summary>
    /// Records what the host predicts Jellyfin will start for one trickplay extraction, and
    /// the command lines rebuilt for a tentacle's GPU to run instead (or, in shadow mode, to
    /// show). Each attempt is the server's argv and the rebuilt argv, both ending in an
    /// output <c>&lt;outputRoot&gt;/<see cref="TrickplayOutputPlaceholder"/>/%08d.jpg</c>.
    /// </summary>
    /// <param name="node">The tentacle they were rebuilt for.</param>
    /// <param name="attempts">The command lines Jellyfin may start, in its order, each with its rebuild.</param>
    /// <param name="outputRoot">The directory Jellyfin makes each run's output directory in (its temp directory).</param>
    /// <param name="shadow">Whether they only would have run there (shadow mode).</param>
    public void NoteTrickplay(TentacleNode node, IReadOnlyList<TrickplayAttempt> attempts, string outputRoot, bool shadow)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentException.ThrowIfNullOrEmpty(outputRoot);
        if (CrossVendorMode == PlacementMode.Disabled)
        {
            return;
        }

        var output = TrickplayOutput(outputRoot, TrickplayOutputPlaceholder);
        if (attempts.Count == 0 || attempts.Any(a => a.Server.Count == 0 || a.Rebuilt.Count == 0 || a.Server[^1] != output || a.Rebuilt[^1] != output))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SweepTrickplay(now);
        var plan = Interlocked.Increment(ref _trickplayPlanCounter);
        _trickplayPlans[plan] = new TrickplayPlan(node.Name, now + TrickplayNoteLife);
        var gpu = node.Gpu;
        foreach (var attempt in attempts)
        {
            _trickplayNotes[Key(attempt.Server)] = new TrickplayNote(
                plan,
                node.Name,
                gpu?.Vendor ?? string.Empty,
                gpu?.Device ?? string.Empty,
                gpu is null ? "cross-vendor" : Scheduler.CrossVendorReason(gpu),
                attempt.Server.ToArray(),
                attempt.Rebuilt.ToArray(),
                shadow,
                now + TrickplayNoteLife);
        }
    }

    /// <summary>
    /// The rebuild for a trickplay command line a shim brought, if the host predicted exactly
    /// it: every token the same, and the output a fresh directory right below the predicted
    /// root. Its argv is the rebuilt one with that same output. Null otherwise, and when the
    /// tentacle it was rebuilt for cannot take it now (the job then runs as Jellyfin built it).
    /// </summary>
    /// <param name="args">The shim's argv.</param>
    /// <returns>The note, or null.</returns>
    public CrossVendorNote? MatchTrickplay(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || _trickplayNotes.IsEmpty || TrickplayRun().Match(args[^1]) is not { Success: true } run)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        SweepTrickplay(now);
        var root = run.Groups["root"].Value;
        var predicted = args.Take(args.Count - 1).Append(TrickplayOutput(root, TrickplayOutputPlaceholder)).ToArray();
        if (!_trickplayNotes.TryRemove(Key(predicted), out var note) || note.Expires < now)
        {
            return null;
        }

        if (_trickplayPlans.TryGetValue(note.Plan, out var plan))
        {
            plan.Matched = true;
        }

        var output = args[^1];
        var rebuilt = note.Rebuilt.Take(note.Rebuilt.Count - 1).Append(output).ToArray();
        var shadow = note.Shadow || CrossVendorMode != PlacementMode.Active;
        if (!shadow && Nodes.Find(note.Node) is not { IsSchedulable: true, ForeignHardwareOk: true })
        {
            // Placed when Jellyfin asked for it; gone or unproven by the time it starts.
            // Unlike a transcode, the server's own command line is still at hand: run that.
            LogTrickplayUnavailable(note.Node);
            Metrics.CrossVendor(note.Node, "skipped:unavailable", "Trickplay");
            return null;
        }

        Metrics.CrossVendor(note.Node, shadow ? "shadow" : "rebuilt", "Trickplay");
        return new CrossVendorNote(note.Node, note.Vendor, note.Device, note.Reason, Display(args), Display(rebuilt), rebuilt, shadow, note.Expires, "Trickplay");
    }

    private static string TrickplayOutput(string root, string run) => root.TrimEnd('/') + "/" + run + "/%08d.jpg";

    private static string Key(IEnumerable<string> argv) => string.Join('\0', argv);

    // For the job's details: ffmpeg's arguments, quoted where they need it.
    private static string Display(IEnumerable<string> argv)
        => "ffmpeg " + string.Join(' ', argv.Select(a => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c == '"') ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : a));

    private void SweepTrickplay(DateTimeOffset now)
    {
        foreach (var stale in _trickplayNotes.Where(n => n.Value.Expires < now).Select(n => n.Key).ToArray())
        {
            _trickplayNotes.TryRemove(stale, out _);
        }

        foreach (var (id, plan) in _trickplayPlans.Where(p => p.Value.Expires < now).ToArray())
        {
            if (_trickplayPlans.TryRemove(id, out _) && !plan.Matched)
            {
                // Predicted, and no shim brought it: Jellyfin no longer builds trickplay
                // the way the host reproduces it (or never started the job).
                LogTrickplayNoMatch(plan.Node);
                Metrics.CrossVendor(plan.Node, "skipped:no-match", "Trickplay");
            }
        }
    }

    // A run's output: Jellyfin's temp directory, a fresh GUID ("N" format), image numbers.
    [GeneratedRegex(@"^(?<root>/[^\0]*[^/])/[0-9a-f]{32}/%08d\.jpg$")]
    private static partial Regex TrickplayRun();

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle {Node} can no longer take the trickplay job rebuilt for it: it runs as Jellyfin built it")]
    private partial void LogTrickplayUnavailable(string node);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A trickplay job predicted for tentacle {Node} never started as predicted: not rebuilt")]
    private partial void LogTrickplayNoMatch(string node);

    private sealed record TrickplayNote(long Plan, string Node, string Vendor, string Device, string Reason, IReadOnlyList<string> Server, IReadOnlyList<string> Rebuilt, bool Shadow, DateTimeOffset Expires);

    private sealed class TrickplayPlan(string node, DateTimeOffset expires)
    {
        public string Node { get; } = node;

        public DateTimeOffset Expires { get; } = expires;

        public bool Matched { get; set; }
    }
}
