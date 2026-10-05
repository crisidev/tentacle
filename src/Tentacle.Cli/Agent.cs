using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Tentacle.Protocol;

namespace Tentacle.Cli;

/// <summary>
/// The worker agent: one tentacle per GPU on this host (or one CPU-only tentacle).
/// Each keeps one control WebSocket to the broker (registration,
/// heartbeat, assignments) and one WebSocket per job carrying its stdio. The
/// invariant: no job outlives its connections. A dropped job socket or a lost
/// control session kills the process at once; the shim on the server then exits
/// non-zero and Jellyfin restarts the transcode, placed somewhere else.
/// </summary>
internal sealed class Agent : IDisposable
{
    /// <summary>
    /// Default status file, read by `tentacle agent-status`.
    /// </summary>
    public const string DefaultStatusPath = "/run/tentacle/agent.status";

    private const string JobEnv = "TENTACLE_JOB_ID";
    private const string NiceTool = "/usr/bin/nice";

    // The agent's environment jobs inherit (the operator's, not the broker's: the
    // broker's own share is EnvPolicy). Locale, paths, and GPU driver settings.
    private static readonly string[] InheritedNames =
    [
        "PATH", "HOME", "USER", "LANG", "LANGUAGE", "TZ", "TMPDIR", "LD_LIBRARY_PATH", "MALLOC_TRIM_THRESHOLD_",
        "XDG_RUNTIME_DIR", "NEOReadDebugKeys", "AMD_DEBUG", "DRI_PRIME", "ONEVPL_SEARCH_PATH",
    ];

    private static readonly string[] InheritedPrefixes =
    [
        "LC_", "LIBVA_", "NVIDIA_", "CUDA_", "__NV_", "__GL_", "OCL_ICD_", "VK_", "MESA_", "RADV_", "GALLIUM_", "INTEL_", "FONTCONFIG_",
    ];

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdef");
    private readonly ConcurrentDictionary<string, Process> _jobs = new(StringComparer.Ordinal);
    private readonly string _broker;
    private readonly BrokerTrust _trust;
    private readonly string _node;
    private readonly string _host;
    private readonly GpuInfo? _gpu;
    private readonly bool _detectOnly;
    private readonly string _realDir;
    private readonly StatusBoard _status;
    private readonly int _maxJobs;
    private readonly int _maxBackgroundJobs;
    private readonly double _weight;
    private readonly Sandbox? _sandbox;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    // Borrowed from ServeAsync, which owns and disposes it.
#pragma warning disable CA2213
    private ClientWebSocket? _control;
#pragma warning restore CA2213
    private volatile bool _draining;
    private string _token;
    private string _ffmpegVersion = string.Empty;

    private Agent(string broker, string token, BrokerTrust trust, string host, GpuInfo? gpu, string node, string realDir, StatusBoard status, int maxJobs, int maxBackgroundJobs, double weight, Sandbox? sandbox)
    {
        _sandbox = sandbox;
        _broker = broker.TrimEnd('/');
        _token = token;
        _trust = trust;
        _host = host;
        _gpu = gpu;
        _node = node;
        _detectOnly = maxJobs == 0;
        _realDir = realDir;
        _status = status;
        _maxJobs = maxJobs;
        _maxBackgroundJobs = maxBackgroundJobs;
        _weight = weight;
    }

