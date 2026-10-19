using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Unwired batch-plan foundation. Never registers or applies a plan.</summary>
internal static class BatchSetParameterPlanBuilder
{
    // Leave room for the response's request ID, revision, warnings, duration and JSON framing.
    private const int ResponseEnvelopeHeadroomBytes = 4_096;

    internal static ChangePlan Build(PlanSetStringParametersBatchRequest request,
        string sessionId, string documentKey, string expectedRevision,
        IReadOnlyList<ResolvedSetParameterPlan> observations, DateTimeOffset now, string documentTitle)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        if (!string.Equals(request.DocumentKey, documentKey, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotFound);
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > ContractLimits.MaximumIdentifierLength ||
            string.IsNullOrWhiteSpace(expectedRevision) || expectedRevision.Length > ContractLimits.MaximumRevisionLength)
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
        if (observations is null || observations.Count != request.Items.Count)
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);

        var operations = new List<ChangeOperation>(request.Items.Count);
        var seen = new HashSet<(string OwnerUniqueId, long ParameterId)>();
        for (int index = 0; index < request.Items.Count; index++)
        {
            StringParameterBatchItem item = request.Items[index];
            ResolvedSetParameterPlan actual = observations[index] ??
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            ContractValidator.ValidateBatchTarget(actual.Target);
            ContractValidator.ValidateBatchIdentity(actual.Parameter);
            ContractValidator.Validate(actual.Before);
            if (!string.Equals(expectedRevision, actual.DocumentRevision, StringComparison.Ordinal))
                throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
            if (actual.Target.Kind != item.Target.Kind ||
                !string.Equals(actual.Target.UniqueId, item.Target.UniqueId, StringComparison.Ordinal) ||
                actual.Target.ElementId != item.Target.ElementId)
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            // Document targets and ProjectInformation element targets share one actual Revit owner.
            // Never derive this identity from client-supplied target identifiers.
            string ownerUniqueId = actual.CanonicalOwnerUniqueId ?? "";
            if (string.IsNullOrWhiteSpace(ownerUniqueId) ||
                (actual.Target.Kind != ParameterTargetKind.Document &&
                 !string.Equals(ownerUniqueId, actual.Target.UniqueId, StringComparison.Ordinal)))
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            ParameterIdentity selected = actual.Parameter;
            if (selected.Kind != item.Parameter.Kind || selected.Kind switch
                {
                    ParameterIdentityKind.SharedGuid => selected.SharedGuid != item.Parameter.SharedGuid,
                    ParameterIdentityKind.BuiltIn => selected.BuiltInId != item.Parameter.BuiltInId,
                    ParameterIdentityKind.ParameterElement =>
                        !selected.StableId.StartsWith("parameter-element:", StringComparison.Ordinal) ||
                        selected.DefinitionId != item.Parameter.DefinitionId ||
                        !string.Equals(selected.OwnerContext, $"document:{documentKey}", StringComparison.Ordinal) ||
                        !string.Equals(selected.OwnerContext, item.Parameter.OwnerContext, StringComparison.Ordinal) ||
                        !string.Equals(selected.DataTypeId, item.Parameter.DataTypeId, StringComparison.Ordinal),
                    _ => true
                })
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            long? parameterId = actual.CanonicalParameterId;
            if (parameterId is null ||
                (selected.Kind == ParameterIdentityKind.BuiltIn && parameterId != selected.BuiltInId) ||
                (selected.Kind == ParameterIdentityKind.ParameterElement && parameterId != selected.DefinitionId))
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            if (actual.Before.BlockingReason == "element_owned_by_other_user")
                throw new ChangePlanFailure(BridgeErrorCodes.WorksharingOwnership);
            if (actual.Before.IsReadOnly || actual.Before.BlockingReason is not null)
                throw new ChangePlanFailure(BridgeErrorCodes.ParameterReadOnly);
            if (actual.Before.Kind is not (ParameterValueKind.String or ParameterValueKind.None))
                throw new ChangePlanFailure(BridgeErrorCodes.TypeMismatch);
            if (actual.Before.HasValue && string.Equals(actual.Before.StringValue, item.NewString, StringComparison.Ordinal))
                throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
            if (!seen.Add((ownerUniqueId, parameterId.Value)))
                throw new ChangePlanFailure(BridgeErrorCodes.AmbiguousTarget);
            operations.Add(new ChangeOperation
            {
                OperationId = $"operation-{index + 1}",
                Kind = ChangeOperationKind.SetParameterValue,
                Target = actual.Target, Parameter = selected, Before = actual.Before,
                After = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                    IsReadOnly = false, StringValue = item.NewString },
                Options = new Dictionary<string, string>()
            });
        }
        var plan = new ChangePlan
        {
            PlanId = "plan-batch-draft", SessionId = sessionId,
            DocumentKey = documentKey, ExpectedRevision = expectedRevision,
            CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(5),
            Operations = operations, Warnings = [], PlanHash = new string('0', 64)
        };
        // Require the same batch shape that approval and Apply accept before issuing a hash.
        BatchStringWritePreconditions.Validate(plan);
        // Stable content-derived identity; array order is deliberately preserved in the hash.
        string contentHash = ChangePlanHasher.ComputeHash(plan);
        plan = plan with { PlanId = $"plan-{contentHash[..32]}" };
        ChangePlan hashed = plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
        ContractValidator.Validate(hashed);
        // Serialize with exactly the contract's JSON options; a valid collection of bounded
        // values may still exceed the complete plan/response byte limit.
        if (JsonSerializer.SerializeToUtf8Bytes(hashed, ContractJson.Options).Length >
            ContractLimits.MaximumContractBytes - ResponseEnvelopeHeadroomBytes)
            throw new ChangePlanFailure(BridgeErrorCodes.LimitExceeded);
        // Approval uses the entire hashed plan plus the real, escaped Revit title and a
        // second fixed hash region. Never issue a plan that the dialog cannot display.
        try
        {
            _ = BatchApplyApprovalPrompt.Format(hashed, documentTitle);
        }
        catch (ChangePlanFailure failure) when (failure.Code == BridgeErrorCodes.InvalidRequest)
        {
            throw new ChangePlanFailure(BridgeErrorCodes.LimitExceeded);
        }
        return hashed;
    }
}
