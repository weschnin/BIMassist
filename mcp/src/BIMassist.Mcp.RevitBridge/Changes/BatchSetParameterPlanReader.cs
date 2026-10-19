using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Collects every batch observation before a plan can be constructed; no partial result on failure.</summary>
internal static class BatchSetParameterPlanReader
{
    internal static IReadOnlyList<ResolvedSetParameterPlan> Read(
        PlanSetStringParametersBatchRequest request, string documentKey, string expectedRevision,
        Func<StringParameterBatchItem, ResolvedSetParameterPlan> resolve,
        Func<string> currentRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        if (!string.Equals(request.DocumentKey, documentKey, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotFound);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(currentRevision);
        if (!string.Equals(currentRevision(), expectedRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        var results = new List<ResolvedSetParameterPlan>(request.Items.Count);
        foreach (StringParameterBatchItem item in request.Items)
        {
            ResolvedSetParameterPlan observation = resolve(item) ??
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            if (!string.Equals(observation.DocumentRevision, expectedRevision, StringComparison.Ordinal) ||
                !string.Equals(currentRevision(), expectedRevision, StringComparison.Ordinal))
                throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
            results.Add(observation);
        }
        return results;
    }
}
