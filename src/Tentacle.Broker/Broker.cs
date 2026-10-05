using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Tentacle.Protocol;

namespace Tentacle.Broker;

/// <summary>
/// The broker: places every shim invocation, keeps the tentacle registry, proves
/// what each tentacle shares, and bridges remote jobs. A job lives exactly as long
/// as its connections: when the shim's socket closes (Jellyfin killed it) the agent
/// kills the process, and when the agent goes away the shim exits non-zero.
/// </summary>
public sealed partial class Broker
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoFallbackPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReplyVisibility = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] AttributionRetries = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];
    private readonly ConcurrentDictionary<string, PendingJob> _pending = new(StringComparer.Ordinal);
    private readonly ILogger<Broker> _logger;
    private readonly LocalSeat _local = new();
    private long _jobCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="Broker"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="metrics">The metrics; null for a private registry.</param>
    public Broker(BrokerOptions options, ILogger<Broker> logger, TentacleMetrics? metrics = null)
    {
        Options = options;
        _logger = logger;
        Metrics = metrics ?? new TentacleMetrics();
        Metrics.Registry.AddBeforeCollectCallback(() =>
        {
            Metrics.Refresh(Nodes.Nodes, Listening, Options.Placement, Local);
            Metrics.RefreshServer(Options.ServerName, !Options.LocalFallback ? "never" : Local.Slots > 0 ? "worker" : "backup");
            Metrics.RefreshRunning(History.Snapshot(JobHistory.Capacity), Options.MetricsNameViewers);
        });
    }

    /// <summary>Gets the options.</summary>
    public BrokerOptions Options { get; }

    /// <summary>Gets the metrics.</summary>
    public TentacleMetrics Metrics { get; }

    /// <summary>Gets or sets a value indicating whether the host listens for shims and agents.</summary>
    public bool Listening { get; set; }

    /// <summary>Gets the connected tentacles.</summary>
    public NodeRegistry Nodes { get; } = new();

    /// <summary>
    /// Gets this server's seat in placement; its slots and weight follow <see cref="BrokerOptions.LocalSlots"/>
    /// and <see cref="BrokerOptions.LocalWeight"/>.
    /// </summary>
    public LocalSeat Local
    {
        get
        {
            _local.Slots = Math.Max(0, Options.LocalSlots);
            _local.Weight = Options.LocalWeight > 0 ? Options.LocalWeight : 1;
            return _local;
        }
    }

    /// <summary>Gets the recent jobs.</summary>
    public JobHistory History { get; } = new();

    /// <summary>
    /// Asks every tentacle to prove its shares and hardware again (encoding settings changed).
    /// </summary>
    public void ReverifyAll()
    {
        foreach (var node in Nodes.Nodes)
        {
            node.NeedsVerify = true;
        }
    }

    /// <summary>
    /// Asks one tentacle to prove its shares and hardware again.
    /// </summary>
    /// <param name="name">The tentacle.</param>
    /// <returns>False if it is not connected.</returns>
    public bool RequestVerify(string name)
    {
        if (Nodes.Find(name) is not { } node)
        {
            return false;
        }

        node.NeedsVerify = true;
        return true;
    }

    /// <summary>
    /// Serves one shim connection.
    /// </summary>
    /// <param name="input">Frames from the shim.</param>
    /// <param name="output">Frames to the shim.</param>
    /// <param name="peer">The shim's credentials, when the transport knows them.</param>
    /// <param name="cancellationToken">Cancelled when the connection or the broker ends.</param>
    /// <returns>A task.</returns>
    public async Task HandleShimAsync(Stream input, Stream output, PeerCredentials? peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        // Only Jellyfin's own children (same uid) may submit jobs: whoever can
        // talk to this socket can make a tentacle run ffmpeg.
        if (peer is not null && PeerCredentials.CurrentUid >= 0 && peer.Uid != PeerCredentials.CurrentUid)
        {
            LogPeerRejected(peer.Pid, peer.Uid);
            return;
        }

        StartRequest request;
        using (var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            startCts.CancelAfter(StartTimeout);
            var first = await StreamFrames.ReadAsync(input, startCts.Token).ConfigureAwait(false);
            if (first is not { Type: FrameType.Start } start)
            {
                return;
            }

            request = start.Read(ProtocolJson.Default.StartRequest);
        }

        var analysis = ArgvAnalysis.Analyze(request.Binary, request.Args, request.Cwd);
        var mode = Options.Placement;

        // Without the fallback this server neither competes for jobs nor takes one a
        // tentacle could run: such a job waits for a tentacle, then is refused.
        var strict = !Options.LocalFallback && mode == PlacementMode.Active;
        PlacementDecision Decide() => request.Protocol == ProtocolInfo.Version
            ? Scheduler.Place(analysis, mode, Nodes.Nodes, strict ? null : Local)
            : new PlacementDecision(null, "protocol-mismatch", Scheduler.PolicyFor(analysis.Kind));
        var decision = Decide();

        var record = new JobRecord
        {
            Id = NextJobId(),
            Kind = analysis.Kind.ToString(),
            StartedAt = DateTimeOffset.UtcNow,
            Reason = decision.Reason,
            WouldRunOn = decision.WouldRunOn,
            Command = Shorten(request),
        };

        var nice = decision.Policy.Background ? Options.BackgroundNice : 0;
        var waitUntil = DateTimeOffset.UtcNow + Options.NoFallbackWait;
        PendingJob? pending = null;
        JobLease? lease = null;
        while (true)
        {
            if (decision.Node is { } node)
            {
                lease = node.Reserve(decision.Policy.Cost, decision.Policy.Background);
                pending = await AssignAsync(node, record.Id, request, analysis.NeedsCwd, nice, cancellationToken).ConfigureAwait(false);
                if (pending?.Socket is not null)
                {
                    break;
                }

                node.Release(lease);
                lease = null;
                pending = null;
                decision = decision with { Node = null, Reason = "attach-timeout" };
                Account(node, infrastructureFailure: true);
            }

            if (!strict || !decision.Policy.Remote || !Scheduler.IsCapacity(decision.Reason) || DateTimeOffset.UtcNow >= waitUntil)
            {
                break;
            }

            await Task.Delay(NoFallbackPoll, cancellationToken).ConfigureAwait(false);
            decision = Decide();
        }

        record.Reason = decision.Reason;
        if (strict && lease is null && decision.Policy.Remote && Scheduler.IsCapacity(decision.Reason))
        {
            await RefuseAsync(output, analysis, record, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (mode != PlacementMode.Disabled)
        {
            History.Add(record);
            _ = AttributeAsync(analysis, record);
        }

        if (pending?.Socket is null || lease is null)
        {
            Metrics.JobStarted(record.Kind, record.Node, record.Reason);

            // Work that could have gone remote loads this server, whatever sent it here.
            var localLease = decision.Policy.Remote ? Local.Reserve(decision.Policy.Cost, decision.Policy.Background) : null;
            try
            {
                await RunLocalAsync(input, output, record, peer?.Pid ?? request.Pid, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (localLease is not null)
                {
                    Local.Release(localLease);
                }

                Ended(record, "local");
            }

            return;
        }

        var target = decision.Node!;
        record.Node = target.Name;
        record.Nice = nice;
        Metrics.JobStarted(record.Kind, record.Node, record.Reason);
        try
        {
            LogRemote(record.Id, record.Kind, target.Name);
            var placement = new Placement { Remote = true, JobId = record.Id, Node = target.Name, Reason = record.Reason, Nice = nice };
            await StreamFrames.WriteAsync(output, Frame.Json(FrameType.Placement, placement, ProtocolJson.Default.Placement), cancellationToken).ConfigureAwait(false);
            await BridgeAsync(input, output, pending.Socket, record, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            target.Release(lease);
            pending.Done.TrySetResult();
            _pending.TryRemove(record.Id, out _);
            Account(target, record.Outcome is "lost" or "failed");
            Ended(record, target.Name);
        }
    }

    /// <summary>
    /// Serves an agent's control WebSocket: registration, verification, heartbeats, assignments.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public async Task HandleControlAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var credential = await AcceptAuthorizedAsync(context).ConfigureAwait(false);
        if (credential is null)
        {
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var aborted = context.RequestAborted;

        Hello hello;
        using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(aborted))
        {
            helloCts.CancelAfter(HelloTimeout);
            var first = await SocketFrames.ReceiveAsync(socket, helloCts.Token).ConfigureAwait(false);
            if (first is not { Type: FrameType.Hello } helloFrame)
            {
                return;
            }

            hello = helloFrame.Read(ProtocolJson.Default.Hello);
        }

        if (hello.ProtocolMin > ProtocolInfo.Version || hello.ProtocolMax < ProtocolInfo.Version || string.IsNullOrWhiteSpace(hello.Node))
        {
            var reason = $"protocol {ProtocolInfo.Version} not in [{hello.ProtocolMin},{hello.ProtocolMax}] or no node name";
            await SocketFrames.SendAsync(socket, Frame.Json(FrameType.Reject, new ErrorInfo { Message = reason }, ProtocolJson.Default.ErrorInfo), aborted).ConfigureAwait(false);
            LogRejected(hello.Node, reason);
            return;
        }

        var welcome = new Welcome
        {
            Protocol = ProtocolInfo.Version,
            HeartbeatMs = (int)Options.Heartbeat.TotalMilliseconds,
            ServerFfmpegVersion = Options.ServerFfmpegVersion(),
        };
        await SocketFrames.SendAsync(socket, Frame.Json(FrameType.Welcome, welcome, ProtocolJson.Default.Welcome), aborted).ConfigureAwait(false);

        var node = new TentacleNode(hello) { Credential = credential };
        node.TokenMatch = Options.Match(credential, DateTimeOffset.UtcNow);
        Nodes.Add(node);
        Metrics.NodeSeen(node.Name, node.DetectOnly);
        LogRegistered(node.Name, node.AgentVersion, node.FfmpegVersion, node.MaxJobs, node.MaxBackgroundJobs);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(aborted, node.Lifetime.Token);
        var token = lifetime.Token;
        try
        {
            var writer = node.RunWriterAsync(socket, token);
            var pinger = HeartbeatAsync(node, token);
            while (!token.IsCancellationRequested)
            {
                var frame = await SocketFrames.ReceiveAsync(socket, token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                node.LastSeen = DateTimeOffset.UtcNow;
                switch (frame.Value.Type)
                {
                    case FrameType.VerifyResult:
                        node.PendingVerify?.TrySetResult(frame.Value.Read(ProtocolJson.Default.VerifyResult));
                        break;
                    case FrameType.Draining when !node.Draining:
                        node.Draining = true;
                        LogDraining(node.Name, node.ActiveJobs);
                        break;
                    default:
                        break;
                }
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quietly(writer), Quietly(pinger)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            // The agent went away or broke protocol: it is removed below, and its
            // jobs die with their job sockets.
        }
        finally
        {
            Nodes.Remove(node);
            node.Close();
            LogUnregistered(node.Name);
        }
    }

    /// <summary>
    /// Serves an agent's job WebSocket: attaches it to the waiting shim connection.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="jobId">The job id from the route.</param>
    /// <returns>A task that ends when the job does.</returns>
    public async Task HandleJobAsync(HttpContext context, string jobId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_pending.TryGetValue(jobId, out var pending)
            || !FixedEquals(context.Request.Query["key"].ToString(), pending.Key))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (await AcceptAuthorizedAsync(context).ConfigureAwait(false) is null)
        {
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        if (!pending.Attached.TrySetResult(socket))
        {
            socket.Abort();
            socket.Dispose();
            return;
        }

        // The socket belongs to this request: keep it open until the bridge is done.
        await pending.Done.Task.ConfigureAwait(false);
        socket.Dispose();
    }

    /// <summary>
    /// Records a shim connection refused because its peer credentials could not be read.
    /// </summary>
    public void RejectUnidentifiedShim() => LogPeerUnidentified();

    private static string? Presented(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = context.Request.Headers.Authorization.ToString();
        const string Prefix = "Bearer ";
        return header.StartsWith(Prefix, StringComparison.Ordinal) ? header[Prefix.Length..] : null;
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Shorten(StartRequest request)
    {
        var line = request.Binary + " " + string.Join(' ', request.Args);
        return line.Length <= 2000 ? line : string.Concat(line.AsSpan(0, 2000), "…");
    }

    private static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ChannelClosedException or IOException)
        {
        }
    }

    private static (string Path, long Size)? FindSample(string root)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden };
            var file = Directory.EnumerateFiles(root, "*", options).FirstOrDefault();
            return file is null ? null : (file, new FileInfo(file).Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteQuietly(string? path)
    {
        try
        {
            if (path is not null)
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The agent's reply must become visible to the server: on NFS with attribute
    /// caching that can take a moment, so retry briefly.
    /// </summary>
    private static async Task<(bool Ok, string Detail)> ReadReplyAsync(RootProbe probe, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ReplyVisibility;
        while (true)
        {
            try
            {
                var reply = ReplyFile.Read(probe.ReplyFile!);
                return string.Equals(reply.Trim(), probe.Nonce, StringComparison.Ordinal)
                    ? (true, "read/write")
                    : (false, "the reply the server read is not the tentacle's: a different directory at the same path");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    return (false, $"the tentacle's reply never reached the server: a different directory at the same path ({e.Message})");
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks a WebSocket request's token.
    /// </summary>
    /// <returns>The token it presented, or null after answering 400/401.</returns>
    private async Task<string?> AcceptAuthorizedAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }

        var presented = Presented(context);
        if (Options.Match(presented, DateTimeOffset.UtcNow) == TokenMatch.None)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("bad token", context.RequestAborted).ConfigureAwait(false);
            return null;
        }

        return presented;
    }

    /// <summary>
    /// Fills in who and what the job is for. Jellyfin registers a transcode right
    /// around starting ffmpeg, so retry briefly before giving up.
    /// </summary>
    /// <summary>
    /// Tells the shim not to run a job: no tentacle took it in time and this server
    /// does not fall back. ffmpeg then fails as if it had, with the reason on stderr.
    /// </summary>
    private async Task RefuseAsync(Stream output, ArgvAnalysis analysis, JobRecord record, CancellationToken cancellationToken)
    {
        record.Node = Scheduler.RefusedName;
        record.Outcome = "refused";
        record.DurationMs = (long)(DateTimeOffset.UtcNow - record.StartedAt).TotalMilliseconds;
        History.Add(record);
        _ = AttributeAsync(analysis, record);
        Metrics.JobStarted(record.Kind, record.Node, record.Reason);
        var why = $"no tentacle could take this {record.Kind.ToLowerInvariant()} job within {Options.NoFallbackWait.TotalSeconds:0} s ({record.Reason}), and {Options.ServerName} does not run jobs a tentacle could (Tentacle settings: \"This server runs jobs\")";
        try
        {
            var placement = new Placement { Remote = false, JobId = record.Id, Node = record.Node, Reason = record.Reason, Refused = why };
            await StreamFrames.WriteAsync(output, Frame.Json(FrameType.Placement, placement, ProtocolJson.Default.Placement), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
        }
        finally
        {
            Ended(record, record.Node);
        }
    }

    private async Task AttributeAsync(ArgvAnalysis analysis, JobRecord record)
    {
        foreach (var delay in AttributionRetries)
        {
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Options.Attribute(analysis) is { } found)
                {
                    record.User = found.User;
                    record.Client = found.Client;
                    record.Item = found.Item;
                    record.ItemId = found.ItemId;
                    record.PlaySessionId = found.PlaySessionId;
                    if (found.User is not null || analysis.Kind != JobKind.Transcode || record.Outcome != "running")
                    {
                        return;
                    }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
            {
                LogAttributionFailed(record.Id, e.Message);
                return;
            }
        }
    }

    /// <summary>
    /// Closes the books on a job: metrics, and one logfmt line Loki can parse.
    /// </summary>
    private void Ended(JobRecord record, string startedOn)
    {
        if (record.Outcome == "running")
        {
            // Cancelled before the job reported anything (the server is stopping).
            record.Outcome = "exited";
        }

        record.DurationMs ??= (long)(DateTimeOffset.UtcNow - record.StartedAt).TotalMilliseconds;
        Metrics.JobEnded(record, startedOn);
        LogEnded(record.Id, record.Kind, Logfmt(record.Node), Logfmt(record.Reason), Logfmt(record.WouldRunOn ?? string.Empty), record.Outcome, record.ExitCode, record.DurationMs.Value, record.Nice, Logfmt(record.User ?? string.Empty), Logfmt(record.Item ?? string.Empty));
    }

    /// <summary>
    /// A logfmt value: bare when safe, otherwise quoted with escapes. Reasons carry
    /// media paths and node names come from agents: neither may forge fields or lines.
    /// </summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The value, safe to put after "key=".</returns>
    public static string Logfmt(string value)
    {
        if (value.Length > 0 && value.All(c => c > ' ' && c != '"' && c != '=' && c != '\\' && !char.IsControl(c)))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            _ = c switch
            {
                '"' => sb.Append("\\\""),
                '\\' => sb.Append("\\\\"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                _ when char.IsControl(c) => sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"),
                _ => sb.Append(c),
            };
        }

        return sb.Append('"').ToString();
    }

    private void Account(TentacleNode node, bool infrastructureFailure)
    {
        if (node.RecordOutcome(infrastructureFailure) && node.CooldownUntil is { } until)
        {
            LogCooldown(node.Name, until);
        }
    }

    private string NextJobId()
        => string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:HHmmss}-{Interlocked.Increment(ref _jobCounter)}");

    private async Task<PendingJob?> AssignAsync(TentacleNode node, string jobId, StartRequest request, bool needsCwd, int nice, CancellationToken cancellationToken)
    {
        var pending = new PendingJob(RandomHex(16));
        _pending[jobId] = pending;
        var assign = new Assign
        {
            JobId = jobId,
            JobKey = pending.Key,
            Binary = request.Binary,
            Args = request.Args,

            // Jellyfin's own cwd (an s6 service dir) exists only on the server; it only
            // matters for jobs that write relative to it, and those passed the path check.
            Cwd = needsCwd ? request.Cwd : "/",
            Env = EnvPolicy.Filter(request.Env),
            Nice = nice,
        };

        if (node.Send(Frame.Json(FrameType.Assign, assign, ProtocolJson.Default.Assign)))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, node.Lifetime.Token);
                timeout.CancelAfter(Options.AttachTimeout);
                return pending with { Socket = await pending.Attached.Task.WaitAsync(timeout.Token).ConfigureAwait(false) };
            }
            catch (OperationCanceledException)
            {
                // Fall through: run locally.
            }
        }

        _pending.TryRemove(jobId, out _);
        pending.Done.TrySetResult();
        node.Send(Frame.Json(FrameType.Cancel, new CancelJob { JobId = jobId }, ProtocolJson.Default.CancelJob));
        LogAttachTimeout(jobId, node.Name);
        return null;
    }

    private async Task RunLocalAsync(Stream input, Stream output, JobRecord record, int pid, CancellationToken cancellationToken)
    {
        // Capture before ffmpeg can exit: afterwards the wait state is gone.
        var waitState = LocalExitTracker.Capture(pid);
        var placement = new Placement { Remote = false, JobId = record.Id, Node = "local", Reason = record.Reason };
        try
        {
            await StreamFrames.WriteAsync(output, Frame.Json(FrameType.Placement, placement, ProtocolJson.Default.Placement), cancellationToken).ConfigureAwait(false);

            // The shim execs ffmpeg with this socket inherited: EOF means ffmpeg exited.
            while (await StreamFrames.ReadAsync(input, cancellationToken).ConfigureAwait(false) is not null)
            {
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException)
        {
        }

        var code = await LocalExitTracker.ExitCodeAsync(waitState, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        record.ExitCode = code;
        record.Outcome = code switch
        {
            null => "exited",
            0 => "ok",
            _ => "error",
        };
        record.DurationMs = (long)(DateTimeOffset.UtcNow - record.StartedAt).TotalMilliseconds;
    }

    private async Task BridgeAsync(Stream shimIn, Stream shimOut, WebSocket agent, JobRecord record, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        using var shimLock = new SemaphoreSlim(1, 1);
        using var agentLock = new SemaphoreSlim(1, 1);
        var exited = false;
        var started = false;

        async Task ToShim(Frame frame)
        {
            await shimLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StreamFrames.WriteAsync(shimOut, frame, token).ConfigureAwait(false);
            }
            finally
            {
                shimLock.Release();
            }
        }

        // Shim → agent: stdin bytes, EOF, signals. Ends when the shim's socket closes:
        // Jellyfin killed the shim (SIGKILL after "q" + 5s) or the shim exited.
        async Task PumpFromShim()
        {
            while (await StreamFrames.ReadAsync(shimIn, token).ConfigureAwait(false) is { } frame)
            {
                if (frame.Type is FrameType.Stdin or FrameType.StdinEof or FrameType.Signal or FrameType.StdoutClosed)
                {
                    await agentLock.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await SocketFrames.SendAsync(agent, frame, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        agentLock.Release();
                    }
                }
            }
        }

        // Agent → shim: output and the exit status.
        async Task PumpFromAgent()
        {
            while (await SocketFrames.ReceiveAsync(agent, token).ConfigureAwait(false) is { } frame)
            {
                await ToShim(frame).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case FrameType.Started:
                        started = true;
                        break;
                    case FrameType.Exit:
                        var exit = frame.Read(ProtocolJson.Default.ExitInfo);
                        record.ExitCode = exit.Code;
                        record.Outcome = exit.Code == 0 ? "ok" : "error";
                        exited = true;
                        return;
                    case FrameType.Error:
                        // Before the process started it is the tentacle's fault (cwd, binary...).
                        record.Outcome = started ? "error" : "failed";
                        return;
                    default:
                        break;
                }
            }
        }

        var fromShim = PumpFromShim();
        var fromAgent = PumpFromAgent();
        var first = await Task.WhenAny(fromShim, fromAgent).ConfigureAwait(false);

        if (first == fromShim)
        {
            // The shim is gone before the process ended: kill it remotely by
            // dropping the job socket. The agent kills the process.
            if (!exited)
            {
                record.Outcome = "killed";
            }

            agent.Abort();
        }
        else if (!exited && record.Outcome is not ("error" or "failed"))
        {
            // The agent vanished mid-job (node died, network, agent restart).
            record.Outcome = "lost";
            try
            {
                var error = new ErrorInfo { Message = $"tentacle {record.Node} lost the job" };
                await ToShim(Frame.Json(FrameType.Error, error, ProtocolJson.Default.ErrorInfo)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
            }
        }

        // Give the other direction a moment to finish (the shim closes after Exit), then stop it.
        await Task.WhenAny(Task.WhenAll(fromShim, fromAgent), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(Quietly(fromShim), Quietly(fromAgent)).ConfigureAwait(false);
    }

    private async Task HeartbeatAsync(TentacleNode node, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Options.Heartbeat);
        do
        {
            if (DateTimeOffset.UtcNow - node.LastSeen > Options.Heartbeat * 3)
            {
                LogHeartbeatLost(node.Name);
                node.Close();
                return;
            }

            // A rotation (or the end of its overlap) retires the token this session
            // authenticated with: drop it; the agent reconnects with its new token.
            node.TokenMatch = Options.Match(node.Credential, DateTimeOffset.UtcNow);
            if (node.TokenMatch == TokenMatch.None)
            {
                LogTokenRetired(node.Name);
                node.Close();
                return;
            }

            var due = node.NeedsVerify || (node.VerifiedAt is { } at && DateTimeOffset.UtcNow - at > Options.VerifyInterval);
            if (due && !node.Verifying && node.CooldownUntil is null && !node.Draining)
            {
                node.Verifying = true;
                _ = Task.Run(() => VerifyAsync(node, cancellationToken), CancellationToken.None);
            }

            node.Send(Frame.Empty(FrameType.Ping));
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Why a tentacle cannot run the server's hardware command lines at all, so there
    /// is nothing to test: no GPU, or a GPU of another vendor. Null when it may.
    /// Agents before 0.7 say nothing about their GPU and are tested as before.
    /// </summary>
    /// <param name="node">The tentacle.</param>
    /// <param name="hardware">The server's hardware test.</param>
    /// <returns>The reason, or null.</returns>
    public static string? WhyNoHardwareTest(TentacleNode node, HardwareTest hardware)
    {
        if (node.Legacy)
        {
            return null;
        }

        if (node.Gpu is not { } gpu)
        {
            return $"no GPU: takes the jobs that use no hardware, not {hardware.Type} ones";
        }

        if (hardware.Vendor is { } vendor && !string.Equals(gpu.Vendor, vendor, StringComparison.Ordinal))
        {
            return $"{gpu.Vendor} GPU, and Jellyfin builds {hardware.Type} commands for {vendor}: takes the jobs that use no hardware";
        }

        return null;
    }

    /// <summary>
    /// Proves what a tentacle shares and whether its hardware works. Writable roots
    /// get a round-trip: the broker writes a nonce, the agent reads it and writes it
    /// back, the broker reads that. Read-only roots (libraries) get a stat of a file
    /// the server sees there. A root that fails is simply not used for that tentacle.
    /// </summary>
    private async Task VerifyAsync(TentacleNode node, CancellationToken cancellationToken)
    {
        var probes = new List<RootProbe>();
        var checks = new List<NodeCheck>();
        var required = new Dictionary<string, bool>(StringComparer.Ordinal);
        var verified = new List<string>();
        try
        {
            foreach (var root in Options.SharedRoots())
            {
                if (root.Writable && (root.Required || Directory.Exists(root.Path)))
                {
                    try
                    {
                        // Required directories are Jellyfin's working directories: create them
                        // as Jellyfin would. Optional ones are never created here.
                        if (root.Required)
                        {
                            Directory.CreateDirectory(root.Path);
                        }

                        var id = RandomHex(6);
                        var probe = new RootProbe
                        {
                            Path = root.Path,
                            Writable = true,
                            ProbeFile = Path.Combine(root.Path, $".tentacle-probe-{id}"),
                            ReplyFile = Path.Combine(root.Path, $".tentacle-reply-{id}"),
                            Nonce = RandomHex(16),
                        };
                        await File.WriteAllTextAsync(probe.ProbeFile, probe.Nonce, cancellationToken).ConfigureAwait(false);
                        probes.Add(probe);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        checks.Add(new NodeCheck($"root:{root.Path}", false, $"the server cannot write here: {e.Message}", root.Required));
                    }
                }
                else if (Directory.Exists(root.Path))
                {
                    var sample = FindSample(root.Path);
                    probes.Add(new RootProbe { Path = root.Path, SampleFile = sample?.Path, SampleSize = sample?.Size });
                }
                else if (root.Required)
                {
                    checks.Add(new NodeCheck($"root:{root.Path}", false, "missing on the server", true));
                }
                else
                {
                    // Optional and absent here: not in use on this server, nothing to prove.
                    continue;
                }

                required[root.Path] = root.Required;
            }

            var hardware = Options.HardwareTest();
            var skipHardware = hardware is null ? null : WhyNoHardwareTest(node, hardware);
            var request = new VerifyRequest
            {
                Roots = probes.ToArray(),
                HardwareTestArgs = skipHardware is null ? hardware?.Args ?? [] : [],
                HardwareType = hardware?.Type ?? "none",
            };
            var pending = new TaskCompletionSource<VerifyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            node.PendingVerify = pending;
            node.Send(Frame.Json(FrameType.Verify, request, ProtocolJson.Default.VerifyRequest));
            var result = await pending.Task.WaitAsync(VerifyTimeout, cancellationToken).ConfigureAwait(false);

            foreach (var probe in probes)
            {
                var answer = result.Roots.FirstOrDefault(r => string.Equals(r.Path, probe.Path, StringComparison.Ordinal));
                var ok = answer?.Ok == true;
                var detail = answer?.Detail ?? "no answer";
                if (ok && probe.Writable)
                {
                    (ok, detail) = await ReadReplyAsync(probe, cancellationToken).ConfigureAwait(false);
                }

                checks.Add(new NodeCheck($"root:{probe.Path}", ok, ok ? (probe.Writable ? "read/write" : "readable") : detail, required.GetValueOrDefault(probe.Path)));
                if (ok)
                {
                    verified.Add(probe.Path);
                }
            }

            var serverFfmpeg = Options.ServerFfmpegVersion();
            var match = FfmpegVersion.Compare(serverFfmpeg, node.FfmpegVersion);
            var incompatible = match == FfmpegMatch.Incompatible || (Options.StrictFfmpegVersion && match == FfmpegMatch.PatchDiffers);
            checks.Insert(0, new NodeCheck("ffmpeg", match == FfmpegMatch.Same, match == FfmpegMatch.Same
                ? node.FfmpegVersion
                : $"{match}: tentacle {node.FfmpegVersion}, server {serverFfmpeg}"));

            if (Options.ServerUid >= 0)
            {
                checks.Insert(1, new NodeCheck("uid", node.Uid == Options.ServerUid, node.Uid == Options.ServerUid
                    ? string.Create(CultureInfo.InvariantCulture, $"{node.Uid}")
                    : string.Create(CultureInfo.InvariantCulture, $"tentacle writes as uid {node.Uid}, the server runs as {Options.ServerUid}")));
            }

            var hardwareOk = false;
            if (hardware is not null && skipHardware is not null)
            {
                // Not a fault: this tentacle runs everything that uses no hardware.
                checks.Add(new NodeCheck($"hardware:{hardware.Type}", false, skipHardware, Required: false));
            }
            else if (hardware is not null)
            {
                hardwareOk = result.HardwareTested && result.HardwareOk;
                checks.Add(new NodeCheck($"hardware:{hardware.Type}", hardwareOk, hardwareOk ? "self-test passed" : result.HardwareDetail));
            }

            node.ApplyVerification(checks, verified, hardwareOk, incompatible);
            var failed = checks.Where(c => !c.Ok).Select(c => $"{c.Name}{(c.Required ? string.Empty : " [optional]")} ({c.Detail})").ToArray();
            LogVerified(node.Name, node.State, verified.Count, probes.Count, failed.Length == 0 ? "all checks passed" : string.Join("; ", failed));
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            LogVerifyFailed(node.Name, e.Message);
            node.NeedsVerify = true;
        }
        finally
        {
            foreach (var probe in probes.Where(p => p.Writable))
            {
                DeleteQuietly(probe.ProbeFile);
                DeleteQuietly(probe.ReplyFile);
            }

            node.PendingVerify = null;
            node.Verifying = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle {Node} registered (agent {Version}, {Ffmpeg}, {Slots} slots, {Background} background)")]
    private partial void LogRegistered(string node, string version, string ffmpeg, int slots, int background);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle {Node} disconnected")]
    private partial void LogUnregistered(string node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tentacle {Node} rejected: {Reason}")]
    private partial void LogRejected(string node, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shim connection from pid {Pid} uid {Uid} rejected: not the server's uid")]
    private partial void LogPeerRejected(int pid, int uid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shim connection rejected: its peer credentials (SO_PEERCRED) could not be read")]
    private partial void LogPeerUnidentified();

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle {Node} verified: {State}, {Shared}/{Probed} roots shared; {Summary}")]
    private partial void LogVerified(string node, NodeState state, int shared, int probed, string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tentacle {Node} verification failed: {Reason}")]
    private partial void LogVerifyFailed(string node, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tentacle {Node} is draining ({Jobs} jobs running), no new jobs")]
    private partial void LogDraining(string node, int jobs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tentacle {Node} failed repeatedly, cooling down until {Until}")]
    private partial void LogCooldown(string node, DateTimeOffset until);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tentacle {Node} authenticated with a token that is no longer valid (rotated), dropping it")]
    private partial void LogTokenRetired(string node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tentacle {Node} missed its heartbeats, dropping it")]
    private partial void LogHeartbeatLost(string node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job}: tentacle {Node} did not attach in time, running locally")]
    private partial void LogAttachTimeout(string job, string node);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Job} ({Kind}) running on tentacle {Node}")]
    private partial void LogRemote(string job, string kind, string node);

    [LoggerMessage(Level = LogLevel.Information, Message = "tentacle_job job={Job} kind={Kind} node={Node} reason={Reason} would_run_on={WouldRunOn} outcome={Outcome} exit={Code} duration_ms={DurationMs} nice={Nice} user={User} item={Item}")]
    private partial void LogEnded(string job, string kind, string node, string reason, string wouldRunOn, string outcome, int? code, long durationMs, int nice, string user, string item);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Job {Job}: could not tell who it is for: {Reason}")]
    private partial void LogAttributionFailed(string job, string reason);

    private sealed record PendingJob(string Key)
    {
        public TaskCompletionSource<WebSocket> Attached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WebSocket? Socket { get; init; }
    }
}
