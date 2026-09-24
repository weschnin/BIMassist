using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

public sealed record ListSharedDefinitionsRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    public string? NameContains { get; init; }

    public string? GroupName { get; init; }

    public Guid? SharedGuid { get; init; }
}

public sealed record SharedDefinitionDescriptor
{
    [JsonRequired]
    public required Guid SharedGuid { get; init; }

    [JsonRequired]
    public required string Name { get; init; }

    [JsonRequired]
    public required string DataTypeId { get; init; }

    [JsonRequired]
    public required string GroupName { get; init; }

    public string? Description { get; init; }

    [JsonRequired]
    public required bool IsVisible { get; init; }

    [JsonRequired]
    public required bool IsUserModifiable { get; init; }
}
