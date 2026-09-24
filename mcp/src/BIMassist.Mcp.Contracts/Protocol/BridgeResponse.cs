using System.Text.Json;
using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Protocol;

public sealed record BridgeResponse
{
    [JsonRequired]
    public required string RequestId { get; init; }

    [JsonRequired]
    public required bool Success { get; init; }

    public JsonElement? Result { get; init; }

    [JsonRequired]
    public required IReadOnlyList<BridgeWarning> Warnings { get; init; }

    public BridgeError? Error { get; init; }

    public string? DocumentRevision { get; init; }

    [JsonRequired]
    public required long DurationMs { get; init; }
}

public sealed record BridgeWarning(
    [property: JsonRequired] string Code,
    [property: JsonRequired] string Message);
