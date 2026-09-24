using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

public sealed record ListFamiliesRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    public string? NameContains { get; init; }

    [JsonRequired]
    public required bool IncludeInPlace { get; init; }
}

public sealed record GetFamilyMetadataRequest
{
    [JsonRequired]
    public required string FamilyUniqueId { get; init; }

    [JsonRequired]
    public required PageRequest Page { get; init; }
}

public sealed record FamilySummary
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
    public required bool IsInPlace { get; init; }

    [JsonRequired]
    public required bool IsEditable { get; init; }

    [JsonRequired]
    public required bool IsShared { get; init; }

    [JsonRequired]
    public required int TypeCount { get; init; }
}

public sealed record FamilyTypeSummary
{
    [JsonRequired]
    public required string UniqueId { get; init; }

    [JsonRequired]
    public required long ElementId { get; init; }

    [JsonRequired]
    public required string Name { get; init; }
}

public sealed record FamilyMetadata
{
    [JsonRequired]
    public required FamilySummary Family { get; init; }

    [JsonRequired]
    public required PageResult<FamilyTypeSummary> Types { get; init; }
}
