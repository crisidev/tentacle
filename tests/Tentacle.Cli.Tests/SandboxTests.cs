using System;
using System.Diagnostics;
using System.IO;
using Tentacle.Cli;
using Xunit;

namespace Tentacle.Cli.Tests;

public class SandboxTests
{
    [Fact]
    public void RootsAreAbsoluteDistinctAndNeverEverything()
    {
        Assert.Equal(["/data/movies", "/config/cache/transcodes"], Sandbox.ParseRoots(" /data/movies/ : /config/cache/transcodes ::/data/movies"));
        Assert.Empty(Sandbox.ParseRoots(null));
        Assert.Empty(Sandbox.ParseRoots(string.Empty));
        Assert.Null(Sandbox.Create(string.Empty));
        Assert.Throws<ArgumentException>(() => Sandbox.ParseRoots("data/movies"));
        Assert.Throws<ArgumentException>(() => Sandbox.ParseRoots("/data/../etc"));
        Assert.Throws<ArgumentException>(() => Sandbox.ParseRoots("/"));
    }

    [Fact]
    public void ConfinedJobReadsAndWritesTheRootsOnly()
    {
        Sandbox sandbox;
        try
        {
            sandbox = Sandbox.Create("/nonexistent-root")!;
        }
        catch (ArgumentException)
        {
            return; // No Landlock here: nothing to prove.
        }

        // Outside /tmp, which jobs may use as scratch space.
        var dir = Path.Combine(AppContext.BaseDirectory, "sandbox-" + Guid.NewGuid().ToString("N")[..8]);
        var root = Directory.CreateDirectory(Path.Combine(dir, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(dir, "outside")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "in.txt"), "inside");
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            sandbox = Sandbox.Create(root)!;
            Assert.True(sandbox.Allows(Path.Combine(root, "x")));
            Assert.False(sandbox.Allows(outside));

            Assert.Equal((0, "inside"), Run(sandbox, "/bin/cat", Path.Combine(root, "in.txt")));
            Assert.NotEqual(0, Run(sandbox, "/bin/cat", Path.Combine(outside, "secret.txt")).Code);
            Assert.Equal(0, Run(sandbox, "/bin/cp", Path.Combine(root, "in.txt"), Path.Combine(root, "copy.txt")).Code);
            Assert.NotEqual(0, Run(sandbox, "/bin/cp", Path.Combine(root, "in.txt"), Path.Combine(outside, "copy.txt")).Code);
            Assert.False(File.Exists(Path.Combine(outside, "copy.txt")));

            // A relative path is no way out: the cwd does not widen anything.
            Assert.NotEqual(0, Run(sandbox, "/bin/cat", "../outside/secret.txt").Code);

            // No symlinks: one in a shared root could point the server at its own files.
            Assert.NotEqual(0, Run(sandbox, "/bin/ln", "-s", "/etc/passwd", Path.Combine(root, "link")).Code);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static (int Code, string Output) Run(Sandbox sandbox, string binary, params string[] args)
        {
            var self = Path.Combine(AppContext.BaseDirectory, "tentacle.dll");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = sandbox.Roots[0],
            };
            start.ArgumentList.Add(self);
            foreach (var arg in sandbox.Wrap(self, binary, 0))
            {
                start.ArgumentList.Add(arg);
            }

            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
    }

    [Fact]
    public void PlainWsTakesAnOptInAndNeverGoesWithTls()
    {
        var system = BrokerTrust.Create(null, null);
        var pinned = BrokerTrust.Create(new string('a', 64), null);

        Assert.Null(Agent.PlainWsRefusal("wss://broker:8097", system, null));
        Assert.Null(Agent.PlainWsRefusal("wss://broker:8097", pinned, null));
        Assert.Contains("TENTACLE_ALLOW_PLAIN_WS", Agent.PlainWsRefusal("ws://broker:8097", system, null), StringComparison.Ordinal);
        Assert.Contains("must be wss://", Agent.PlainWsRefusal("ws://broker:8097", system, "false"), StringComparison.Ordinal);
        Assert.Null(Agent.PlainWsRefusal("ws://broker:8097", system, "true"));
        Assert.Contains("TENTACLE_BROKER_FINGERPRINT", Agent.PlainWsRefusal("ws://broker:8097", pinned, "true"), StringComparison.Ordinal);
    }
}
