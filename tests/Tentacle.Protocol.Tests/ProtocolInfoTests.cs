using Tentacle.Protocol;
using Xunit;

namespace Tentacle.Protocol.Tests;

public class ProtocolInfoTests
{
    [Fact]
    public void ProductVersionComesFromTheBuild()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", ProtocolInfo.ProductVersion);
    }

    [Fact]
    public void DefaultsMatchThePlan()
    {
        Assert.Equal(8097, ProtocolInfo.DefaultAgentPort);
        Assert.Equal("/run/tentacle/broker.sock", ProtocolInfo.DefaultSocketPath);
    }
}
