using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Parameters;

public enum ParameterIdentityKind
{
    SharedGuid,
    BuiltIn,
    ParameterElement,
    FamilyDefinition
}

public sealed record ParameterIdentity
{
    [JsonRequired]
    public required ParameterIdentityKind Kind { get; init; }

    public Guid? SharedGuid { get; init; }

    public long? BuiltInId { get; init; }

    [JsonRequired]
    public required string StableId { get; init; }

    [JsonRequired]
    public required string Name { get; init; }

    public string? OwnerContext { get; init; }

    public long? DefinitionId { get; init; }

    public string? DataTypeId { get; init; }

    public bool? IsInstance { get; init; }
}