    private bool Secure => _broker.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the agent until SIGTERM or SIGINT.
    /// </summary>
    /// <returns>The exit code.</returns>
    public static int Run()
    {
        var statusPath = Environment.GetEnvironmentVariable("TENTACLE_STATUS_FILE") ?? DefaultStatusPath;
        var broker = Environment.GetEnvironmentVariable("TENTACLE_BROKER_URL") ?? string.Empty;
        var token = ReadToken();
        var host = Environment.GetEnvironmentVariable("TENTACLE_NODE_NAME") ?? Environment.MachineName;
        var realDir = Environment.GetEnvironmentVariable("TENTACLE_REAL_DIR") ?? Shim.DefaultRealDir;

        // 0 slots: detect only. The tentacle registers, is verified and shows what it
        // found, and never receives a job.
        var maxJobs = IntEnv("TENTACLE_MAX_JOBS", 4, allowZero: true);
        var maxBackground = maxJobs == 0 ? 0 : IntEnv("TENTACLE_MAX_BACKGROUND_JOBS", Math.Max(1, maxJobs / 2));
        var drainSeconds = IntEnv("TENTACLE_DRAIN_SECONDS", 20);
        var weight = double.TryParse(Environment.GetEnvironmentVariable("TENTACLE_WEIGHT"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0 ? w : 1;

        // First SIGTERM/SIGINT drains (no new jobs, running ones get TENTACLE_DRAIN_SECONDS
        // to finish), a second one stops at once.
        using var stop = new CancellationTokenSource();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            if (!drain.TrySetResult())
            {
                stop.Cancel();
            }
        }

        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

        // s6 readiness (svc-jellyfin has notification-fd 3): the service is up once the
        // agent runs. Whether it reaches the broker is agent-status's business.
        NotifyReady(Environment.GetEnvironmentVariable("TENTACLE_NOTIFY_FD"));

        var status = new StatusBoard(statusPath);
        if (broker.Length == 0 || token.Length == 0)
        {
            WriteStatusFile(statusPath, "state=unconfigured");
            Log("TENTACLE_BROKER_URL and TENTACLE_TOKEN (or TENTACLE_TOKEN_FILE) are required; idling");
            Task.WaitAny(drain.Task, Task.Delay(Timeout.Infinite, stop.Token));
            Log("agent stopped");
            return 0;
        }

        BrokerTrust trust;
        try
        {
            trust = BrokerTrust.FromEnvironment();
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.Security.Cryptography.CryptographicException)
        {
            WriteStatusFile(statusPath, "state=misconfigured");
            Log($"TLS settings: {e.Message}; idling");
            Task.WaitAny(drain.Task, Task.Delay(Timeout.Infinite, stop.Token));
            Log("agent stopped");
            return 0;
        }

        // ws:// sends the token in the clear, so it takes an explicit opt-in, and never
        // goes with a fingerprint or CA file (those mean the broker uses TLS).
        if (PlainWsRefusal(broker, trust, Environment.GetEnvironmentVariable("TENTACLE_ALLOW_PLAIN_WS")) is { } refusal)
        {
            WriteStatusFile(statusPath, "state=misconfigured");
            Log($"{refusal}; idling");
            Task.WaitAny(drain.Task, Task.Delay(Timeout.Infinite, stop.Token));
            Log("agent stopped");
            return 0;
        }

        // The broker chooses every job's command line; TENTACLE_ROOTS is where the
        // agent draws the line. Set, it is enforced or nothing runs.
        Sandbox? sandbox;
        try
        {
            sandbox = Sandbox.Create(Environment.GetEnvironmentVariable("TENTACLE_ROOTS"));
        }
        catch (ArgumentException e)
        {
            WriteStatusFile(statusPath, "state=misconfigured");
            Log($"{e.Message}; idling");
            Task.WaitAny(drain.Task, Task.Delay(Timeout.Infinite, stop.Token));
            Log("agent stopped");
            return 0;
        }

        Log(sandbox is null
            ? "warning: TENTACLE_ROOTS is not set, so jobs are not confined: whoever controls the broker can read and write any file this user can"
            : $"jobs confined: {sandbox.Describe()}");
        if (sandbox is { Abi: < 6 })
        {
            Log($"warning: this kernel's Landlock (ABI {sandbox.Abi}) confines jobs only partly: {Sandbox.Protections(sandbox.Abi)}");
        }

        KillOrphans();
        var ffmpeg = Path.Combine(realDir, "ffmpeg");
        var gpus = GpuProbe.DetectAsync(ffmpeg, Environment.GetEnvironmentVariable("TENTACLE_GPUS"), Log, stop.Token).GetAwaiter().GetResult();
        Log($"host {host}: {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}, {GpuProbe.CpuModel()}, kernel {GpuProbe.Kernel()}, {gpus.Count} GPU(s)");
        foreach (var gpu in gpus)
        {
            Log("  " + GpuProbe.Describe(gpu));
        }

        // One tentacle per GPU, named after the host when there is only one (the
        // usual case, and what dashboards and alerts already know), else host/renderD129.
        var agents = (gpus.Count == 0 ? [null] : gpus.Cast<GpuInfo?>().ToArray())
            .Select(gpu => new Agent(broker, token, trust, host, gpu, gpus.Count > 1 ? $"{host}/{Path.GetFileName(gpu!.Device)}" : host, realDir, status, maxJobs, maxBackground, weight, sandbox))
            .ToArray();
        try
        {
            var running = Task.WhenAll(agents.Select(a => a.RunAsync(stop.Token)));
            _ = drain.Task.ContinueWith(
                async _ =>
                {
                    await Task.WhenAll(agents.Select(a => a.DrainAsync(TimeSpan.FromSeconds(drainSeconds), stop.Token))).ConfigureAwait(false);
                    await stop.CancelAsync().ConfigureAwait(false);
                },
                TaskScheduler.Default).Unwrap();
            running.GetAwaiter().GetResult();
        }
        finally
        {
            foreach (var agent in agents)
            {
                agent.Dispose();
            }
        }

        Log("agent stopped");
        return 0;
    }

    /// <summary>
    /// `tentacle agent-status`: 0 while the agent is registered with the broker.
    /// </summary>
    /// <returns>The exit code.</returns>
    public static int Status()
    {
        var statusPath = Environment.GetEnvironmentVariable("TENTACLE_STATUS_FILE") ?? DefaultStatusPath;
        if (!File.Exists(statusPath))
        {
            Console.Error.WriteLine($"[tentacle] no status at {statusPath}");
            return 1;
        }

        // One line per tentacle; healthy when every one is registered.
        var status = File.ReadAllText(statusPath).Trim();
        Console.Out.WriteLine(status);
        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && lines.All(l => l.StartsWith("state=ready", StringComparison.Ordinal)) ? 0 : 1;
    }

    /// <inheritdoc />
    public void Dispose() => _controlLock.Dispose();

    /// <summary>
    /// Why the agent must not connect to a ws:// broker, or null when it may: the
    /// token would cross the network in the clear, so that takes
    /// TENTACLE_ALLOW_PLAIN_WS=true, and a fingerprint or CA file says the broker
    /// uses TLS, so the URL is a mistake.
    /// </summary>
    /// <param name="broker">TENTACLE_BROKER_URL.</param>
    /// <param name="trust">The TLS trust settings.</param>
    /// <param name="allow">TENTACLE_ALLOW_PLAIN_WS.</param>
    /// <returns>The refusal, or null.</returns>
    internal static string? PlainWsRefusal(string broker, BrokerTrust trust, string? allow)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(trust);
        if (broker.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trust.Mode != "system")
        {
            return $"TENTACLE_BROKER_URL must be wss:// with {(trust.Mode == "pinned" ? "TENTACLE_BROKER_FINGERPRINT" : "TENTACLE_CA_FILE")} set";
        }

        return string.Equals(allow, "true", StringComparison.OrdinalIgnoreCase)
            ? null
            : "TENTACLE_BROKER_URL must be wss:// (the broker's default): plain ws:// would send the token in cleartext; set TENTACLE_ALLOW_PLAIN_WS=true if the broker's TLS is off on purpose";
    }

