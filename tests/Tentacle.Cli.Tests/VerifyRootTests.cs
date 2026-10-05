using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tentacle.Cli;
using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Cli.Tests;

/// <summary>
/// The agent answers verifications unconfined, so the broker's paths must never
/// lead it outside the root: not by "..", not through a symlink.
/// </summary>
public sealed class VerifyRootTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "verify-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RealPathFollowsSymlinksAndDotDot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

        Assert.Equal(outside, Native.RealPath(Path.Combine(root, "link")));
        Assert.Equal(outside, Native.RealPath(root + "/../outside"));
        Assert.Null(Native.RealPath(Path.Combine(_dir, "missing")));
        Assert.False(ArgvAnalysis.IsUnder(Native.RealPath(Path.Combine(root, "link"))!, root));
    }

    [Fact]
    public async Task WritesTheReplyInTheResolvedRoot()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_dir, "outside")).FullName;
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, outside);
        await File.WriteAllTextAsync(Path.Combine(outside, ".tentacle-probe-ab12"), "n0nce", TestContext.Current.CancellationToken);

        var probe = new RootProbe
        {
            Path = link,
            Writable = true,
            Nonce = "n0nce",
            ProbeFile = Path.Combine(link, ".tentacle-probe-ab12"),
            ReplyFile = Path.Combine(link, ".tentacle-reply-ab12"),
        };
        var result = await Agent.VerifyRootAsync(probe, Native.RealPath(link), TestContext.Current.CancellationToken);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(link, result.Path);
        Assert.Equal("n0nce", await File.ReadAllTextAsync(Path.Combine(outside, ".tentacle-reply-ab12"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefusesSampleFilesOutsideTheRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "media")).FullName;
        var secret = Path.Combine(_dir, "secret.txt");
        await File.WriteAllTextAsync(secret, "12345", TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), _dir);

        foreach (var sample in new[] { secret, root + "/../secret.txt", Path.Combine(root, "escape", "secret.txt"), Path.Combine(_dir, "missing.txt") })
        {
            var result = await Agent.VerifyRootAsync(new RootProbe { Path = root, SampleFile = sample, SampleSize = 5 }, Native.RealPath(root), TestContext.Current.CancellationToken);
            Assert.False(result.Ok);
            Assert.Contains("not inside the root", result.Detail, StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(Path.Combine(root, "movie.mkv"), "12345", TestContext.Current.CancellationToken);
        var ok = await Agent.VerifyRootAsync(new RootProbe { Path = root, SampleFile = Path.Combine(root, "movie.mkv"), SampleSize = 5 }, Native.RealPath(root), TestContext.Current.CancellationToken);
        Assert.True(ok.Ok, ok.Detail);
    }
}
