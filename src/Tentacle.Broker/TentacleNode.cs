using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Tentacle.Protocol;

namespace Tentacle.Broker;

/// <summary>
/// A tentacle's state, as the scheduler and the dashboard see it.
/// </summary>
public enum NodeState
{
    /// <summary>Registered, shares and hardware not proven yet: no jobs.</summary>
    Verifying,

    /// <summary>Every check passed.</summary>
    Ready,

    /// <summary>Usable, with limits: some roots unshared, no hardware, a patch-level ffmpeg difference.</summary>
    Degraded,

    /// <summary>Must not run jobs (different ffmpeg major.minor).</summary>
    Incompatible,

    /// <summary>Shutting down: finishes its jobs, takes no new ones.</summary>
    Draining,

    /// <summary>Failed repeatedly: benched until the cooldown ends and it is verified again.</summary>
    CoolingDown,

    /// <summary>Verified, and only reports what it found: it never takes jobs (TENTACLE_MAX_JOBS=0).</summary>
    DetectOnly,
}

/// <summary>
/// One verification check.
/// </summary>
/// <param name="Name">What was checked (ffmpeg, uid, hardware, root:/path).</param>
/// <param name="Ok">Whether it passed.</param>
/// <param name="Detail">What was found.</param>
/// <param name="Required">Whether failing it degrades the tentacle (otherwise it only narrows its jobs).</param>
public sealed record NodeCheck(string Name, bool Ok, string Detail, bool Required = true);

/// <summary>
/// A slot reservation for one job; released when the job ends.
/// </summary>
/// <param name="Cost">The job's cost.</param>
/// <param name="Background">Whether it is a background job.</param>
public sealed record JobLease(double Cost, bool Background);

/// <summary>
/// A registered tentacle: one agent connection.
/// </summary>
public sealed class TentacleNode
{
    private const int FailuresBeforeCooldown = 3;
    private static readonly TimeSpan FirstCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    private readonly Channel<Frame> _outbox = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private double _load;
    private double _backgroundLoad;
    private int _activeJobs;
    private int _failures;
    private int _cooldowns;
    private DateTimeOffset? _cooldownUntil;

    /// <summary>
    /// Initializes a new instance of the <see cref="TentacleNode"/> class.
    /// </summary>
    /// <param name="hello">The agent's registration.</param>
    /// <param name="clock">The clock (tests use a fake one).</param>
    public TentacleNode(Hello hello, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(hello);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Name = hello.Node;
        InstanceId = hello.InstanceId;
        AgentVersion = hello.AgentVersion;
        FfmpegVersion = hello.FfmpegVersion;
        DetectOnly = hello.DetectOnly;
        MaxJobs = DetectOnly ? 0 : Math.Max(1, hello.MaxJobs);
        MaxBackgroundJobs = DetectOnly ? 0 : hello.MaxBackgroundJobs > 0 ? Math.Min(hello.MaxBackgroundJobs, MaxJobs) : Math.Max(1, MaxJobs / 2);
        Host = hello.Host.Length > 0 ? hello.Host : hello.Node;
        Arch = hello.Arch;
        Kernel = hello.Kernel;
        CpuModel = hello.CpuModel;
        Sandbox = hello.Sandbox;
        Gpu = hello.Gpu;
        Weight = hello.Weight > 0 ? hello.Weight : 1;
        Uid = hello.Uid;
        Devices = hello.Devices;
        Cores = hello.Cores;
        ConnectedAt = _clock();
        LastSeen = ConnectedAt;
    }

    /// <summary>Gets the node name.</summary>
    public string Name { get; }

    /// <summary>Gets the agent process id.</summary>
    public string InstanceId { get; }

    /// <summary>Gets the agent version.</summary>
    public string AgentVersion { get; }

    /// <summary>Gets the node's ffmpeg version line.</summary>
    public string FfmpegVersion { get; }

    /// <summary>Gets the job slots.</summary>
    public int MaxJobs { get; }

    /// <summary>Gets the slots background jobs may use.</summary>
    public int MaxBackgroundJobs { get; }

    /// <summary>Gets the scheduling weight.</summary>
    public double Weight { get; }

    /// <summary>Gets the uid the agent runs as.</summary>
    public int Uid { get; }

    /// <summary>Gets the GPU devices the agent sees.</summary>
    public IReadOnlyList<string> Devices { get; }

    /// <summary>Gets the CPU count.</summary>
    public int Cores { get; }

    /// <summary>Gets the host the tentacle runs on (several tentacles share one with several GPUs).</summary>
    public string Host { get; }

