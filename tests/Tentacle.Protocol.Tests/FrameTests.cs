using System.IO;
using System.Linq;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Protocol.Tests;

public class FrameTests
{
    [Fact]
    public void StreamFramesRoundTrip()
    {
        using var stream = new MemoryStream();
        var data = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
        StreamFrames.Write(stream, new Frame(FrameType.Stdout, data));
        StreamFrames.Write(stream, Frame.Json(FrameType.Exit, new ExitInfo { Code = 137, DurationMs = 5 }, ProtocolJson.Default.ExitInfo));
        StreamFrames.Write(stream, Frame.Empty(FrameType.StdinEof));
        stream.Position = 0;

        var first = StreamFrames.Read(stream)!.Value;
        Assert.Equal(FrameType.Stdout, first.Type);
        Assert.Equal(data, first.Payload.ToArray());

        var exit = StreamFrames.Read(stream)!.Value.Read(ProtocolJson.Default.ExitInfo);
        Assert.Equal(137, exit.Code);

        Assert.Equal(FrameType.StdinEof, StreamFrames.Read(stream)!.Value.Type);
        Assert.Null(StreamFrames.Read(stream));
    }

    [Fact]
    public void TruncatedFrameThrows()
    {
        using var stream = new MemoryStream([10, 0, 0, 0, (byte)FrameType.Stdout, 1, 2]);
        Assert.Throws<EndOfStreamException>(() => StreamFrames.Read(stream));
    }

    [Fact]
    public void OversizedFrameIsRejected()
    {
        using var stream = new MemoryStream([0, 0, 0, 0x10, 1]);
        Assert.Throws<InvalidDataException>(() => StreamFrames.Read(stream));
    }
}
