using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Parameters;

public enum ParameterTargetKind
{
    Document,
    Element,
    ElementType,
    Family,
    FamilyType
}

public sealed record ParameterTarget
{
    [JsonRequired]
    public required ParameterTargetKind Kind { get; init; }

    public string? UniqueId { get; init; }

    public long? ElementId { get; init; }

    public string? FamilyName { get; init; }

    public string? TypeName { get; init; }
}