    /// <summary>Gets the CPU architecture, empty for agents before 0.7.</summary>
    public string Arch { get; }

    /// <summary>Gets the kernel release.</summary>
    public string Kernel { get; }

    /// <summary>Gets the CPU or board model.</summary>
    public string CpuModel { get; }

    /// <summary>Gets when this tentacle last took a job, as a placement turn (higher is later).</summary>
    public long LastTurn { get; private set; }

    /// <summary>Gets how the tentacle confines its jobs, or empty when it does not.</summary>
    public string Sandbox { get; }

    /// <summary>Gets the GPU this tentacle stands for, or null (CPU only, or an agent before 0.7).</summary>
    public GpuInfo? Gpu { get; }

    /// <summary>Gets a value indicating whether the tentacle only reports and never takes jobs.</summary>
    public bool DetectOnly { get; }

    /// <summary>Gets a value indicating whether the agent predates hardware detection (it sends no GPU facts).</summary>
    public bool Legacy => Arch.Length == 0;

    /// <summary>Gets when the agent connected.</summary>
    public DateTimeOffset ConnectedAt { get; }

    /// <summary>Gets or sets when the agent last answered.</summary>
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Gets or sets the token the agent authenticated with.</summary>
    public string Credential { get; set; } = string.Empty;

    /// <summary>Gets or sets which configured token that is, as of the last heartbeat.</summary>
    public TokenMatch TokenMatch { get; set; } = TokenMatch.Current;

    /// <summary>Gets or sets a value indicating whether the agent announced it is shutting down.</summary>
    public bool Draining { get; set; }

    /// <summary>Gets the last verification's checks.</summary>
    public IReadOnlyList<NodeCheck> Checks { get; private set; } = [];

    /// <summary>Gets the roots this tentacle proved it shares.</summary>
    public IReadOnlyList<string> VerifiedRoots { get; private set; } = [];

    /// <summary>Gets a value indicating whether the hardware self-test passed.</summary>
    public bool HardwareOk { get; private set; }

    /// <summary>Gets a value indicating whether the ffmpeg build rules this tentacle out.</summary>
    public bool Incompatible { get; private set; }

    /// <summary>Gets when the tentacle was last verified, if ever.</summary>
    public DateTimeOffset? VerifiedAt { get; private set; }

    /// <summary>Gets or sets a value indicating whether a verification is running.</summary>
    public bool Verifying { get; set; }

    /// <summary>Gets or sets a value indicating whether the next ping should start a verification.</summary>
    public bool NeedsVerify { get; set; } = true;

    /// <summary>Gets or sets the pending verification's result.</summary>
    public TaskCompletionSource<VerifyResult>? PendingVerify { get; set; }

    /// <summary>Gets the running jobs.</summary>
    public int ActiveJobs => Volatile.Read(ref _activeJobs);

    /// <summary>Gets the cost of the running jobs.</summary>
    public double Load
    {
        get
        {
            lock (_lock)
            {
                return _load;
            }
        }
    }

    /// <summary>Gets the cooldown end, while benched.</summary>
    public DateTimeOffset? CooldownUntil
    {
        get
        {
            lock (_lock)
            {
                return _cooldownUntil is { } until && until > _clock() ? until : null;
            }
        }
    }

    /// <summary>Gets the current state.</summary>
    public NodeState State
    {
        get
        {
            if (Draining)
            {
                return NodeState.Draining;
            }

            // Takes no jobs, so nothing about it can be wrong in a way that matters:
            // an older ffmpeg or a missing share is information, not an alert.
            if (DetectOnly)
            {
                return VerifiedAt is null ? NodeState.Verifying : NodeState.DetectOnly;
            }

            if (Incompatible)
            {
                return NodeState.Incompatible;
            }

            if (CooldownUntil is not null)
            {
                return NodeState.CoolingDown;
            }

            if (VerifiedAt is null)
            {
                return NodeState.Verifying;
            }

            return Checks.All(c => c.Ok || !c.Required) ? NodeState.Ready : NodeState.Degraded;
        }
    }

    /// <summary>Gets a value indicating whether the scheduler may use this tentacle at all.</summary>
    public bool IsSchedulable => State is NodeState.Ready or NodeState.Degraded;

    /// <summary>Gets a token cancelled when the control connection ends.</summary>
    public CancellationTokenSource Lifetime { get; } = new();

    /// <summary>
    /// Whether every path is under a root this tentacle proved it shares.
    /// </summary>
    /// <param name="paths">The job's paths.</param>
    /// <returns>True if all are covered.</returns>
    public bool Covers(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var roots = VerifiedRoots;
        return paths.All(p => roots.Any(r => ArgvAnalysis.IsUnder(p, r)));
    }

