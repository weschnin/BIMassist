using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

public sealed record GetParameterValuesRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    public IReadOnlyList<ParameterIdentity>? Parameters { get; init; }
}

public sealed record ParameterValueEntry
{
    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    [JsonRequired]
    public required ParameterIdentity Parameter { get; init; }

    [JsonRequired]
    public required ParameterValue Value { get; init; }
}
