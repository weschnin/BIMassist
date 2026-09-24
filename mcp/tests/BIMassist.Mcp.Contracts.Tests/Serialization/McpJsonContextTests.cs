using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Serialization;

public sealed class McpJsonContextTests
{
    [Fact]
    public void Shared_contract_options_are_read_only_before_first_use()
    {
        Assert.True(ContractJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => ContractJson.Options.WriteIndented = true);
    }

    [Fact]
    public void Shared_contract_options_prefer_generated_contract_metadata()
    {
        Assert.IsType<McpJsonContext>(ContractJson.Options.TypeInfoResolverChain[0]);
    }

    [Fact]
    public void Shared_contract_options_reject_unregistered_types()
    {
        Assert.Throws<NotSupportedException>(() => ContractJson.Serialize(new UnregisteredContract("value")));
    }

    [Fact]
    public void Generated_context_serializes_bridge_request_with_camel_case_names()
    {
        var request = new BridgeRequest
        {
            ProtocolVersion = "1",
            SchemaVersion = "1",
            RequestId = "request-1",
            Operation = "status.get",
            Payload = JsonDocument.Parse("{}").RootElement.Clone()
        };

        string json = JsonSerializer.Serialize(request, McpJsonContext.Default.BridgeRequest);

        Assert.Contains("\"requestId\":\"request-1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_context_rejects_unknown_fields()
    {
        const string json = """
            {
              "protocolVersion":"1",
              "schemaVersion":"1",
              "requestId":"request-1",
              "operation":"status.get",
              "payload":{},
              "unexpected":true
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, McpJsonContext.Default.BridgeRequest));
    }

    private sealed record UnregisteredContract(string Value);
}
