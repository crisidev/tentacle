using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Protocol.Tests;

/// <summary>
/// Randomized robustness tests: whatever bytes arrive, decoding either succeeds or
/// fails with the few exceptions the transports handle (InvalidDataException,
/// EndOfStreamException, JsonException) and a decoded message is safe to use.
/// Deterministic seeds; TENTACLE_FUZZ_ITERATIONS makes the runs longer.
/// </summary>
public class FuzzTests
{
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("TENTACLE_FUZZ_ITERATIONS"), out var n) && n > 0 ? n : 20_000;

    private static readonly Frame[] Valid =
    [
        Frame.Json(FrameType.Start, new StartRequest { Args = ["-i", "/data/a.mkv", "-vf", "subtitles=f='/x/y.ass':fontsdir=/f", "-f", "hls", "/t/x.m3u8"], Env = new() { ["TZ"] = "UTC" }, Pid = 42 }, ProtocolJson.Default.StartRequest),
        Frame.Json(FrameType.Placement, new Placement { Remote = true, JobId = "1-2", Node = "tatooine", Reason = "least-loaded", Nice = 10 }, ProtocolJson.Default.Placement),
        Frame.Json(FrameType.Exit, new ExitInfo { Code = 255 }, ProtocolJson.Default.ExitInfo),
        Frame.Json(FrameType.Hello, new Hello { Node = "mandalore", MaxJobs = 4, Devices = ["/dev/dri/renderD128"] }, ProtocolJson.Default.Hello),
        Frame.Json(FrameType.Assign, new Assign { JobId = "1", JobKey = "k", Args = ["-i", "x"], Env = new() { ["LANG"] = "C" } }, ProtocolJson.Default.Assign),
        Frame.Json(FrameType.Verify, new VerifyRequest { Roots = [new RootProbe { Path = "/t", Writable = true, Nonce = "n", ProbeFile = "/t/.p", ReplyFile = "/t/.r" }], HardwareTestArgs = ["-hide_banner"] }, ProtocolJson.Default.VerifyRequest),
        Frame.Json(FrameType.VerifyResult, new VerifyResult { Roots = [new RootResult { Path = "/t", Ok = true }] }, ProtocolJson.Default.VerifyResult),
        new Frame(FrameType.Stdout, new byte[] { 0, 1, 2, 13, 10, 255 }),
        Frame.Empty(FrameType.StdinEof),
    ];

    private static bool Expected(Exception e) => e is InvalidDataException or EndOfStreamException or JsonException;

    private static byte[] Encode(Frame frame)
    {
        using var stream = new MemoryStream();
        StreamFrames.Write(stream, frame);
        return stream.ToArray();
    }

    private static byte[] Mutate(Random random, byte[] input)
    {
        var bytes = new List<byte>(input);
        var edits = random.Next(1, 6);
        for (var i = 0; i < edits; i++)
        {
            var at = bytes.Count == 0 ? 0 : random.Next(bytes.Count);
            switch (random.Next(6))
            {
                case 0 when bytes.Count > 0: bytes[at] ^= (byte)(1 << random.Next(8)); break;
                case 1 when bytes.Count > 0: bytes[at] = (byte)random.Next(256); break;
                case 2: bytes.Insert(at, (byte)random.Next(256)); break;
                case 3 when bytes.Count > 0: bytes.RemoveAt(at); break;
                case 4: bytes.RemoveRange(at, bytes.Count - at); break;
                default:
                    // Splice in JSON that is syntactically fine but semantically nasty.
                    var junk = Encoding.UTF8.GetBytes(random.Next(5) switch
                    {
                        0 => "null",
                        1 => "[null]",
                        2 => "{\"a\":null}",
                        3 => "-1e999",
                        _ => "\"\\u0000\"",
                    });
                    bytes.InsertRange(at, junk);
                    break;
            }
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// Decodes every frame in the bytes and reads its payload as every message type.
    /// </summary>
    private static void DecodeAll(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        while (true)
        {
            Frame? frame;
            try
            {
                frame = StreamFrames.Read(stream);
            }
            catch (Exception e) when (Expected(e))
            {
                return;
            }

            if (frame is not { } f)
            {
                return;
            }

            ReadAs(f, ProtocolJson.Default.StartRequest, s => ArgvAnalysis.Analyze(s.Binary, s.Args, s.Cwd).Paths.ToArray());
            ReadAs(f, ProtocolJson.Default.Assign, a => EnvPolicy.Filter(a.Env));
            ReadAs(f, ProtocolJson.Default.Hello, h => string.Join(',', h.Devices).Length + h.Node.Length);
            ReadAs(f, ProtocolJson.Default.VerifyRequest, v => v.Roots.Sum(r => r.Path.Length) + v.HardwareTestArgs.Sum(a => a.Length));
            ReadAs(f, ProtocolJson.Default.VerifyResult, v => v.Roots.Sum(r => r.Path.Length + r.Detail.Length));
            ReadAs(f, ProtocolJson.Default.Placement, p => p.Node.Length + p.Reason.Length + p.JobId.Length);
            ReadAs(f, ProtocolJson.Default.ErrorInfo, e => e.Message.Length);
        }
    }

    /// <summary>
    /// A payload that deserializes must be usable as-is: no nulls where the types say none.
    /// </summary>
    private static void ReadAs<T>(Frame frame, JsonTypeInfo<T> info, Func<T, object> use)
    {
        T value;
        try
        {
            value = frame.Read(info);
        }
        catch (Exception e) when (Expected(e))
        {
            return;
        }

        use(value);
    }

    [Fact]
    public void RandomBytesNeverCrashTheDecoder()
    {
        var random = new Random(1);
        for (var i = 0; i < Iterations; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);

            // Half the time a plausible length header, so bodies get decoded too.
            if (bytes.Length >= 5 && random.Next(2) == 0)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (uint)random.Next(1, bytes.Length - 3));
            }

            DecodeAll(bytes);
        }
    }

    [Fact]
    public void MutatedFramesNeverCrashTheDecoderOrTheirReaders()
    {
        var random = new Random(2);
        var encoded = Valid.Select(Encode).ToArray();
        for (var i = 0; i < Iterations; i++)
        {
            DecodeAll(Mutate(random, encoded[random.Next(encoded.Length)]));
        }
    }

    [Fact]
    public void OversizedLengthsAreRefusedBeforeAllocating()
    {
        foreach (var length in new uint[] { 0, Frame.MaxFrameBytes + 1, int.MaxValue, uint.MaxValue })
        {
            var bytes = new byte[8];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), length);
            Assert.Throws<InvalidDataException>(() => StreamFrames.Read(new MemoryStream(bytes)));
        }
    }

    [Theory]
    [InlineData("{\"args\":null}")]
    [InlineData("{\"args\":[\"-i\",null]}")]
    [InlineData("{\"env\":{\"TZ\":null}}")]
    [InlineData("{\"binary\":null}")]
    [InlineData("{\"cwd\":null}")]
    public void NullsInsideAStartRequestAreRejected(string json)
    {
        var frame = new Frame(FrameType.Start, Encoding.UTF8.GetBytes(json));
        Assert.ThrowsAny<Exception>(() => frame.Read(ProtocolJson.Default.StartRequest));
        Assert.True(Expected(Record.Exception(() => frame.Read(ProtocolJson.Default.StartRequest))!));
    }

    [Fact]
    public void RandomArgvNeverCrashesTheAnalysis()
    {
        var random = new Random(3);
        string[] vocabulary =
        [
            "-i", "-f", "hls", "-vf", "-filter_complex", "-af", "-hls_segment_filename", "-dump_attachment:t", "-attach",
            "-init_hw_device", "-hwaccel", "-y", "-map", "file:", "pipe:1", "/", "-", "\\", ":", "'", "=", ",", ";", "[0:v]",
            "subtitles=f=", "fontsdir=", "http://127.0.0.1/x", "concat:", "lavfi", "", "%08d.jpg", "-version",
        ];
        for (var i = 0; i < Iterations; i++)
        {
            var args = new string[random.Next(0, 12)];
            for (var j = 0; j < args.Length; j++)
            {
                var parts = random.Next(1, 4);
                args[j] = string.Concat(Enumerable.Range(0, parts).Select(_ => random.Next(4) == 0
                    ? ((char)random.Next(1, 0x2FF)).ToString()
                    : vocabulary[random.Next(vocabulary.Length)]));
            }

            var analysis = ArgvAnalysis.Analyze(random.Next(2) == 0 ? "ffmpeg" : "ffprobe", args, random.Next(2) == 0 ? "/" : null);
            Assert.NotNull(analysis.Paths);
        }
    }
}
