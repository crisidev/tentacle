using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Prometheus;
using Tentacle.Protocol;

namespace Tentacle.Broker;

/// <summary>
/// The broker's Prometheus metrics. The plugin registers them in prometheus-net's
/// default registry, which is Jellyfin's own: they appear in Jellyfin's /metrics
/// (System → EnableMetrics). Job counters move as jobs run; node gauges are
/// refreshed on every scrape. A tentacle that disconnects stays visible as state
/// "Disconnected" until the server restarts, so its absence can be alerted on.
/// </summary>
public sealed class TentacleMetrics
{
    /// <summary>The state a known tentacle has while not connected.</summary>
    public const string Disconnected = "Disconnected";

    private static readonly string[] States = [.. Enum.GetNames<NodeState>(), Disconnected];
    private static readonly string[] Modes = Enum.GetNames<PlacementMode>();

    private readonly Counter _jobs;
    private readonly Histogram _duration;
    private readonly Gauge _running;
    private readonly Counter _placements;
    private readonly Gauge _nodeState;
    private readonly Gauge _slots;
    private readonly Gauge _slotsUsed;
    private readonly Gauge _nodeInfo;
    private readonly Gauge _nodeCheck;
    private readonly Gauge _capability;
    private readonly Gauge _runningJob;
    private readonly Dictionary<string, string[]> _runningSeries = new(StringComparer.Ordinal);
    private readonly Gauge _brokerUp;
    private readonly Gauge _mode;
    private readonly Gauge _server;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    // Detect-only tentacles: their series go when they do, so taking a probe away
    // does not leave a Disconnected tentacle behind (TentacleNodeDown).
    private readonly HashSet<string> _transient = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _info = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _checks = new(StringComparer.Ordinal);
    private string[]? _serverLabels;

    /// <summary>
    /// Initializes a new instance of the <see cref="TentacleMetrics"/> class.
    /// </summary>
    /// <param name="registry">Where to register; null for a private registry (tests).</param>
    public TentacleMetrics(CollectorRegistry? registry = null)
    {
        Registry = registry ?? Metrics.NewCustomRegistry();
        var factory = Metrics.WithCustomRegistry(Registry);

        _jobs = factory.CreateCounter(
            "tentacle_jobs_total",
            "ffmpeg jobs that went through the broker, by where they ran (\"local\" = the server) and how they ended (ok, error, killed, lost, failed, exited).",
            new CounterConfiguration { LabelNames = ["node", "kind", "outcome"] });
        _duration = factory.CreateHistogram(
            "tentacle_job_duration_seconds",
            "Run time of finished jobs.",
            new HistogramConfiguration { LabelNames = ["node", "kind"], Buckets = Histogram.ExponentialBuckets(0.25, 2, 15) });
        _running = factory.CreateGauge(
            "tentacle_jobs_running",
            "Jobs running now.",
            new GaugeConfiguration { LabelNames = ["node", "kind"] });
        _placements = factory.CreateCounter(
            "tentacle_placements_total",
            "Placement decisions: where each job went and why (least-loaded, kind-local, path-ineligible, all-busy, dry-run...).",
            new CounterConfiguration { LabelNames = ["kind", "node", "reason"] });
        _nodeState = factory.CreateGauge(
            "tentacle_node_state",
            "1 for the tentacle's current state, 0 for the others.",
            new GaugeConfiguration { LabelNames = ["node", "state"] });
        _slots = factory.CreateGauge(
            "tentacle_node_slots",
            "Job slots a tentacle offers (class all, or the background cap).",
            new GaugeConfiguration { LabelNames = ["node", "class"] });
        _slotsUsed = factory.CreateGauge(
            "tentacle_node_slots_used",
            "Slots in use: the summed cost of running jobs.",
            new GaugeConfiguration { LabelNames = ["node", "class"] });
        _nodeInfo = factory.CreateGauge(
            "tentacle_node_info",
            "Always 1: the tentacle's agent and ffmpeg versions, host, architecture and GPU (vendor none for a CPU-only tentacle).",
            new GaugeConfiguration { LabelNames = ["node", "agent_version", "ffmpeg_version", "host", "arch", "vendor", "gpu", "api"] });
        _runningJob = factory.CreateGauge(
            "tentacle_running_job_start_time_seconds",
            "One series per running ffmpeg job: where it runs, and who and what it is for when the plugin's \"Name viewers in metrics\" is on; the value is its start (Unix seconds). Gone when the job ends.",
            new GaugeConfiguration { LabelNames = ["job_id", "node", "kind", "user", "client", "item"] });
        _capability = factory.CreateGauge(
            "tentacle_node_capability",
            "1 for each thing a tentacle's GPU proved it can do at agent start (encode:h264, decode:hevc10...).",
            new GaugeConfiguration { LabelNames = ["node", "capability"] });
        _nodeCheck = factory.CreateGauge(
            "tentacle_node_check",
            "Last verification of a tentacle: 1 if the check passed (ffmpeg, uid, hardware:<type>, root:<path>).",
            new GaugeConfiguration { LabelNames = ["node", "check", "required"] });
        _brokerUp = factory.CreateGauge(
            "tentacle_broker_up",
            "1 when the broker listens for shims and agents; 0 means every job runs locally.");
        _mode = factory.CreateGauge(
            "tentacle_placement_mode",
            "1 for the configured placement mode (Disabled, DryRun, Active).",
            new GaugeConfiguration { LabelNames = ["mode"] });
        _server = factory.CreateGauge(
            "tentacle_server_info",
            "1, labelled with this server's name (it is node=\"local\" elsewhere) and its role: worker (competes for jobs), backup (only what no tentacle can take) or never.",
            new GaugeConfiguration { LabelNames = ["name", "role"] });
    }

