using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;

namespace BIMassist.Mcp.Contracts.Changes;

// Contract only: no bridge operation or Apply handler is registered for this request.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanSetStringParametersBatchRequest
{
    [JsonRequired]
    public required string DocumentKey { get; init; }

    [JsonRequired]
    public required IReadOnlyList<StringParameterBatchItem> Items { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StringParameterBatchItem
{
    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    [JsonRequired]
    public required ParameterIdentity Parameter { get; init; }

    [JsonRequired]
    public required string NewString { get; init; }
}
