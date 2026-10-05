using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Tentacle.Protocol;

/// <summary>
/// Shim → broker: an ffmpeg/ffprobe invocation Jellyfin made.
/// </summary>
public sealed class StartRequest : IValidated
{
    /// <summary>Gets or sets the shim's protocol version.</summary>
    public int Protocol { get; set; } = ProtocolInfo.Version;

    /// <summary>Gets or sets the binary: "ffmpeg" or "ffprobe".</summary>
    public string Binary { get; set; } = "ffmpeg";

    /// <summary>Gets or sets the arguments, without argv[0].</summary>
    public string[] Args { get; set; } = [];

    /// <summary>Gets or sets the working directory (one attachment extraction relies on it).</summary>
    public string Cwd { get; set; } = "/";

    /// <summary>Gets or sets the allow-listed environment to recreate remotely.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Gets or sets the shim's pid (the pid Jellyfin sees).</summary>
    public int Pid { get; set; }

    /// <inheritdoc />
    public void Validate()
    {
        Validation.NoNulls(Args, nameof(Args));
        Validation.NoNulls(Env, nameof(Env));
    }
}

/// <summary>
/// Broker → shim: where the job runs.
/// </summary>
public sealed class Placement
{
    /// <summary>Gets or sets a value indicating whether the job runs on a tentacle.</summary>
    public bool Remote { get; set; }

    /// <summary>Gets or sets the job id.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Gets or sets the node name ("local" for the server).</summary>
    public string Node { get; set; } = "local";

    /// <summary>Gets or sets why this placement was chosen.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets the nice value to run with (background jobs run at 10).</summary>
    public int Nice { get; set; }

    /// <summary>Gets or sets why the job must not run at all, or empty: no tentacle took it and the server does not fall back.</summary>
    public string Refused { get; set; } = string.Empty;
}

/// <summary>
/// Agent → shim: the remote process started.
/// </summary>
public sealed class StartedInfo
{
    /// <summary>Gets or sets the node.</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>Gets or sets the remote pid.</summary>
    public int Pid { get; set; }
}

/// <summary>
/// Shim → agent: a signal for the process.
/// </summary>
public sealed class SignalInfo
{
    /// <summary>Gets or sets the Linux signal number.</summary>
    public int Signal { get; set; }
}

/// <summary>
/// Agent → shim: the process exited.
/// </summary>
public sealed class ExitInfo
{
    /// <summary>Gets or sets the exit code, 128+n for a death by signal n (like a shell, and like .NET's ExitCode).</summary>
    public int Code { get; set; }

    /// <summary>Gets or sets the run time in milliseconds.</summary>
    public long DurationMs { get; set; }
}

/// <summary>
/// A failure outside the process.
/// </summary>
public sealed class ErrorInfo
{
    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Agent → broker: registration.
/// </summary>
public sealed class Hello : IValidated
{
    /// <summary>Gets or sets the lowest protocol the agent speaks.</summary>
    public int ProtocolMin { get; set; } = ProtocolInfo.Version;

    /// <summary>Gets or sets the highest protocol the agent speaks.</summary>
    public int ProtocolMax { get; set; } = ProtocolInfo.Version;

    /// <summary>Gets or sets the agent version.</summary>
    public string AgentVersion { get; set; } = ProtocolInfo.ProductVersion;

    /// <summary>Gets or sets the node name.</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>Gets or sets a random id for this agent process, to tell restarts apart.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the first line of `ffmpeg -version` on the node.</summary>
    public string FfmpegVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets how many jobs the node accepts at once.</summary>
    public int MaxJobs { get; set; }

    /// <summary>Gets or sets the number of CPUs.</summary>
    public int Cores { get; set; }

    /// <summary>Gets or sets how many slots background jobs (trickplay, analysis) may use.</summary>
    public int MaxBackgroundJobs { get; set; }

    /// <summary>Gets or sets the scheduling weight: a node with weight 2 takes twice the load.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Gets or sets the uid the agent (and so ffmpeg) runs as.</summary>
    public int Uid { get; set; }

    /// <summary>Gets or sets the GPU device nodes the agent can see.</summary>
    public string[] Devices { get; set; } = [];

    /// <summary>Gets or sets the host the agent runs on; one host registers one tentacle per GPU.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the CPU architecture (x64, arm64).</summary>
    public string Arch { get; set; } = string.Empty;

    /// <summary>Gets or sets the kernel release.</summary>
    public string Kernel { get; set; } = string.Empty;

    /// <summary>Gets or sets the CPU (or board) model.</summary>
    public string CpuModel { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the tentacle only reports what it found and never takes jobs.</summary>
    public bool DetectOnly { get; set; }

    /// <summary>Gets or sets the GPU this tentacle stands for, or null for a CPU-only tentacle.</summary>
    public GpuInfo? Gpu { get; set; }

    /// <summary>Gets or sets how jobs are confined (Landlock and the roots), or empty when they are not.</summary>
    public string Sandbox { get; set; } = string.Empty;

    /// <inheritdoc />
    public void Validate()
    {
        Validation.NoNulls(Devices, nameof(Devices));
        Gpu?.Validate();
    }
}

/// <summary>
/// Broker → agent: registration accepted.
/// </summary>
public sealed class Welcome
{
    /// <summary>Gets or sets the negotiated protocol.</summary>
    public int Protocol { get; set; }