    /// <summary>Gets the registry the metrics live in.</summary>
    public CollectorRegistry Registry { get; }

    /// <summary>
    /// Records a placement decision; the job counts as running until <see cref="JobEnded"/>.
    /// </summary>
    /// <param name="kind">The job kind.</param>
    /// <param name="node">Where it runs.</param>
    /// <param name="reason">Why; a detail after ':' (path-ineligible:/path) stays out of the label.</param>
    public void JobStarted(string kind, string node, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var colon = reason.IndexOf(':', StringComparison.Ordinal);
        _placements.WithLabels(kind, node, colon < 0 ? reason : reason[..colon]).Inc();
        _running.WithLabels(node, kind).Inc();
    }

    /// <summary>
    /// Records a finished job.
    /// </summary>
    /// <param name="record">The job, with its final node and outcome.</param>
    /// <param name="startedOn">The node it was counted as running on.</param>
    public void JobEnded(JobRecord record, string startedOn)
    {
        ArgumentNullException.ThrowIfNull(record);
        _running.WithLabels(startedOn, record.Kind).Dec();
        _jobs.WithLabels(record.Node, record.Kind, record.Outcome).Inc();
        if (record.DurationMs is { } ms)
        {
            _duration.WithLabels(record.Node, record.Kind).Observe(ms / 1000.0);
        }
    }

    /// <summary>
    /// Remembers a tentacle, so it shows as Disconnected after it goes even if no
    /// scrape happened while it was connected.
    /// </summary>
    /// <param name="name">The node name.</param>
    /// <param name="detectOnly">Whether it is a detect-only tentacle, forgotten when it goes.</param>
    public void NodeSeen(string name, bool detectOnly = false)
    {
        lock (_lock)
        {
            (detectOnly ? _transient : _known).Add(name);
        }
    }

    /// <summary>
    /// Refreshes the node and broker gauges; call before every scrape.
    /// </summary>
    /// <param name="nodes">The connected tentacles.</param>
    /// <param name="brokerUp">Whether the broker listens.</param>
    /// <param name="mode">The placement mode.</param>
    /// <param name="local">This server's seat: its slots and load appear as node "local".</param>
    public void Refresh(IReadOnlyCollection<TentacleNode> nodes, bool brokerUp, PlacementMode mode, LocalSeat? local = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _brokerUp.Set(brokerUp ? 1 : 0);
        if (local is not null)
        {
            _slots.WithLabels(Scheduler.LocalName, "all").Set(local.Slots);
            _slots.WithLabels(Scheduler.LocalName, "background").Set(local.BackgroundSlots);
            _slotsUsed.WithLabels(Scheduler.LocalName, "all").Set(Math.Round(local.Load, 2));
            _slotsUsed.WithLabels(Scheduler.LocalName, "background").Set(Math.Round(local.BackgroundLoad, 2));
        }

        foreach (var m in Modes)
        {
            _mode.WithLabels(m).Set(string.Equals(m, mode.ToString(), StringComparison.Ordinal) ? 1 : 0);
        }

        lock (_lock)
        {
            var connected = nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
            foreach (var n in connected.Values)
            {
                (n.DetectOnly ? _transient : _known).Add(n.Name);
            }

            foreach (var name in _known.Union(_transient).ToArray())
            {
                connected.TryGetValue(name, out var node);
                if (node is null && !_known.Contains(name))
                {
                    foreach (var s in States)
                    {
                        _nodeState.RemoveLabelled(name, s);
                    }

                    Forget(name);
                    _transient.Remove(name);
                    continue;
                }

                var snapshot = node?.Snapshot();
                var state = snapshot?.State ?? Disconnected;
                foreach (var s in States)
                {
                    _nodeState.WithLabels(name, s).Set(string.Equals(s, state, StringComparison.Ordinal) ? 1 : 0);
                }

                if (snapshot is null)
                {
                    Forget(name);
                    continue;
                }

                _slots.WithLabels(name, "all").Set(snapshot.MaxJobs);
                _slots.WithLabels(name, "background").Set(snapshot.MaxBackgroundJobs);
                _slotsUsed.WithLabels(name, "all").Set(snapshot.Load);
                _slotsUsed.WithLabels(name, "background").Set(snapshot.BackgroundLoad);

                var gpu = snapshot.Gpu;
                string[] info =
                [
                    name, snapshot.AgentVersion, ShortVersion(snapshot.FfmpegVersion), snapshot.Host, snapshot.Arch,
                    gpu?.Vendor ?? "none", gpu?.Model ?? string.Empty, gpu?.Api ?? Hardware.NoApi,
                ];
                foreach (var capability in Hardware.Capabilities)
                {
                    _capability.WithLabels(name, capability).Set(gpu?.Capabilities.Contains(capability) == true ? 1 : 0);
                }

                if (_info.TryGetValue(name, out var old) && !old.SequenceEqual(info, StringComparer.Ordinal))
                {
                    _nodeInfo.RemoveLabelled(old);
                }

                _info[name] = info;
                _nodeInfo.WithLabels(info).Set(1);

                var checks = _checks.TryGetValue(name, out var seen) ? seen : _checks[name] = new HashSet<string>(StringComparer.Ordinal);
                var current = new HashSet<string>(StringComparer.Ordinal);
                foreach (var check in snapshot.Checks)
                {
                    var required = check.Required ? "true" : "false";
                    current.Add(check.Name + "\0" + required);
                    _nodeCheck.WithLabels(name, check.Name, required).Set(check.Ok ? 1 : 0);
                }

                foreach (var stale in checks.Except(current).ToArray())
                {
                    var parts = stale.Split('\0');
                    _nodeCheck.RemoveLabelled(name, parts[0], parts[1]);
                }

                checks.Clear();
                checks.UnionWith(current);
            }
        }
    }

