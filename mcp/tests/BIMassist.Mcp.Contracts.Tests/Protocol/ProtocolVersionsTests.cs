using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Tests.Protocol;

public sealed class ProtocolVersionsTests
{
    [Fact]
    public void Current_versions_are_explicit_and_stable()
    {
        Assert.Equal("1", ProtocolVersions.ProtocolVersion);
        Assert.Equal("1", ProtocolVersions.SchemaVersion);
        Assert.Equal("0.1.1", ProtocolVersions.AddonVersion);
        Assert.Equal(2026, ProtocolVersions.RevitMajor);
    }

    [Theory]
    [InlineData("1", "1", true)]
    [InlineData("2", "1", false)]
    [InlineData("1", "2", false)]
    [InlineData(null, "1", false)]
    [InlineData("1", null, false)]
    public void Compatibility_requires_exact_protocol_and_schema_versions(
        string? protocolVersion,
        string? schemaVersion,
        bool expected)
    {
        Assert.Equal(expected, ProtocolVersions.IsCompatible(protocolVersion, schemaVersion));
    }
}
