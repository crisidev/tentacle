using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Tentacle.Protocol;

/// <summary>
/// One protocol frame: a type byte and its payload.
/// </summary>
/// <param name="Type">The frame type.</param>
/// <param name="Payload">The payload: raw bytes or UTF-8 JSON.</param>
public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload)
{
    /// <summary>
    /// The largest frame accepted on any transport.
    /// </summary>
    public const int MaxFrameBytes = 1 << 20;

    /// <summary>
    /// The largest data payload a sender produces (stdio chunks).
    /// </summary>
    public const int MaxDataBytes = 64 * 1024;

    /// <summary>
    /// Builds a frame with a JSON payload.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="type">The frame type.</param>
    /// <param name="value">The message.</param>
    /// <param name="info">Its source-generated type info from <see cref="ProtocolJson"/>.</param>
    /// <returns>The frame.</returns>
    public static Frame Json<T>(FrameType type, T value, JsonTypeInfo<T> info)
        => new(type, JsonSerializer.SerializeToUtf8Bytes(value, info));

    /// <summary>
    /// Builds a frame without payload.
    /// </summary>
    /// <param name="type">The frame type.</param>
    /// <returns>The frame.</returns>
    public static Frame Empty(FrameType type) => new(type, ReadOnlyMemory<byte>.Empty);

    /// <summary>
    /// Reads the JSON payload.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="info">Its source-generated type info.</param>
    /// <returns>The message.</returns>
    public T Read<T>(JsonTypeInfo<T> info)
    {
        var value = JsonSerializer.Deserialize(Payload.Span, info) ?? throw new InvalidDataException($"empty {Type} payload");
        (value as IValidated)?.Validate();
        return value;
    }
}

/// <summary>
/// Frames on a byte stream (the shim's unix socket): u32le length, then type and payload.
/// </summary>
public static class StreamFrames
{
    /// <summary>
    /// Writes a frame. Not thread-safe: callers serialize writers.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="frame">The frame.</param>
    public static void Write(Stream stream, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[5 + frame.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(1 + frame.Payload.Length));
        buffer[4] = (byte)frame.Type;
        frame.Payload.Span.CopyTo(buffer.AsSpan(5));
        stream.Write(buffer);
        stream.Flush();
    }

    /// <summary>
    /// Writes a frame asynchronously. Not thread-safe: callers serialize writers.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(Stream stream, Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[5 + frame.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)(1 + frame.Payload.Length));
        buffer[4] = (byte)frame.Type;
        frame.Payload.Span.CopyTo(buffer.AsSpan(5));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The frame, or null at a clean EOF.</returns>
    public static Frame? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[4];
        if (!Fill(stream, header, allowEof: true))
        {
            return null;
        }

        var body = new byte[Length(header)];
        Fill(stream, body, allowEof: false);
        return new Frame((FrameType)body[0], body.AsMemory(1));
    }

    /// <summary>
    /// Reads one frame asynchronously.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The frame, or null at a clean EOF.</returns>
    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await FillAsync(stream, header, allowEof: true, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var body = new byte[Length(header)];
        await FillAsync(stream, body, allowEof: false, cancellationToken).ConfigureAwait(false);
        return new Frame((FrameType)body[0], body.AsMemory(1));
    }

    private static int Length(ReadOnlySpan<byte> header)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 1 or > Frame.MaxFrameBytes)
        {
            throw new InvalidDataException($"bad frame length {length}");
        }

        return (int)length;
    }

    private static bool Fill(Stream stream, Span<byte> buffer, bool allowEof)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                if (read == 0 && allowEof)
                {
                    return false;
                }

                throw new EndOfStreamException("truncated frame");
            }

            read += n;
        }

        return true;
    }

    private static async Task<bool> FillAsync(Stream stream, Memory<byte> buffer, bool allowEof, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0 && allowEof)
                {
                    return false;
                }

                throw new EndOfStreamException("truncated frame");
            }

            read += n;
        }

        return true;
    }
}

/// <summary>
/// Frames on a WebSocket: one binary message per frame.
/// </summary>
public static class SocketFrames
{
    /// <summary>
    /// Sends a frame. Not thread-safe: callers serialize senders.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="frame">The frame.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task SendAsync(WebSocket socket, Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = new byte[1 + frame.Payload.Length];
        buffer[0] = (byte)frame.Type;
        frame.Payload.Span.CopyTo(buffer.AsSpan(1));
        await socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives one frame.
    /// </summary>
    /// <param name="socket">The socket.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The frame, or null when the peer closed the socket.</returns>
    public static async Task<Frame?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = new byte[Frame.MaxDataBytes + 1];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (message.Length > Frame.MaxFrameBytes)
            {
                throw new InvalidDataException("frame too large");
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        if (message.Length == 0)
        {
            throw new InvalidDataException("empty frame");
        }

        var bytes = message.ToArray();
        return new Frame((FrameType)bytes[0], bytes.AsMemory(1));
    }
}
