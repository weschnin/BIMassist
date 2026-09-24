using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Reads;

public sealed record ListParametersRequest
{
    [JsonRequired]
    public required PageRequest Page { get; init; }

    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    public string? NameContains { get; init; }

    public ParameterStorageType? StorageType { get; init; }

    public ParameterBindingKind? BindingKind { get; init; }

    public bool? IsReadOnly { get; init; }
}

public sealed record ParameterSummary
{
    [JsonRequired]
    public required ParameterIdentity Identity { get; init; }

    [JsonRequired]
    public required ParameterStorageType StorageType { get; init; }

    [JsonRequired]
    public required ParameterBindingKind BindingKind { get; init; }

    [JsonRequired]
    public required bool IsReadOnly { get; init; }

    public string? BlockedReason { get; init; }
}

public sealed record GetParameterMetadataRequest
{
    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    [JsonRequired]
    public required ParameterIdentity Parameter { get; init; }
}
