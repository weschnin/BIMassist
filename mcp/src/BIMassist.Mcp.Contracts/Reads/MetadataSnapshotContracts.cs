using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

public sealed record ExportMetadataSnapshotRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    [JsonRequired]
    public required MetadataSnapshotSelection Selection { get; init; }
}

public sealed record MetadataSnapshotSelection
{
    [JsonRequired]
    public required bool IncludeFamilies { get; init; }

    [JsonRequired]
    public required bool IncludeSharedDefinitions { get; init; }

    [JsonRequired]
    public required bool IncludeProjectBindings { get; init; }

    [JsonRequired]
    public required IReadOnlyList<ParameterTarget> ParameterTargets { get; init; }

    [JsonRequired]
    public required bool IncludeParameterValues { get; init; }
}

public enum MetadataSnapshotItemKind
{
    Family,
    FamilyType,
    ParameterMetadata,
    SharedDefinition,
    ProjectBinding,
    ParameterValue
}

public sealed record MetadataSnapshotItem
{
    [JsonRequired]
    public required MetadataSnapshotItemKind Kind { get; init; }

    public FamilySummary? Family { get; init; }

    public FamilyTypeSnapshotItem? FamilyType { get; init; }

    public ParameterMetadata? ParameterMetadata { get; init; }

    public SharedDefinitionDescriptor? SharedDefinition { get; init; }

    public ProjectBindingDescriptor? ProjectBinding { get; init; }

    public ParameterValueEntry? ParameterValue { get; init; }
}

public sealed record FamilyTypeSnapshotItem
{
    [JsonRequired]
    public required string FamilyUniqueId { get; init; }

    [JsonRequired]
    public required FamilyTypeSummary Type { get; init; }
}

public sealed record MetadataSnapshotPage
{
    [JsonRequired]
    public required string SnapshotId { get; init; }

    [JsonRequired]
    public required string DocumentKey { get; init; }

    [JsonRequired]
    public required string DocumentRevision { get; init; }

    [JsonRequired]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    [JsonRequired]
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    [JsonRequired]
    public required PageResult<MetadataSnapshotItem> Page { get; init; }
}
