using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Tentacle.Protocol;

namespace Tentacle.Cli;

/// <summary>
/// The ffmpeg/ffprobe shim Jellyfin runs. It asks the broker where the job goes:
/// locally it execs the real binary (same pid, so Jellyfin's Process *is* ffmpeg),
/// remotely it relays stdin, stdout, stderr and the exit code. Whenever the broker
/// is missing, slow or confused, it runs locally: Tentacle must never be the reason
/// a transcode fails, and Jellyfin validates ffmpeg before the broker exists.
/// </summary>
internal static class Shim
{
    /// <summary>
    /// Where the real jellyfin-ffmpeg binaries live, unless TENTACLE_REAL_DIR says otherwise.
    /// </summary>
    public const string DefaultRealDir = "/usr/lib/jellyfin-ffmpeg";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(250);
    // Long enough for a job waiting on a tentacle when the server does not fall back
    // (10 s, plus an attach); past it the shim runs ffmpeg here.
    private static readonly TimeSpan PlacementTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs the shim.
    /// </summary>
    /// <param name="mode">ffmpeg or ffprobe.</param>
    /// <param name="args">The arguments, without argv[0].</param>
    /// <returns>The exit code (only returns for remote jobs and failed execs).</returns>
    public static int Run(ToolMode mode, string[] args)
    {
        var binary = mode == ToolMode.Ffprobe ? "ffprobe" : "ffmpeg";
        var real = RealBinary(mode, Environment.GetEnvironmentVariable("TENTACLE_REAL_DIR"));

        if (mode == ToolMode.Ffprobe || IsDisabled() || !NeedsBroker(binary, args))
        {
            return ExecReal(real, args);
        }

        var socket = TryConnect(Environment.GetEnvironmentVariable("TENTACLE_SOCKET") ?? ProtocolInfo.DefaultSocketPath);
        if (socket is null)
        {
            return ExecReal(real, args);
        }

        var stream = new NetworkStream(socket, ownsSocket: true);
        Placement? placement;
        try
        {
            var start = new StartRequest
            {
                Binary = binary,
                Args = args,
                Cwd = Environment.CurrentDirectory,
                Env = CollectEnv(),
                Pid = Environment.ProcessId,
            };
            StreamFrames.Write(stream, Frame.Json(FrameType.Start, start, ProtocolJson.Default.StartRequest));
            socket.ReceiveTimeout = (int)PlacementTimeout.TotalMilliseconds;
            placement = StreamFrames.Read(stream) is { Type: FrameType.Placement } frame
                ? frame.Read(ProtocolJson.Default.Placement)
                : null;
            socket.ReceiveTimeout = 0;
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            placement = null;
        }

        if (placement is null)
        {
            Debug("no placement from the broker, running locally");
            stream.Dispose();
            return ExecReal(real, args);
        }

        if (placement.Refused.Length > 0)
        {
            // Jellyfin logs ffmpeg's stderr with the failed transcode.
            Console.Error.WriteLine($"tentacle: job refused: {placement.Refused}");
            stream.Dispose();
            return 1;
        }

        if (!placement.Remote)
        {
            if (placement.Nice > 0)
            {
                Native.SetNice(0, placement.Nice);
            }

            // Keep the socket open across exec: the broker sees EOF when ffmpeg exits.
            Native.ClearCloseOnExec((int)socket.SafeHandle.DangerousGetHandle());
            return ExecReal(real, args);
        }

        return Relay(stream, placement);
    }

    /// <summary>
    /// The real binary for a mode.
    /// </summary>
    /// <param name="mode">ffmpeg or ffprobe.</param>
    /// <param name="realDir">TENTACLE_REAL_DIR, if set.</param>
    /// <returns>The absolute path of the real binary.</returns>
    public static string RealBinary(ToolMode mode, string? realDir)
    {
        var dir = string.IsNullOrEmpty(realDir) ? DefaultRealDir : realDir;
        return Path.Combine(dir, mode == ToolMode.Ffprobe ? "ffprobe" : "ffmpeg");
    }

    /// <summary>
    /// Whether an invocation is worth a broker round-trip: real work, not
    /// `-version`/`-encoders` style queries or lavfi-only validation probes.
    /// </summary>
    /// <param name="binary">"ffmpeg" or "ffprobe".</param>
    /// <param name="args">The arguments.</param>
    /// <returns>True if the broker should place it.</returns>
    public static bool NeedsBroker(string binary, string[] args)
    {
        var analysis = ArgvAnalysis.Analyze(binary, args);
        return !analysis.InfoQuery && analysis.HasRealInput;
    }

