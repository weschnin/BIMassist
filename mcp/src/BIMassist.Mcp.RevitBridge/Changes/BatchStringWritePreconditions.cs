using System.Text;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>
/// Pure, batch-only shape checks. Canonical owner/parameter deduplication requires actual Revit
/// identities; a DTO cannot prove that two differently described targets are not aliases.
/// Live before-value comparisons must use StringWritePreconditions.Check after resolution.
/// </summary>
internal static class BatchStringWritePreconditions
{
    internal static IReadOnlyList<ChangeOperation> Validate(ChangePlan plan)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.DocumentKey) ||
            plan.DocumentKey.EnumerateRunes().Count() > ContractLimits.MaximumDocumentKeyLength ||
            plan.Operations is null || plan.Operations.Count is < 2 or > ContractLimits.MaximumStringBatchItems)
            throw Invalid();

        var seenOperationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChangeOperation operation in plan.Operations)
        {
            if (operation is null || !seenOperationIds.Add(operation.OperationId ?? "") ||
                operation.Kind != ChangeOperationKind.SetParameterValue ||
                operation.Options is null || operation.Options.Count != 0 ||
                operation.Target is null || operation.Parameter is null ||
                operation.Before is null || operation.After is null ||
                operation.BeforeBinding is not null || operation.AfterBinding is not null ||
                operation.BeforeDefinition is not null || operation.AfterDefinition is not null)
                throw Invalid();

            // Reuse contract leaf checks for bounded identifiers, discriminator payloads and IDs,
            // without validating unrelated plan hash/expiry metadata here.
            try
            {
                ContractValidator.ValidateBatchTarget(operation.Target);
                ContractValidator.ValidateBatchIdentity(operation.Parameter);
                ContractValidator.Validate(operation.Before);
                ContractValidator.ValidateBatchStringAfterValue(operation.After);
            }
            catch (JsonException)
            {
                throw Invalid();
            }

            ParameterTarget target = operation.Target;
            ParameterIdentity parameter = operation.Parameter;
            ParameterValue before = operation.Before;
            ParameterValue after = operation.After;
            if (string.IsNullOrWhiteSpace(operation.OperationId) ||
                operation.OperationId.Length > ContractLimits.MaximumIdentifierLength ||
                target.Kind is not (ParameterTargetKind.Document or ParameterTargetKind.Element or ParameterTargetKind.ElementType) ||
                (target.Kind != ParameterTargetKind.Document &&
                 (string.IsNullOrWhiteSpace(target.UniqueId) || target.ElementId is null or <= 0)) ||
                parameter.Kind is not (ParameterIdentityKind.SharedGuid or ParameterIdentityKind.BuiltIn or ParameterIdentityKind.ParameterElement) ||
                (parameter.Kind == ParameterIdentityKind.ParameterElement &&
                 (!parameter.StableId.StartsWith("parameter-element:", StringComparison.Ordinal) ||
                  !string.Equals(parameter.OwnerContext, $"document:{plan.DocumentKey}", StringComparison.Ordinal))) ||
                before.Kind is not (ParameterValueKind.None or ParameterValueKind.String) ||
                (before.Kind == ParameterValueKind.None && before.HasValue) ||
                (before.HasValue && before.StringValue is null) ||
                after.Kind != ParameterValueKind.String || !after.HasValue || after.StringValue is null ||
                before.IsReadOnly || after.IsReadOnly ||
                before.BlockingReason is not null || after.BlockingReason is not null ||
                (before.HasValue && string.Equals(before.StringValue, after.StringValue, StringComparison.Ordinal)))
                throw Invalid();
        }
        return plan.Operations;
    }

    private static ChangePlanFailure Invalid() => new(BridgeErrorCodes.InvalidRequest);
}
