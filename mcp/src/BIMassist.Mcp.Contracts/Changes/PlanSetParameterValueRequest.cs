using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;

namespace BIMassist.Mcp.Contracts.Changes;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlanSetParameterValueRequest
{
    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    [JsonRequired]
    public required ParameterIdentity Parameter { get; init; }

    [JsonRequired]
    public required ParameterValue After { get; init; }
}
