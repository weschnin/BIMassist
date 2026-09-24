using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

public sealed record ListProjectBindingsRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    public string? NameContains { get; init; }

    public ParameterBindingKind? BindingKind { get; init; }

    public string? CategoryId { get; init; }

    public bool? IsShared { get; init; }
}

public sealed record ProjectBindingDescriptor
{
    [JsonRequired]
    public required ParameterIdentity Identity { get; init; }

    [JsonRequired]
    public required ParameterDefinitionMetadata Definition { get; init; }

    [JsonRequired]
    public required ParameterBindingMetadata Binding { get; init; }
}
