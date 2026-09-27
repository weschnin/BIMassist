using BIMassist.Mcp.Server.Transport;

namespace BIMassist.Mcp.Server.Tests.Transport;

public sealed class BridgeEndpointNameTests
{
    [Fact]
    public void Creates_the_same_sid_process_and_protocol_endpoint_as_the_bridge()
    {
        string endpoint = BridgeEndpointName.Create("S-1-5-21-100-200-300-1001", 4242);

        Assert.Equal("bimassist.mcp.revit.s-1-5-21-100-200-300-1001.4242.v1", endpoint);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("not-a-sid", 10)]
    [InlineData("S-1-5-21-100", 0)]
    public void Rejects_invalid_endpoint_identity(string sid, int processId)
    {
        Assert.Throws<ArgumentException>(() => BridgeEndpointName.Create(sid, processId));
    }
}