    /// <summary>
    /// Whether a job of this cost fits now. Background jobs also have their own
    /// cap, so trickplay and analysis can never fill the slots playback needs.
    /// </summary>
    /// <param name="cost">The job's cost.</param>
    /// <param name="background">Whether it is a background job.</param>
    /// <returns>True if it fits.</returns>
    public bool CanAccept(double cost, bool background)
    {
        lock (_lock)
        {
            const double Epsilon = 1e-9;
            return _load + cost <= MaxJobs + Epsilon
                && (!background || _backgroundLoad + cost <= MaxBackgroundJobs + Epsilon);
        }
    }

    /// <summary>
    /// The load the node would have with one more job, divided by its weight.
    /// </summary>
    /// <param name="cost">The job's cost.</param>
    /// <returns>The score; lower is better.</returns>
    public double ScoreWith(double cost)
    {
        lock (_lock)
        {
            return (_load + cost) / Weight;
        }
    }

    /// <summary>
    /// Reserves capacity for a job.
    /// </summary>
    /// <param name="cost">The job's cost.</param>
    /// <param name="background">Whether it is a background job.</param>
    /// <returns>The lease to release when the job ends.</returns>
    public JobLease Reserve(double cost, bool background)
    {
        lock (_lock)
        {
            _load += cost;
            if (background)
            {
                _backgroundLoad += cost;
            }
        }

        Interlocked.Increment(ref _activeJobs);
        LastTurn = Scheduler.NextTurn();
        return new JobLease(cost, background);
    }

    /// <summary>
    /// Releases a job's capacity.
    /// </summary>
    /// <param name="lease">The lease.</param>
    public void Release(JobLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_lock)
        {
            _load = Math.Max(0, _load - lease.Cost);
            if (lease.Background)
            {
                _backgroundLoad = Math.Max(0, _backgroundLoad - lease.Cost);
            }
        }

        Interlocked.Decrement(ref _activeJobs);
    }

    /// <summary>
    /// Records how a job ended. Infrastructure failures (lost connection, attach
    /// timeout, spawn error) count towards a cooldown; ffmpeg's own errors and
    /// cancellations do not: they would fail anywhere.
    /// </summary>
    /// <param name="infrastructureFailure">Whether the tentacle, not the job, failed.</param>
    /// <returns>True when this failure started a cooldown.</returns>
    public bool RecordOutcome(bool infrastructureFailure)
    {
        lock (_lock)
        {
            if (!infrastructureFailure)
            {
                _failures = 0;
                _cooldowns = 0;
                return false;
            }

            if (++_failures < FailuresBeforeCooldown)
            {
                return false;
            }

            var span = TimeSpan.FromTicks(Math.Min(FirstCooldown.Ticks << Math.Min(_cooldowns, 10), MaxCooldown.Ticks));
            _cooldownUntil = _clock() + span;
            _cooldowns++;
            _failures = 0;
            NeedsVerify = true;
            return true;
        }
    }

    /// <summary>
    /// Applies a verification outcome.
    /// </summary>
    /// <param name="checks">The checks.</param>
    /// <param name="verifiedRoots">The roots that proved shared.</param>
    /// <param name="hardwareOk">Whether the hardware test passed.</param>
    /// <param name="incompatible">Whether the ffmpeg build rules the node out.</param>
    public void ApplyVerification(IReadOnlyList<NodeCheck> checks, IReadOnlyList<string> verifiedRoots, bool hardwareOk, bool incompatible)
    {
        ArgumentNullException.ThrowIfNull(checks);
        Checks = DetectOnly ? checks.Select(c => c with { Required = false }).ToArray() : checks;
        VerifiedRoots = verifiedRoots;
        HardwareOk = hardwareOk;
        Incompatible = incompatible;
        VerifiedAt = _clock();
        NeedsVerify = false;
    }

    /// <summary>
    /// Queues a control frame; the connection's writer loop sends it.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <returns>False if the connection is gone.</returns>
    public bool Send(Frame frame) => _outbox.Writer.TryWrite(frame);

    /// <summary>
    /// Sends queued frames until the connection ends.
    /// </summary>
    /// <param name="socket">The control socket.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunWriterAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await SocketFrames.SendAsync(socket, frame, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops accepting frames.
    /// </summary>
    public void Close()
    {
        _outbox.Writer.TryComplete();
        PendingVerify?.TrySetCanceled();
        Lifetime.Cancel();
    }

    /// <summary>
    /// A snapshot for the dashboard.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public NodeSnapshot Snapshot()
    {
        double load, background;
        lock (_lock)
        {
            load = _load;
            background = _backgroundLoad;
        }

        return new NodeSnapshot(
            Name,
            State.ToString(),
            AgentVersion,
            FfmpegVersion,
            ActiveJobs,
            Math.Round(load, 2),
            MaxJobs,
            Math.Round(background, 2),
            MaxBackgroundJobs,
            Weight,
            Cores,
            HardwareOk,
            Devices,
            VerifiedRoots,
            Checks,
            CooldownUntil,
            VerifiedAt,
            ConnectedAt,
            LastSeen,
            TokenMatch == TokenMatch.Previous,
            Host,
            Arch,
            Kernel,
            CpuModel,
            DetectOnly,
            Gpu,
            Sandbox);
    }
}

