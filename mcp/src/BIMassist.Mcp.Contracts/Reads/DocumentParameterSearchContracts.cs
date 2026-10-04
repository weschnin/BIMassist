using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SearchDocumentParametersRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    [JsonRequired]
    public required string NameContains { get; init; }

    public string? CategoryId { get; init; }

    [JsonRequired]
    public required bool IncludeTypes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentParameterMatch
{
    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    [JsonRequired]
    public required ParameterIdentity Parameter { get; init; }

    [JsonRequired]
    public required ParameterStorageType StorageType { get; init; }

    [JsonRequired]
    public required bool IsReadOnly { get; init; }
}
