using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Parameters;

public enum ParameterStorageType
{
    None,
    String,
    Integer,
    Double,
    ElementId
}

public enum ParameterBindingKind
{
    None,
    Shared,
    FamilyInstance,
    FamilyType,
    ProjectInstance,
    ProjectType
}

public sealed record ParameterDefinitionMetadata(
    [property: JsonRequired] string Name,
    string? DataTypeId,
    string? GroupTypeId,
    string? Description,
    [property: JsonRequired] bool IsVisible,
    [property: JsonRequired] bool IsUserModifiable);

public sealed record ParameterBindingMetadata(
    [property: JsonRequired] ParameterBindingKind Kind,
    [property: JsonRequired] IReadOnlyList<string> CategoryIds,
    string? FamilyCategoryId,
    [property: JsonRequired] string Origin);

public sealed record ParameterMetadata
{
    [JsonRequired]
    public required ParameterIdentity Identity { get; init; }

    [JsonRequired]
    public required ParameterDefinitionMetadata Definition { get; init; }

    [JsonRequired]
    public required ParameterBindingMetadata Binding { get; init; }

    [JsonRequired]
    public required ParameterStorageType StorageType { get; init; }

    [JsonRequired]
    public required bool IsReadOnly { get; init; }

    public string? Formula { get; init; }

    public string? BlockedReason { get; init; }

    [JsonRequired]
    public required ParameterContextMetadata Context { get; init; }

    public ParameterUnitMetadata? Unit { get; init; }

    [JsonRequired]
    public required ParameterWorksharingMetadata Worksharing { get; init; }
}

public sealed record ParameterContextMetadata
{
    [JsonRequired]
    public required string DocumentKey { get; init; }

    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    [JsonRequired]
    public required bool IsTypeParameter { get; init; }

    [JsonRequired]
    public required bool IsFamilyParameter { get; init; }
}

public sealed record ParameterUnitMetadata
{
    [JsonRequired]
    public required string SpecTypeId { get; init; }

    public string? UnitTypeId { get; init; }

    public string? Format { get; init; }
}

public sealed record ParameterWorksharingMetadata
{
    [JsonRequired]
    public required bool IsWorkshared { get; init; }

    [JsonRequired]
    public required bool IsOwnedByCurrentUser { get; init; }

    public string? Owner { get; init; }

    [JsonRequired]
    public required bool IsEditable { get; init; }
}
