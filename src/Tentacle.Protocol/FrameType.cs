namespace Tentacle.Protocol;

/// <summary>
/// The first byte of every frame. Job frames flow shim ↔ broker ↔ agent; control
/// frames flow broker ↔ agent. JSON payloads use <see cref="ProtocolJson"/>.
/// </summary>
public enum FrameType : byte
{
    /// <summary>
    /// Shim → broker, JSON <see cref="StartRequest"/>: the invocation to place.
    /// </summary>
    Start = 0x01,

    /// <summary>
    /// Broker → shim, JSON <see cref="Placement"/>: run locally or remotely.
    /// </summary>
    Placement = 0x02,

    /// <summary>
    /// Agent → broker → shim, JSON <see cref="StartedInfo"/>: the remote process is running.
    /// </summary>
    Started = 0x03,

    /// <summary>
    /// Shim → agent, raw bytes for the process's stdin.
    /// </summary>
    Stdin = 0x10,

    /// <summary>
    /// Shim → agent, no payload: stdin reached EOF.
    /// </summary>
    StdinEof = 0x11,

    /// <summary>
    /// Agent → shim, raw bytes from the process's stdout.
    /// </summary>
    Stdout = 0x12,

    /// <summary>
    /// Agent → shim, raw bytes from the process's stderr.
    /// </summary>
    Stderr = 0x13,

    /// <summary>
    /// Shim → agent, no payload: the shim's stdout is gone (EPIPE); stop reading.
    /// </summary>
    StdoutClosed = 0x14,

    /// <summary>
    /// Shim → agent, JSON <see cref="SignalInfo"/>: deliver a signal to the process.
    /// </summary>
    Signal = 0x15,

    /// <summary>
    /// Agent → shim, JSON <see cref="ExitInfo"/>: the process exited.
    /// </summary>
    Exit = 0x16,

    /// <summary>
    /// Either direction, JSON <see cref="ErrorInfo"/>: the job failed outside the process.
    /// </summary>
    Error = 0x17,

    /// <summary>
    /// Agent → broker, JSON <see cref="Hello"/>.
    /// </summary>
    Hello = 0x20,

    /// <summary>
    /// Broker → agent, JSON <see cref="Welcome"/>.
    /// </summary>
    Welcome = 0x21,

    /// <summary>
    /// Broker → agent, JSON <see cref="ErrorInfo"/>: registration refused.
    /// </summary>
    Reject = 0x22,

    /// <summary>
    /// Broker → agent, no payload.
    /// </summary>
    Ping = 0x24,

    /// <summary>
    /// Agent → broker, JSON <see cref="AgentLoad"/>.
    /// </summary>
    Pong = 0x25,

    /// <summary>
    /// Broker → agent, JSON <see cref="Assign"/>: start a job.
    /// </summary>
    Assign = 0x26,

    /// <summary>
    /// Broker → agent, JSON <see cref="CancelJob"/>: kill a job.
    /// </summary>
    Cancel = 0x27,

    /// <summary>
    /// Broker → agent, JSON <see cref="VerifyRequest"/>: prove the shared roots and the hardware.
    /// </summary>
    Verify = 0x28,

    /// <summary>
    /// Agent → broker, JSON <see cref="VerifyResult"/>.
    /// </summary>
    VerifyResult = 0x29,

    /// <summary>
    /// Agent → broker, no payload: shutting down, assign nothing new.
    /// </summary>
    Draining = 0x2A,
}
