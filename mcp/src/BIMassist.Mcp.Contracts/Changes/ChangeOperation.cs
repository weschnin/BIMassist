using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Parameters;

namespace BIMassist.Mcp.Contracts.Changes;

public enum ChangeOperationKind
{
    SetParameterValue,
    AddSharedParameterToFamily,
    BindSharedParameter
}

public sealed record ChangeOperation
{
    [JsonRequired]
    public required string OperationId { get; init; }

    [JsonRequired]
    public required ChangeOperationKind Kind { get; init; }

    [JsonRequired]
    public required ParameterTarget Target { get; init; }

    [JsonRequired]
    public required ParameterIdentity Parameter { get; init; }

    public ParameterValue? Before { get; init; }

    public ParameterValue? After { get; init; }

    public ParameterBindingState? BeforeBinding { get; init; }

    public ParameterBindingState? AfterBinding { get; init; }

    public SharedParameterDefinitionState? BeforeDefinition { get; init; }

    public SharedParameterDefinitionState? AfterDefinition { get; init; }

    [JsonRequired]
    public required IReadOnlyDictionary<string, string> Options { get; init; }
}

public sealed record ParameterBindingState
{
    [JsonRequired]
    public required ParameterBindingKind Kind { get; init; }

    [JsonRequired]
    public required IReadOnlyList<string> CategoryIds { get; init; }

    public string? GroupTypeId { get; init; }
}

public sealed record SharedParameterDefinitionState
{
    [JsonRequired]
    public required Guid SharedGuid { get; init; }

    [JsonRequired]
    public required string Name { get; init; }

    [JsonRequired]
    public required string DataTypeId { get; init; }

    [JsonRequired]
    public required string GroupName { get; init; }

    [JsonRequired]
    public required bool IsInstance { get; init; }
}