    private static int IntEnv(string name, int fallback, bool allowZero = false)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && (v > 0 || (allowZero && v == 0)) ? v : fallback;

    /// <summary>
    /// The GPU device nodes visible in this container.
    /// </summary>
    private static string[] Devices()
    {
        var devices = new System.Collections.Generic.List<string>();
        try
        {
            if (Directory.Exists("/dev/dri"))
            {
                devices.AddRange(Directory.EnumerateFileSystemEntries("/dev/dri", "renderD*"));
            }

            devices.AddRange(Directory.EnumerateFileSystemEntries("/dev", "nvidia[0-9]*"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        devices.Sort(StringComparer.Ordinal);
        return devices.ToArray();
    }

    private static string ReadToken()
    {
        var token = Environment.GetEnvironmentVariable("TENTACLE_TOKEN");
        if (!string.IsNullOrEmpty(token))
        {
            return token.Trim();
        }

        var file = Environment.GetEnvironmentVariable("TENTACLE_TOKEN_FILE");
        return !string.IsNullOrEmpty(file) && File.Exists(file) ? File.ReadAllText(file).Trim() : string.Empty;
    }

    private static void Log(string message) => Console.Error.WriteLine($"[tentacle] {message}");

    private static void WriteStatusFile(string path, string status)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, status + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"cannot write {path}: {e.Message}");
        }
    }

    private static void NotifyReady(string? fdValue)
    {
        if (!int.TryParse(fdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var fd))
        {
            return;
        }

        try
        {
            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            using var stream = new FileStream(handle, FileAccess.Write, 1);
            stream.WriteByte((byte)'\n');
        }
        catch (IOException e)
        {
            Log($"readiness fd {fd}: {e.Message}");
        }
    }

    /// <summary>
    /// A job process left behind by a previous agent (crash, OOM kill) would write
    /// segments nobody wants. Kill every process of ours that carries a job id.
    /// </summary>
    private static void KillOrphans()
    {
        var self = Environment.ProcessId;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == self)
            {
                continue;
            }

