using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Protocol;

public sealed class BridgeMessageSerializationTests
{
    [Fact]
    public void Request_roundtrip_preserves_transport_and_concurrency_fields()
    {
        var payload = JsonDocument.Parse("""{"target":"uid-1","value":42}""").RootElement.Clone();
        var request = new BridgeRequest
        {
            ProtocolVersion = ProtocolVersions.ProtocolVersion,
            SchemaVersion = ProtocolVersions.SchemaVersion,
            RequestId = "request-1",
            SessionId = "session-1",
            DocumentKey = "document-1",
            ExpectedRevision = "revision-7",
            IdempotencyKey = "idem-1",
            Operation = BridgeOperations.PlanSetParameterValues,
            Payload = payload
        };

        string json = ContractJson.Serialize(request);
        BridgeRequest restored = ContractJson.Deserialize<BridgeRequest>(json);

        Assert.Equal(request.RequestId, restored.RequestId);
        Assert.Equal(request.SessionId, restored.SessionId);
        Assert.Equal(request.DocumentKey, restored.DocumentKey);
        Assert.Equal(request.ExpectedRevision, restored.ExpectedRevision);
        Assert.Equal(request.IdempotencyKey, restored.IdempotencyKey);
        Assert.Equal(request.Operation, restored.Operation);
        Assert.Equal(42, restored.Payload.GetProperty("value").GetInt32());
    }

    [Fact]
    public void Unknown_request_fields_are_rejected()
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

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Missing_required_request_fields_are_rejected()
    {
        const string json = """{"protocolVersion":"1","schemaVersion":"1","payload":{}}""";

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Response_roundtrip_preserves_structured_error_and_warnings()
    {
        var response = new BridgeResponse
        {
            RequestId = "request-1",
            Success = false,
            Warnings = [new BridgeWarning("READBACK_SKIPPED", "No value was changed.")],
            Error = new BridgeError(
                BridgeErrorCodes.DocumentChanged,
                "The document revision changed.",
                new Dictionary<string, string> { ["expected"] = "7", ["actual"] = "8" }),
            DocumentRevision = "8",
            DurationMs = 12
        };

        string json = ContractJson.Serialize(response);
        BridgeResponse restored = ContractJson.Deserialize<BridgeResponse>(json);

        Assert.False(restored.Success);
        Assert.Null(restored.Result);
        Assert.Equal(BridgeErrorCodes.DocumentChanged, restored.Error?.Code);
        Assert.Equal("8", restored.Error?.Details?["actual"]);
        Assert.Single(restored.Warnings);
        Assert.Equal(12, restored.DurationMs);
    }
}
