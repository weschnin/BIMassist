using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ListElementsRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    public string? NameContains { get; init; }

    public string? CategoryId { get; init; }

    [JsonRequired]
    public required bool IncludeTypes { get; init; }
}

public sealed record ElementSummary
{
    [JsonRequired]
    public required string UniqueId { get; init; }

    [JsonRequired]
    public required long ElementId { get; init; }

    [JsonRequired]
    public required string Name { get; init; }

    public string? CategoryId { get; init; }

    public string? CategoryName { get; init; }

    [JsonRequired]
    public required bool IsElementType { get; init; }

    public string? TypeUniqueId { get; init; }

    public string? TypeName { get; init; }
}
