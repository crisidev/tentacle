using Tentacle.Cli;
using Xunit;

namespace Tentacle.Cli.Tests;

public class JobEnvironmentTests
{
    [Theory]
    [InlineData("PATH", true)]
    [InlineData("LANG", true)]
    [InlineData("LC_ALL", true)]
    [InlineData("LIBVA_DRIVERS_PATH", true)]
    [InlineData("LIBVA_DRIVER_NAME", true)]
    [InlineData("NVIDIA_DRIVER_CAPABILITIES", true)]
    [InlineData("NVIDIA_VISIBLE_DEVICES", true)]
    [InlineData("OCL_ICD_VENDORS", true)]
    [InlineData("TENTACLE_TOKEN", false)]
    [InlineData("TENTACLE_TOKEN_FILE", false)]
    [InlineData("TENTACLE_ROOTS", false)]
    [InlineData("AWS_SECRET_ACCESS_KEY", false)]
    [InlineData("DOCKER_MODS", false)]
    [InlineData("S6_VERBOSITY", false)]
    [InlineData("LD_PRELOAD", false)]
    [InlineData("PASSWORD", false)]
    public void JobsInheritOnlyWhatFfmpegNeeds(string name, bool inherited)
    {
        Assert.Equal(inherited, Agent.InheritedByJobs(name));
    }

    [Theory]
    [InlineData(1, "files only")]
    [InlineData(4, "files, TCP listen; not signals")]
    [InlineData(6, "files, TCP listen, signals")]
    [InlineData(8, "files, TCP listen, signals")]
    public void SaysWhatTheKernelConfines(int abi, string expected)
    {
        Assert.StartsWith(expected, Sandbox.Protections(abi), System.StringComparison.Ordinal);
    }
}