/// <summary>
/// A tentacle as the dashboard shows it.
/// </summary>
/// <param name="Name">The node name.</param>
/// <param name="State">The state.</param>
/// <param name="AgentVersion">The agent version.</param>
/// <param name="FfmpegVersion">The node's ffmpeg version line.</param>
/// <param name="ActiveJobs">Running jobs.</param>
/// <param name="Load">Cost of the running jobs.</param>
/// <param name="MaxJobs">Job slots.</param>
/// <param name="BackgroundLoad">Cost of the running background jobs.</param>
/// <param name="MaxBackgroundJobs">Background slots.</param>
/// <param name="Weight">Scheduling weight.</param>
/// <param name="Cores">CPU count.</param>
/// <param name="HardwareOk">Whether the hardware self-test passed.</param>
/// <param name="Devices">GPU devices.</param>
/// <param name="VerifiedRoots">Roots proven shared.</param>
/// <param name="Checks">The last verification's checks.</param>
/// <param name="CooldownUntil">The cooldown end, while benched.</param>
/// <param name="VerifiedAt">The last verification.</param>
/// <param name="ConnectedAt">When the agent connected.</param>
/// <param name="LastSeen">When it last answered.</param>
/// <param name="UsesPreviousToken">Whether it still authenticates with the token before the last rotation.</param>
/// <param name="Host">The host it runs on.</param>
/// <param name="Arch">The CPU architecture.</param>
/// <param name="Kernel">The kernel release.</param>
/// <param name="CpuModel">The CPU or board model.</param>
/// <param name="DetectOnly">Whether it only reports and never takes jobs.</param>
/// <param name="Gpu">Its GPU and what that proved it can do, or null.</param>
/// <param name="Sandbox">How it confines its jobs, or empty.</param>
public sealed record NodeSnapshot(
    string Name,
    string State,
    string AgentVersion,
    string FfmpegVersion,
    int ActiveJobs,
    double Load,
    int MaxJobs,
    double BackgroundLoad,
    int MaxBackgroundJobs,
    double Weight,
    int Cores,
    bool HardwareOk,
    IReadOnlyList<string> Devices,
    IReadOnlyList<string> VerifiedRoots,
    IReadOnlyList<NodeCheck> Checks,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastSeen,
    bool UsesPreviousToken,
    string Host,
    string Arch,
    string Kernel,
    string CpuModel,
    bool DetectOnly,
    GpuInfo? Gpu,
    string Sandbox);

/// <summary>
/// The connected tentacles, by name. A reconnecting agent replaces its old entry.
/// </summary>
public sealed class NodeRegistry
{
    private readonly ConcurrentDictionary<string, TentacleNode> _nodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the connected nodes.
    /// </summary>
    public IReadOnlyCollection<TentacleNode> Nodes => _nodes.Values.ToArray();

    /// <summary>
    /// Finds a node by name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The node, or null.</returns>
    public TentacleNode? Find(string name) => _nodes.TryGetValue(name, out var node) ? node : null;

    /// <summary>
    /// Registers a node, closing any older connection with the same name.
    /// </summary>
    /// <param name="node">The node.</param>
    public void Add(TentacleNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.AddOrUpdate(node.Name, node, (_, old) =>
        {
            old.Close();
            return node;
        });
    }

    /// <summary>
    /// Removes a node if it is still the registered connection.
    /// </summary>
    /// <param name="node">The node.</param>
    public void Remove(TentacleNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.TryRemove(new KeyValuePair<string, TentacleNode>(node.Name, node));
    }

    /// <summary>
    /// Snapshots for the dashboard.
    /// </summary>
    /// <returns>The snapshots, by name.</returns>
    public IReadOnlyList<NodeSnapshot> Snapshot() => _nodes.Values.Select(n => n.Snapshot()).OrderBy(n => n.Name, StringComparer.Ordinal).ToArray();
}
