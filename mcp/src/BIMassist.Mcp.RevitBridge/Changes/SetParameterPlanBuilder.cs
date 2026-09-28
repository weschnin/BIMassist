using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Changes;

internal static class SetParameterPlanBuilder
{
    internal static ChangePlan Build(
        BridgeRequest request,
        ParameterTarget actualTarget,
        ParameterIdentity actualIdentity,
        ParameterValue actualBefore,
        string currentRevision,
        DateTimeOffset now)
    {
        ContractValidator.Validate(request);
        if (request.Operation != BridgeOperations.PlanSetParameterValues)
            throw new ChangePlanFailure(BridgeErrorCodes.OperationNotSupported);
        var payload = ContractJson.Deserialize<PlanSetParameterValueRequest>(request.Payload.GetRawText());
        ContractValidator.Validate(actualTarget);
        ContractValidator.Validate(actualIdentity);
        ContractValidator.Validate(actualBefore);
        if (!string.Equals(currentRevision, request.ExpectedRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        if (actualTarget.Kind != payload.Target.Kind ||
            (payload.Target.UniqueId is not null &&
             !string.Equals(payload.Target.UniqueId, actualTarget.UniqueId, StringComparison.Ordinal)) ||
            (payload.Target.ElementId is not null && payload.Target.ElementId != actualTarget.ElementId))
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        if (actualIdentity.Kind != payload.Parameter.Kind ||
            actualIdentity.Kind switch
            {
                ParameterIdentityKind.SharedGuid => actualIdentity.SharedGuid != payload.Parameter.SharedGuid,
                ParameterIdentityKind.BuiltIn => actualIdentity.BuiltInId != payload.Parameter.BuiltInId,
                _ => true
            })
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        if (actualBefore.IsReadOnly)
            throw new ChangePlanFailure(BridgeErrorCodes.ParameterReadOnly);
        if (actualBefore.Kind is not (ParameterValueKind.String or ParameterValueKind.None))
            throw new ChangePlanFailure(BridgeErrorCodes.TypeMismatch);
        if (actualBefore.HasValue && string.Equals(actualBefore.StringValue, payload.After.StringValue, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
        var plan = new ChangePlan
        {
            PlanId = $"plan-{Guid.NewGuid():N}",
            SessionId = request.SessionId!,
            DocumentKey = request.DocumentKey!,
            ExpectedRevision = currentRevision,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(5),
            Warnings = [],
            Operations = [new ChangeOperation
            {
                OperationId = $"operation-{Guid.NewGuid():N}",
                Kind = ChangeOperationKind.SetParameterValue,
                Target = actualTarget,
                Parameter = actualIdentity,
                Before = actualBefore,
                After = payload.After,
                Options = new Dictionary<string, string>()
            }],
            PlanHash = new string('0', 64)
        };
        ChangePlan hashed = plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
        ContractValidator.Validate(hashed);
        return hashed;
    }
}