    /// <summary>Gets or sets the ping interval; the agent reconnects after three missed pings.</summary>
    public int HeartbeatMs { get; set; }

    /// <summary>Gets or sets the broker's (Jellyfin server's) ffmpeg version line.</summary>
    public string ServerFfmpegVersion { get; set; } = string.Empty;
}

/// <summary>
/// Agent → broker: current load, sent with every pong.
/// </summary>
public sealed class AgentLoad
{
    /// <summary>Gets or sets the number of running jobs.</summary>
    public int Jobs { get; set; }
}

/// <summary>
/// Broker → agent: start a job, then attach its job socket.
/// </summary>
public sealed class Assign : IValidated
{
    /// <summary>Gets or sets the job id.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>Gets or sets the one-time key for the job socket.</summary>
    public string JobKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the binary: "ffmpeg" or "ffprobe".</summary>
    public string Binary { get; set; } = "ffmpeg";

    /// <summary>Gets or sets the arguments.</summary>
    public string[] Args { get; set; } = [];

    /// <summary>Gets or sets the working directory.</summary>
    public string Cwd { get; set; } = "/";

    /// <summary>Gets or sets the environment to add.</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Gets or sets the nice value to run with.</summary>
    public int Nice { get; set; }

    /// <inheritdoc />
    public void Validate()
    {
        Validation.NoNulls(Args, nameof(Args));
        Validation.NoNulls(Env, nameof(Env));
    }
}

/// <summary>
/// One shared root to prove, in <see cref="VerifyRequest"/>.
/// </summary>
public sealed class RootProbe
{
    /// <summary>Gets or sets the root directory.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether ffmpeg writes here (transcodes, temp, subtitles...).</summary>
    public bool Writable { get; set; }

    /// <summary>Gets or sets the probe file the broker wrote (writable roots).</summary>
    public string? ProbeFile { get; set; }

    /// <summary>Gets or sets the nonce inside the probe file.</summary>
    public string? Nonce { get; set; }

    /// <summary>Gets or sets where the agent writes its reply (the nonce).</summary>
    public string? ReplyFile { get; set; }

    /// <summary>Gets or sets a file the server sees under a read-only root, to stat.</summary>
    public string? SampleFile { get; set; }

    /// <summary>Gets or sets the sample file's size on the server.</summary>
    public long? SampleSize { get; set; }
}

/// <summary>
/// Broker → agent: prove you see the shared roots at the same paths, and run the
/// hardware self-test built from the server's encoding settings.
/// </summary>
public sealed class VerifyRequest : IValidated
{
    /// <summary>Gets or sets the roots.</summary>
    public RootProbe[] Roots { get; set; } = [];

    /// <summary>Gets or sets the ffmpeg arguments of the hardware self-test, empty when the server encodes in software.</summary>
    public string[] HardwareTestArgs { get; set; } = [];

    /// <summary>Gets or sets a label for the hardware test (qsv, vaapi, nvenc...).</summary>
    public string HardwareType { get; set; } = "none";

    /// <inheritdoc />
    public void Validate()
    {
        Validation.NoNulls(Roots, nameof(Roots));
        Validation.NoNulls(HardwareTestArgs, nameof(HardwareTestArgs));
    }
}

/// <summary>
/// One root's outcome, in <see cref="VerifyResult"/>.
/// </summary>
public sealed class RootResult
{
    /// <summary>Gets or sets the root directory.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the root is shared.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets what went wrong.</summary>
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Agent → broker: the verification outcome.
/// </summary>
public sealed class VerifyResult : IValidated
{
    /// <summary>Gets or sets the per-root outcomes.</summary>
    public RootResult[] Roots { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether a hardware test ran.</summary>
    public bool HardwareTested { get; set; }

    /// <summary>Gets or sets a value indicating whether it passed.</summary>
    public bool HardwareOk { get; set; }

    /// <summary>Gets or sets the end of its stderr when it failed.</summary>
    public string HardwareDetail { get; set; } = string.Empty;

    /// <inheritdoc />
    public void Validate()
    {
        Validation.NoNulls(Roots, nameof(Roots));
    }
}

/// <summary>
/// Broker → agent: kill a job.
/// </summary>
public sealed class CancelJob
{
    /// <summary>Gets or sets the job id.</summary>
    public string JobId { get; set; } = string.Empty;
}

/// <summary>
/// Source-generated JSON for every message: required by NativeAOT, faster everywhere.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(StartRequest))]
[JsonSerializable(typeof(Placement))]
[JsonSerializable(typeof(StartedInfo))]
[JsonSerializable(typeof(SignalInfo))]
[JsonSerializable(typeof(ExitInfo))]
[JsonSerializable(typeof(ErrorInfo))]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(Welcome))]
[JsonSerializable(typeof(AgentLoad))]
[JsonSerializable(typeof(Assign))]
[JsonSerializable(typeof(CancelJob))]
[JsonSerializable(typeof(VerifyRequest))]
[JsonSerializable(typeof(VerifyResult))]
public sealed partial class ProtocolJson : JsonSerializerContext
{
}
