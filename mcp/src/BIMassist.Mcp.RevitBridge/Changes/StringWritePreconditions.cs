using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Changes;

internal static class StringWritePreconditions
{
    internal static ChangeOperation Single(IReadOnlyList<ChangeOperation> operations)
    {
        if (operations.Count != 1 || operations[0].Kind != ChangeOperationKind.SetParameterValue ||
            operations[0].Options.Count != 0 ||
            operations[0].Parameter.Kind is not (ParameterIdentityKind.SharedGuid or ParameterIdentityKind.BuiltIn or ParameterIdentityKind.ParameterElement) ||
            operations[0].Before?.Kind is not (ParameterValueKind.String or ParameterValueKind.None) ||
            operations[0].After?.Kind != ParameterValueKind.String ||
            operations[0].After?.HasValue != true || operations[0].After?.StringValue is null)
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
        return operations[0];
    }

    internal static void Check(ChangeOperation operation, string? current, bool hasValue)
    {
        if (operation.Before is null || operation.Before.HasValue != hasValue ||
            (hasValue && !string.Equals(operation.Before.StringValue, current, StringComparison.Ordinal)))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
    }
}