    private static bool IsDisabled() => Environment.GetEnvironmentVariable("TENTACLE_DISABLE") == "1";

    private static Socket? TryConnect(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cts.Token).AsTask().GetAwaiter().GetResult();
            return socket;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            Debug($"broker unreachable at {path}: {e.Message}");
            socket.Dispose();
            return null;
        }
    }

    private static Dictionary<string, string> CollectEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            var value = (string?)entry.Value ?? string.Empty;
            if (EnvPolicy.IsAllowed(key, value))
            {
                env[key] = value;
            }
        }

        return env;
    }

    /// <summary>
    /// Mirrors a remote process: stdin to the broker byte by byte (Jellyfin's
    /// throttler writes bare 'p'/'u'/'c' keys, "q\n" stops), stdout/stderr back
    /// unbuffered with '\r' progress lines intact, signals forwarded, exit code kept.
    /// If Jellyfin SIGKILLs us the kernel closes the socket and the broker kills the job.
    /// </summary>
    private static int Relay(NetworkStream stream, Placement placement)
    {
        var sendLock = new Lock();
        void Send(Frame frame)
        {
            lock (sendLock)
            {
                StreamFrames.Write(stream, frame);
            }
        }

        var stdinThread = new Thread(() => PumpStdin(Send)) { IsBackground = true, Name = "stdin" };
        stdinThread.Start();

        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => Forward(ctx, 15));
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => Forward(ctx, 2));
        using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => Forward(ctx, 1));
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, ctx => Forward(ctx, 3));

        void Forward(PosixSignalContext context, int signal)
        {
            context.Cancel = true;
            try
            {
                Send(Frame.Json(FrameType.Signal, new SignalInfo { Signal = signal }, ProtocolJson.Default.SignalInfo));
            }
            catch (IOException)
            {
            }
        }

        using var stdout = Console.OpenStandardOutput();
        using var stderr = Console.OpenStandardError();
        var stdoutOpen = true;
        try
        {
            while (true)
            {
                var frame = StreamFrames.Read(stream);
                switch (frame?.Type)
                {
                    case null:
                        return Lost(stderr, placement, "broker connection closed");
                    case FrameType.Stdout when stdoutOpen:
                        try
                        {
                            stdout.Write(frame.Value.Payload.Span);
                            stdout.Flush();
                        }
                        catch (IOException)
                        {
                            stdoutOpen = false;
                            Send(Frame.Empty(FrameType.StdoutClosed));
                        }

                        break;
                    case FrameType.Stderr:
                        stderr.Write(frame.Value.Payload.Span);
                        stderr.Flush();
                        break;
                    case FrameType.Exit:
                        return frame.Value.Read(ProtocolJson.Default.ExitInfo).Code;
                    case FrameType.Error:
                        return Lost(stderr, placement, frame.Value.Read(ProtocolJson.Default.ErrorInfo).Message);
                    default:
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return Lost(stderr, placement, e.Message);
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static void PumpStdin(Action<Frame> send)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[Frame.MaxDataBytes];
            while (true)
            {
                var n = stdin.Read(buffer, 0, buffer.Length);
                if (n <= 0)
                {
                    send(Frame.Empty(FrameType.StdinEof));
                    return;
                }

                send(new Frame(FrameType.Stdin, buffer.AsSpan(0, n).ToArray()));
            }
        }
        catch (IOException)
        {
            // The job ended and the socket closed under us.
        }
    }

    private static int Lost(Stream stderr, Placement placement, string reason)
    {
        var line = System.Text.Encoding.UTF8.GetBytes($"[tentacle] job {placement.JobId} on {placement.Node} lost: {reason}\n");
        try
        {
            stderr.Write(line);
            stderr.Flush();
        }
        catch (IOException)
        {
        }

        return 1;
    }

    private static int ExecReal(string real, string[] args)
    {
        var argv = new List<string>(args.Length + 1) { real };
        argv.AddRange(args);
        var errno = Native.Exec(real, argv);

        // Like a shell: 127 when the binary is missing, 126 when it can't run.
        Console.Error.WriteLine($"[tentacle] cannot exec {real}: errno {errno}");
        return errno == 2 ? 127 : 126;
    }

    private static void Debug(string message)
    {
        if (Environment.GetEnvironmentVariable("TENTACLE_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[tentacle] {message}");
        }
    }
}