    /// <summary>
    /// Names this server and its role in tentacle_server_info, so dashboards can show
    /// node="local" by name.
    /// </summary>
    /// <param name="name">TENTACLE_NODE_NAME, else the hostname.</param>
    /// <param name="role">worker, backup or never.</param>
    public void RefreshServer(string name, string role)
    {
        lock (_lock)
        {
            if (_serverLabels is { } old && (old[0] != name || old[1] != role))
            {
                _server.RemoveLabelled(old);
            }

            _serverLabels = [name, role];
            _server.WithLabels(name, role).Set(1);
        }
    }

    /// <summary>
    /// Mirrors the running jobs as series: who, what and where, for the dashboards.
    /// A job's labels change once it is attributed; the old series is dropped.
    /// </summary>
    /// <param name="jobs">The jobs; those not running are ignored.</param>
    /// <param name="nameViewers">Whether user, app and item go into the labels. Off by default:
    /// /metrics is often less protected than the API, and who watches what is personal.</param>
    public void RefreshRunning(IEnumerable<JobRecord> jobs, bool nameViewers = false)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        lock (_lock)
        {
            var current = new Dictionary<string, (string[] Labels, double Start)>(StringComparer.Ordinal);
            foreach (var job in jobs.Where(j => j.Outcome == "running"))
            {
                string[] labels = nameViewers
                    ? [job.Id, job.Node, job.Kind, job.User ?? string.Empty, job.Client ?? string.Empty, job.Item ?? string.Empty]
                    : [job.Id, job.Node, job.Kind, string.Empty, string.Empty, string.Empty];
                current[job.Id] = (labels, job.StartedAt.ToUnixTimeMilliseconds() / 1000.0);
            }

            foreach (var (id, labels) in _runningSeries.ToArray())
            {
                if (!current.TryGetValue(id, out var now) || !now.Labels.SequenceEqual(labels, StringComparer.Ordinal))
                {
                    _runningJob.RemoveLabelled(labels);
                    _runningSeries.Remove(id);
                }
            }

            foreach (var (id, (labels, start)) in current)
            {
                _runningJob.WithLabels(labels).Set(start);
                _runningSeries[id] = labels;
            }
        }
    }

    /// <summary>
    /// "ffmpeg version 8.1.3-Jellyfin Copyright (c)..." → "8.1.3-Jellyfin".
    /// </summary>
    private static string ShortVersion(string line)
    {
        const string Prefix = "ffmpeg version ";
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return line;
        }

        var rest = line[Prefix.Length..];
        var space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? rest : rest[..space];
    }

    private void Forget(string name)
    {
        foreach (var c in new[] { "all", "background" })
        {
            _slots.RemoveLabelled(name, c);
            _slotsUsed.RemoveLabelled(name, c);
        }

        foreach (var capability in Hardware.Capabilities)
        {
            _capability.RemoveLabelled(name, capability);
        }

        if (_info.Remove(name, out var info))
        {
            _nodeInfo.RemoveLabelled(info);
        }

        if (_checks.Remove(name, out var checks))
        {
            foreach (var check in checks)
            {
                var parts = check.Split('\0');
                _nodeCheck.RemoveLabelled(name, parts[0], parts[1]);
            }
        }
    }
}
