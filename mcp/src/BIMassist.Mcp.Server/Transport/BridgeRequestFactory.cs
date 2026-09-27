using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Server.Transport;

public static class BridgeRequestFactory
{
    public static BridgeRequest Create(
        string operation,
        object payload,
        string? requestId = null,
        string? sessionId = null,
        string? documentKey = null,
        string? expectedRevision = null,
        string? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(payload);

        string payloadJson = ContractJson.Serialize(payload);
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        var request = new BridgeRequest
        {
            ProtocolVersion = ProtocolVersions.ProtocolVersion,
            SchemaVersion = ProtocolVersions.SchemaVersion,
            RequestId = requestId ?? Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            DocumentKey = documentKey,
            ExpectedRevision = expectedRevision,
            IdempotencyKey = idempotencyKey,
            Operation = operation,
            Payload = document.RootElement.Clone()
        };
        ContractValidator.Validate(request);
        return request;
    }
}
