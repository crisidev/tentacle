using Tentacle.Cli;
using Xunit;

namespace Tentacle.Cli.Tests;

public class InvocationTests
{
    [Theory]
    [InlineData("/usr/local/bin/tentacle/ffmpeg", "Ffmpeg")]
    [InlineData("ffmpeg", "Ffmpeg")]
    [InlineData("/usr/local/bin/tentacle/ffprobe", "Ffprobe")]
    [InlineData("/opt/x/ffprobe.exe", "Ffprobe")]
    [InlineData("/usr/local/bin/tentacle/tentacle", "Tentacle")]
    [InlineData("", "Tentacle")]
    public void ResolvesModeFromArgv0(string argv0, string expected)
    {
        Assert.Equal(expected, Invocation.Resolve(argv0).ToString());
    }

    [Fact]
    public void RealBinaryDefaultsToJellyfinFfmpeg()
    {
        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", Shim.RealBinary(ToolMode.Ffmpeg, null));
        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffprobe", Shim.RealBinary(ToolMode.Ffprobe, string.Empty));
        Assert.Equal("/opt/ff/ffprobe", Shim.RealBinary(ToolMode.Ffprobe, "/opt/ff"));
    }
}
