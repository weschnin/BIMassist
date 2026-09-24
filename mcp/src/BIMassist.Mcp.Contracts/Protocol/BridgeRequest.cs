using System.Text.Json;
using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Protocol;

public sealed record BridgeRequest
{
    [JsonRequired]
    public required string ProtocolVersion { get; init; }

    [JsonRequired]
    public required string SchemaVersion { get; init; }

    [JsonRequired]
    public required string RequestId { get; init; }

    public string? SessionId { get; init; }

    public string? DocumentKey { get; init; }

    public string? ExpectedRevision { get; init; }

    public string? IdempotencyKey { get; init; }

    [JsonRequired]
    public required string Operation { get; init; }

    [JsonRequired]
    public required JsonElement Payload { get; init; }
}
