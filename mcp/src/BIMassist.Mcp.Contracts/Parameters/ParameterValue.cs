using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Parameters;

public enum ParameterValueKind
{
    None,
    String,
    Integer,
    Double,
    Boolean,
    ElementReference,
    MaterialReference,
    TypeReference
}

public sealed record ParameterValue
{
    [JsonRequired]
    public required ParameterValueKind Kind { get; init; }

    [JsonRequired]
    public required bool HasValue { get; init; }

    [JsonRequired]
    public required bool IsReadOnly { get; init; }

    public string? StringValue { get; init; }

    public long? IntegerValue { get; init; }

    public double? DoubleValue { get; init; }

    public double? InternalDoubleValue { get; init; }

    public bool? BooleanValue { get; init; }

    public string? DisplayValue { get; init; }

    public string? SpecTypeId { get; init; }

    public string? InputUnitTypeId { get; init; }

    public ElementReferenceValue? Reference { get; init; }

    public string? Formula { get; init; }

    public string? BlockingReason { get; init; }
}

public enum ParameterReferenceKind
{
    Element,
    Material,
    Type
}

public sealed record ElementReferenceValue(
    string? UniqueId,
    [property: JsonRequired] long ElementId,
    string? Name,
    [property: JsonRequired] ParameterReferenceKind ReferenceKind);