            try
            {
                var environ = File.ReadAllBytes(Path.Combine(dir, "environ"));
                if (Encoding.UTF8.GetString(environ).Contains(JobEnv + "=", StringComparison.Ordinal) && Native.SendSignal(pid, 9))
                {
                    Log($"killed orphaned job process {pid}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not ours, or already gone.
            }
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        _ffmpegVersion = await FfmpegVersionAsync().ConfigureAwait(false);
        Log($"agent {ProtocolInfo.ProductVersion} on {_node}: {_ffmpegVersion}, {(_detectOnly ? "detect only (no jobs)" : $"{_maxJobs} slots")}, broker {_broker}");
        Log(Secure
            ? $"TLS: broker certificate checked by {_trust.Mode} trust"
            : "warning: plain ws:// — the token crosses the network in cleartext; use wss:// (the broker's default)");

        var backoff = TimeSpan.FromMilliseconds(500);
        while (!stop.IsCancellationRequested)
        {
            WriteStatus("connecting");
            var unauthorized = false;

            // Re-read on every attempt: a rotated token file (a k8s Secret) applies
            // at the next reconnect, which a rotation on the server triggers.
            if (ReadToken() is { Length: > 0 } fresh)
            {
                _token = fresh;
            }

            try
            {
                await ServeAsync(() => backoff = TimeSpan.FromMilliseconds(500), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                unauthorized = e is WebSocketException && e.Message.Contains("401", StringComparison.Ordinal);
                Log(unauthorized ? "broker rejected the token (401)" : $"broker connection: {e.Message}{Hint()}");
            }

            // Invariant: no job outlives its control session.
            _control = null;
            KillAllJobs("control session lost");
            WriteStatus("disconnected");

            var delay = unauthorized ? TimeSpan.FromSeconds(60) : backoff + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            try
            {
                await Task.Delay(delay, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        KillAllJobs("agent stopping");
        WriteStatus("stopped");
    }

    private async Task ServeAsync(Action connected, CancellationToken stop)
    {
        using var socket = NewSocket();
        await socket.ConnectAsync(new Uri($"{_broker}/tentacle/v1/control"), stop).ConfigureAwait(false);
        if (socket.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            throw new WebSocketException("401");
        }

        var hello = new Hello
        {
            Node = _node,
            InstanceId = _instanceId,
            FfmpegVersion = _ffmpegVersion,
            MaxJobs = _maxJobs,
            MaxBackgroundJobs = _maxBackgroundJobs,
            Weight = _weight,
            Uid = Native.CurrentUid(),
            Devices = Devices(),
            Cores = Environment.ProcessorCount,
            Host = _host,
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Kernel = GpuProbe.Kernel(),
            CpuModel = GpuProbe.CpuModel(),
            DetectOnly = _detectOnly,
            Gpu = _gpu,
            Sandbox = _sandbox?.Describe() ?? string.Empty,
        };
        await SocketFrames.SendAsync(socket, Frame.Json(FrameType.Hello, hello, ProtocolJson.Default.Hello), stop).ConfigureAwait(false);

        var answer = await SocketFrames.ReceiveAsync(socket, stop).ConfigureAwait(false);
        if (answer is { Type: FrameType.Reject } reject)
        {
            throw new InvalidOperationException("rejected: " + reject.Read(ProtocolJson.Default.ErrorInfo).Message);
        }

        if (answer is not { Type: FrameType.Welcome } welcomeFrame)
        {
            throw new InvalidDataException("no welcome from the broker");
        }

        var welcome = welcomeFrame.Read(ProtocolJson.Default.Welcome);
        if (welcome.ServerFfmpegVersion.Length > 0 && !string.Equals(welcome.ServerFfmpegVersion, _ffmpegVersion, StringComparison.Ordinal))
        {
            Log($"warning: server runs {welcome.ServerFfmpegVersion}, this node {_ffmpegVersion}");
        }

        connected();
        _control = socket;
        WriteStatus("ready");
        Log($"registered with the broker (protocol {welcome.Protocol})");
        if (_draining)
        {
            await SendControlAsync(Frame.Empty(FrameType.Draining)).ConfigureAwait(false);
        }

        var heartbeat = TimeSpan.FromMilliseconds(Math.Max(1000, welcome.HeartbeatMs));
        while (true)
        {
            using var receive = CancellationTokenSource.CreateLinkedTokenSource(stop);
            receive.CancelAfter(heartbeat * 3);
            Frame? frame;
            try
            {
                frame = await SocketFrames.ReceiveAsync(socket, receive.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                throw new TimeoutException("no heartbeat from the broker");
            }

            switch (frame?.Type)
            {
                case null:
                    throw new WebSocketException("broker closed the control connection");
                case FrameType.Ping:
                    var load = new AgentLoad { Jobs = _jobs.Count };
                    await SendControlAsync(Frame.Json(FrameType.Pong, load, ProtocolJson.Default.AgentLoad)).ConfigureAwait(false);
                    break;
                case FrameType.Verify:
                    var verify = frame.Value.Read(ProtocolJson.Default.VerifyRequest);
                    _ = Task.Run(() => VerifyAsync(verify, stop), CancellationToken.None);
                    break;
                case FrameType.Assign:
                    var assign = frame.Value.Read(ProtocolJson.Default.Assign);
                    _ = Task.Run(() => RunJobAsync(assign, stop), CancellationToken.None);
                    break;
                case FrameType.Cancel:
                    Kill(frame.Value.Read(ProtocolJson.Default.CancelJob).JobId, "cancelled by the broker");
                    break;
                default:
                    break;
            }
        }
    }

    private async Task RunJobAsync(Assign assign, CancellationToken stop)
    {
        using var socket = NewSocket();
        var started = Stopwatch.StartNew();
        try
        {
            var url = $"{_broker}/tentacle/v1/job/{Uri.EscapeDataString(assign.JobId)}?key={Uri.EscapeDataString(assign.JobKey)}";
            await socket.ConnectAsync(new Uri(url), stop).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            Log($"job {assign.JobId}: cannot attach: {e.Message}");
            return;
        }

        using var sendLock = new SemaphoreSlim(1, 1);
        async Task Send(Frame frame)
        {
            await sendLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await SocketFrames.SendAsync(socket, frame, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        if (!Directory.Exists(assign.Cwd))
        {
            await Fail(Send, $"working directory {assign.Cwd} does not exist on {_node}").ConfigureAwait(false);
            await CloseQuietly(socket).ConfigureAwait(false);
            return;
        }

        // Background jobs start through nice(1) or the sandbox launcher, so ffmpeg runs at
        // that priority from its first instruction (setpriority after start would leave
        // a window at nice 0).
        var real = Path.Combine(_realDir, assign.Binary == "ffprobe" ? "ffprobe" : "ffmpeg");
        var niceTool = _sandbox is null && assign.Nice > 0 && File.Exists(NiceTool) ? NiceTool : null;
        var start = JobStart(real, assign.Nice, niceTool);
        start.RedirectStandardInput = true;
        start.WorkingDirectory = assign.Cwd;
        foreach (var arg in PointAtGpu(assign.Args))
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in EnvPolicy.Filter(assign.Env))
        {
            start.Environment[key] = value;
        }

        start.Environment[JobEnv] = assign.JobId;

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await Fail(Send, $"cannot start ffmpeg on {_node}: {e.Message}").ConfigureAwait(false);
            await CloseQuietly(socket).ConfigureAwait(false);
            return;
        }

        _jobs[assign.JobId] = process;
        if (assign.Nice > 0 && niceTool is null && _sandbox is null)
        {
            Native.SetNice(process.Id, assign.Nice);
        }

        Log($"job {assign.JobId} started (pid {process.Id}{(assign.Nice > 0 ? $", nice {assign.Nice}" : string.Empty)})");
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        try
        {
            await Send(Frame.Json(FrameType.Started, new StartedInfo { Node = _node, Pid = process.Id }, ProtocolJson.Default.StartedInfo)).ConfigureAwait(false);

            stdout = Pump(process.StandardOutput.BaseStream, FrameType.Stdout, Send);
            stderr = Pump(process.StandardError.BaseStream, FrameType.Stderr, Send);
            var input = PumpInput(socket, process);

            var exited = process.WaitForExitAsync(CancellationToken.None);
            if (await Task.WhenAny(exited, input).ConfigureAwait(false) == input)
            {
                // The job socket closed first: the shim is gone, so is the job.
                KillProcess(process);
                await exited.ConfigureAwait(false);
                return;
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var exit = new ExitInfo { Code = process.ExitCode, DurationMs = started.ElapsedMilliseconds };
            await Send(Frame.Json(FrameType.Exit, exit, ProtocolJson.Default.ExitInfo)).ConfigureAwait(false);
            Log($"job {assign.JobId} exited {exit.Code} after {exit.DurationMs} ms");
            await CloseQuietly(socket).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException)
        {
            Log($"job {assign.JobId}: connection lost ({e.Message}), killing it");
            KillProcess(process);
        }
        finally
        {
            _jobs.TryRemove(assign.JobId, out _);

            // Process.Dispose leaves redirected streams open once they were used:
            // close the three pipes here, or each job leaks them until a GC.
            CloseStreams(process);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How a job (or the hardware self-test) starts: through the sandbox launcher when
    /// TENTACLE_ROOTS is set, else through nice(1) or directly. The job's arguments
    /// follow. It inherits only the agent's locale, paths and GPU driver settings (see InheritedByJobs): not the token, nor anything else the container was given.
    /// </summary>
    private ProcessStartInfo JobStart(string binary, int nice, string? niceTool)
    {
        ProcessStartInfo start;
        if (_sandbox is not null)
        {
            start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("no path to this executable"));
            foreach (var arg in _sandbox.Wrap(start.FileName, binary, nice))
            {
                start.ArgumentList.Add(arg);
            }
        }
        else if (niceTool is not null)
        {
            start = new ProcessStartInfo(niceTool);
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(nice.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(binary);
        }
        else
        {
            start = new ProcessStartInfo(binary);
        }

        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        foreach (var key in start.Environment.Keys.Where(k => !InheritedByJobs(k)).ToArray())
        {
            start.Environment.Remove(key);
        }

        return start;
    }

    /// <summary>
    /// Whether a job inherits this variable from the agent. Only what ffmpeg and the
    /// GPU drivers read: anything else the container was given (the token, other
    /// credentials, the s6 and mod settings) stays out of jobs' reach.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <returns>True if jobs get it.</returns>
    internal static bool InheritedByJobs(string name)
        => !name.StartsWith("TENTACLE_", StringComparison.Ordinal)
            && (InheritedNames.Contains(name, StringComparer.Ordinal)
                || InheritedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)));

    private static void CloseStreams(Process process)
    {
        foreach (var close in new Action[] { process.StandardInput.Dispose, process.StandardOutput.Dispose, process.StandardError.Dispose })
        {
            try
            {
                close();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed (StdinEof, StdoutClosed) or ffmpeg is gone: nothing to flush.
            }
        }
    }

    private static async Task Pump(Stream source, FrameType type, Func<Frame, Task> send)
    {
        var buffer = new byte[Frame.MaxDataBytes];
        try
        {
            int n;
            while ((n = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await send(new Frame(type, buffer.AsSpan(0, n).ToArray())).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or IOException or WebSocketException or OperationCanceledException)
        {
            // Closed on purpose (StdoutClosed, job over) or the job socket is gone.
        }
    }

    /// <summary>
    /// Job socket → process: stdin bytes, EOF, signals. Completes when the socket closes.
    /// </summary>
    private static async Task PumpInput(WebSocket socket, Process process)
    {
        var stdin = process.StandardInput.BaseStream;
        try
        {
            while (await SocketFrames.ReceiveAsync(socket, CancellationToken.None).ConfigureAwait(false) is { } frame)
            {
                try
                {
                    switch (frame.Type)
                    {
                        case FrameType.Stdin:
                            await stdin.WriteAsync(frame.Payload).ConfigureAwait(false);
                            await stdin.FlushAsync().ConfigureAwait(false);
                            break;
                        case FrameType.StdinEof:
                            stdin.Close();
                            break;
                        case FrameType.StdoutClosed:
                            process.StandardOutput.BaseStream.Close();
                            break;
                        case FrameType.Signal:
                            Native.SendSignal(process.Id, frame.Read(ProtocolJson.Default.SignalInfo).Signal);
                            break;
                        default:
                            break;
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // ffmpeg closed stdin or already exited; its exit status says the rest.
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException)
        {
        }
    }

    private static async Task Fail(Func<Frame, Task> send, string message)
    {
        Log(message);
        try
        {
            await send(Frame.Json(FrameType.Error, new ErrorInfo { Message = message }, ProtocolJson.Default.ErrorInfo)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or IOException)
        {
        }
    }

    private static async Task CloseQuietly(WebSocket socket)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>
    /// Sends a frame on the control socket, serialized with the other senders.
    /// </summary>
    private async Task SendControlAsync(Frame frame)
    {
        if (_control is not { } socket)
        {
            return;
        }

        await _controlLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SocketFrames.SendAsync(socket, frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            // The control loop notices the broken socket.
        }
        finally
        {
            _controlLock.Release();
        }
    }

    /// <summary>
    /// SIGTERM (k8s rolling update, node drain): tell the broker to send nothing new,
    /// give the running jobs a moment, then stop. Jobs still running then are killed:
    /// their shims exit non-zero and Jellyfin restarts them on another node.
    /// </summary>
    private async Task DrainAsync(TimeSpan grace, CancellationToken stop)
    {
        _draining = true;
        WriteStatus("draining");
        Log($"draining: {_jobs.Count} jobs running, waiting up to {grace.TotalSeconds:0} s");
        await SendControlAsync(Frame.Empty(FrameType.Draining)).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + grace;
        while (!_jobs.IsEmpty && DateTimeOffset.UtcNow < deadline && !stop.IsCancellationRequested)
        {
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
        }

        if (!_jobs.IsEmpty)
        {
            Log($"drain timeout: killing {_jobs.Count} jobs");
        }
    }

    /// <summary>
    /// Answers a verification: read each probe the server wrote and write the nonce
    /// back, stat the sample of each read-only root, run the hardware self-test.
    /// </summary>
    private async Task VerifyAsync(VerifyRequest request, CancellationToken stop)
    {
        var results = new System.Collections.Generic.List<RootResult>();
        foreach (var root in request.Roots)
        {
            // The agent is not confined itself, so it works on the real path: symlinks
            // followed and ".." resolved, never the broker's string as given.
            var real = Native.RealPath(root.Path);

            // A root outside TENTACLE_ROOTS is one jobs cannot reach: say so, and the
            // broker sends nothing there. The agent's own probe files stay inside too.
            results.Add(_sandbox is { } sandbox && (real is null || !sandbox.Allows(real))
                ? new RootResult { Path = root.Path, Detail = real is null ? "missing on the tentacle" : "not in this tentacle's TENTACLE_ROOTS" }
                : await VerifyRootAsync(root, real, stop).ConfigureAwait(false));
        }

        var result = new VerifyResult { Roots = results.ToArray() };
        if (request.HardwareTestArgs.Length > 0)
        {
            (result.HardwareOk, result.HardwareDetail) = await RunHardwareTestAsync(PointAtGpu(request.HardwareTestArgs), stop).ConfigureAwait(false);
            result.HardwareTested = true;
        }

        var shared = results.FindAll(r => r.Ok).Count;
        Log($"verified: {shared}/{results.Count} roots shared, hardware {request.HardwareType}: {(result.HardwareTested ? (result.HardwareOk ? "ok" : "FAILED") : "not used")}");
        foreach (var failed in results.FindAll(r => !r.Ok))
        {
            Log($"  not shared: {failed.Path}: {failed.Detail}");
        }

        await SendControlAsync(Frame.Json(FrameType.VerifyResult, result, ProtocolJson.Default.VerifyResult)).ConfigureAwait(false);
    }

    /// <summary>
    /// Proves one root. <paramref name="real"/> is its resolved path: every file the
    /// agent touches is directly inside it, named by the broker but checked here.
    /// </summary>
    /// <param name="root">What the broker asked.</param>
    /// <param name="real">The root's real path, or null when it does not exist.</param>
    /// <param name="stop">Cancelled when the agent stops.</param>
    /// <returns>The result, under the path the broker asked about.</returns>
    internal static async Task<RootResult> VerifyRootAsync(RootProbe root, string? real, CancellationToken stop)
    {
        var result = new RootResult { Path = root.Path };
        try
        {
            if (real is null || !Directory.Exists(real))
            {
                result.Detail = "missing on the tentacle";
                return result;
            }

            if (root.Writable)
            {
                // Only ever touch Tentacle's own files directly inside the root: the
                // broker names them, so a confused or hostile broker must not be able
                // to make the agent read or write anywhere else.
                if (!IsProbeFile(root.Path, root.ProbeFile, ".tentacle-probe-") || !IsProbeFile(root.Path, root.ReplyFile, ".tentacle-reply-"))
                {
                    result.Detail = "refused: probe files must be .tentacle-* directly inside the root";
                    return result;
                }

                var probeFile = Path.Combine(real, Path.GetFileName(root.ProbeFile!));
                var replyFile = Path.Combine(real, Path.GetFileName(root.ReplyFile!));

                // The probe may take a moment to become visible over NFS.
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                string? nonce = null;
                while (nonce is null)
                {
                    try
                    {
                        nonce = (await File.ReadAllTextAsync(probeFile, stop).ConfigureAwait(false)).Trim();
                    }
                    catch (FileNotFoundException) when (DateTimeOffset.UtcNow < deadline)
                    {
                        await Task.Delay(250, stop).ConfigureAwait(false);
                    }
                }

                if (!string.Equals(nonce, root.Nonce, StringComparison.Ordinal))
                {
                    result.Detail = "the probe here is not the server's: a different directory at the same path";
                    return result;
                }

                // CreateNew is O_CREAT|O_EXCL: it fails on an existing file and never follows a symlink.
                var stream = new FileStream(replyFile, FileMode.CreateNew, FileAccess.Write);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(nonce), stop).ConfigureAwait(false);
                }

                result.Ok = true;
                return result;
            }

            if (root.SampleFile is { } sample)
            {
                // Only a file inside this root: the broker must not learn what exists elsewhere.
                var realSample = Native.RealPath(sample);
                if (realSample is null
                    ? !Tentacle.Protocol.ArgvAnalysis.IsUnder(sample, root.Path)
                    : !Tentacle.Protocol.ArgvAnalysis.IsUnder(realSample, real))
                {
                    result.Detail = "refused: the sample file is not inside the root";
                    return result;
                }

                var info = new FileInfo(realSample ?? sample);
                result.Ok = info.Exists && info.Length == root.SampleSize;
                result.Detail = result.Ok ? string.Empty : info.Exists ? $"{sample} differs in size" : $"{sample} not visible";
                return result;
            }

            result.Ok = true;
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result.Detail = e.Message;
            return result;
        }
    }

    private static bool IsProbeFile(string root, string? file, string prefix)
    {
        if (string.IsNullOrEmpty(file))
        {
            return false;
        }

        var name = Path.GetFileName(file);
        return string.Equals(Path.GetDirectoryName(file), root.TrimEnd('/'), StringComparison.Ordinal)
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && name.Length > prefix.Length
            && name.AsSpan(prefix.Length).IndexOfAnyExcept(HexDigits) < 0;
    }

    private async Task<(bool Ok, string Detail)> RunHardwareTestAsync(string[] args, CancellationToken stop)
    {
        var start = JobStart(Path.Combine(_realDir, "ffmpeg"), 0, null);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync(stop);
            _ = process.StandardOutput.ReadToEndAsync(stop);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                return (false, "hardware self-test timed out");
            }

            var text = (await stderr.ConfigureAwait(false)).Trim();
            var tail = text.Length > 400 ? text[^400..] : text;
            return process.ExitCode == 0 ? (true, string.Empty) : (false, $"exit {process.ExitCode}: {tail}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (false, e.Message);
        }
    }

    private string Hint()
    {
        if (_trust.LastFailure is { } failure)
        {
            _trust.ClearFailure();
            return $" ({failure})";
        }

        return Secure ? string.Empty : " (if the broker uses TLS, its default, the URL must be wss://)";
    }

    private ClientWebSocket NewSocket()
    {
        var socket = new ClientWebSocket();
        if (Secure)
        {
            socket.Options.RemoteCertificateValidationCallback = _trust.Validate;
        }

        socket.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.CollectHttpResponseDetails = true;
        return socket;
    }

    private void Kill(string jobId, string why)
    {
        if (_jobs.TryGetValue(jobId, out var process))
        {
            Log($"job {jobId}: {why}");
            KillProcess(process);
        }
    }

    private void KillAllJobs(string why)
    {
        foreach (var jobId in _jobs.Keys)
        {
            Kill(jobId, why);
        }
    }

    private void WriteStatus(string state)
        => _status.Set(_node, $"state={state} node={_node} broker={_broker} tls={(Secure ? _trust.Mode : "off")} jobs={_jobs.Count}{(_detectOnly ? " detect-only" : string.Empty)}");

    /// <summary>
    /// Jellyfin names its own render node in the command line; run it on this
    /// tentacle's GPU instead (the same node on a one-GPU host, so usually a no-op).
    /// </summary>
    private string[] PointAtGpu(string[] args) => _gpu is { } gpu ? Hardware.PointAt(args, gpu.Device) : args;

    private async Task<string> FfmpegVersionAsync()
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(_realDir, "ffmpeg"), "-version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start)!;
            var first = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
            await process.WaitForExitAsync().ConfigureAwait(false);

            // Test hook: pretend to run another ffmpeg build (version-gate tests).
            return Environment.GetEnvironmentVariable("TENTACLE_FAKE_FFMPEG_VERSION") is { Length: > 0 } fake ? fake : first.Trim();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Log($"cannot run ffmpeg -version: {e.Message}");
            return "unknown";
        }
    }

    /// <summary>
    /// The status file holds one line per tentacle of this host.
    /// </summary>
    private sealed class StatusBoard(string path)
    {
        private readonly ConcurrentDictionary<string, string> _lines = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();

        public void Set(string node, string line)
        {
            lock (_lock)
            {
                _lines[node] = line;
                WriteStatusFile(path, string.Join('\n', _lines.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => l.Value)));
            }
        }
    }
}
