using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tentacle.Broker;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Broker.Tests;

/// <summary>
/// Whatever a shim connection sends, the broker ends it with an exception the
/// connection handler expects, never anything else.
/// </summary>
public class BrokerFuzzTests
{
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("TENTACLE_FUZZ_ITERATIONS"), out var n) && n > 0 ? n / 10 : 2_000;

    private static bool Expected(Exception e)
        => e is IOException or OperationCanceledException or InvalidDataException or JsonException;

    [Fact]
    public async Task MutatedShimConnectionsEndCleanly()
    {
        var options = new BrokerOptions { Placement = PlacementMode.Active, SharedRoots = () => [], ServerFfmpegVersion = () => "8.1.3" };
        var broker = new Broker(options, NullLogger<Broker>.Instance);
        var random = new Random(4);
        var start = new StartRequest { Args = ["-i", "/data/a.mkv", "-f", "hls", "/config/cache/transcodes/x.m3u8"], Env = new() { ["TZ"] = "UTC" } };
        using var valid = new MemoryStream();
        StreamFrames.Write(valid, Frame.Json(FrameType.Start, start, ProtocolJson.Default.StartRequest));
        StreamFrames.Write(valid, new Frame(FrameType.Stdin, new byte[] { (byte)'q' }));
        var template = valid.ToArray();

        for (var i = 0; i < Iterations; i++)
        {
            var bytes = new List<byte>(template);
            for (var e = random.Next(1, 5); e > 0; e--)
            {
                var at = random.Next(bytes.Count);
                switch (random.Next(4))
                {
                    case 0: bytes[at] = (byte)random.Next(256); break;
                    case 1: bytes.Insert(at, (byte)random.Next(256)); break;
                    case 2: bytes.RemoveAt(at); break;
                    default: bytes.RemoveRange(at, bytes.Count - at); break;
                }

                if (bytes.Count == 0)
                {
                    break;
                }
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await broker.HandleShimAsync(new MemoryStream(bytes.ToArray()), new MemoryStream(), null, cts.Token);
            }
            catch (Exception e) when (Expected(e))
            {
            }

            Assert.False(cts.IsCancellationRequested, "a connection hung");
        }

        // Every job the fuzzing started is accounted as ended.
        Assert.All(broker.History.Snapshot(500), j => Assert.NotEqual("running", j.Outcome));
    }
}
