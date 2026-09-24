using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Documents;

namespace BIMassist.Mcp.Contracts.Sessions;

public sealed record SessionDescriptor
{
    [JsonRequired]
    public required string SessionId { get; init; }

    [JsonRequired]
    public required int RevitProcessId { get; init; }

    [JsonRequired]
    public required int RevitMajor { get; init; }

    [JsonRequired]
    public required string BridgeVersion { get; init; }

    [JsonRequired]
    public required string ProtocolVersion { get; init; }

    [JsonRequired]
    public required string SchemaVersion { get; init; }

    [JsonRequired]
    public required DateTimeOffset StartedAtUtc { get; init; }

    [JsonRequired]
    public required IReadOnlyList<DocumentDescriptor> Documents { get; init; }
}
