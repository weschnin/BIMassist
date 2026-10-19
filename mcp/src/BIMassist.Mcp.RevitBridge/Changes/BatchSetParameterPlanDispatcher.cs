using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Adapters;

namespace BIMassist.Mcp.RevitBridge.Changes;

internal interface IBatchSetParameterPlanSource
{
    ChangePlan BuildBatchPlan(PlanSetStringParametersBatchRequest payload, string sessionId,
        string documentKey, string expectedRevision, DateTimeOffset now);
}

/// <summary>Plan-only batch dispatcher. Observations come from the serialized Revit callback;
/// a plan is registered only after its complete response passes the transport limit.</summary>
internal sealed class BatchSetParameterPlanDispatcher : IPlanOperationDispatcher
{
    private readonly Func<PlanSetStringParametersBatchRequest, string, string, string, DateTimeOffset, ChangePlan> _build;
    private readonly ChangePlanRegistry _registry;
    private readonly Func<DateTimeOffset> _utcNow;

    internal BatchSetParameterPlanDispatcher(IBatchSetParameterPlanSource source,
        ChangePlanRegistry registry, Func<DateTimeOffset> utcNow)
        : this((source ?? throw new ArgumentNullException(nameof(source))).BuildBatchPlan, registry, utcNow)
    {
    }

    internal BatchSetParameterPlanDispatcher(
        Func<PlanSetStringParametersBatchRequest, string, string, string, DateTimeOffset, ChangePlan> build,
        ChangePlanRegistry registry, Func<DateTimeOffset> utcNow)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public ChangePlan Plan(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        if (request.Operation != BridgeOperations.PlanSetStringParametersBatch)
            throw new ChangePlanFailure(BridgeErrorCodes.OperationNotSupported);
        PlanSetStringParametersBatchRequest payload =
            ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(request.Payload.GetRawText());
        DateTimeOffset now = _utcNow();
        ChangePlan plan = _build(payload, request.SessionId!, request.DocumentKey!, request.ExpectedRevision!, now);
        if (!string.Equals(plan.SessionId, request.SessionId, StringComparison.Ordinal) ||
            !string.Equals(plan.DocumentKey, request.DocumentKey, StringComparison.Ordinal) ||
            !string.Equals(plan.ExpectedRevision, request.ExpectedRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        ContractValidator.Validate(plan);
        // Measure the actual response shape before any registry mutation. Long.MaxValue
        // covers the maximum serialized duration that the processor can return.
        var response = new BridgeResponse
        {
            RequestId = request.RequestId, Success = true,
            Result = JsonSerializer.SerializeToElement(plan, ContractJson.Options),
            Warnings = [], DocumentRevision = plan.ExpectedRevision, DurationMs = long.MaxValue
        };
        if (JsonSerializer.SerializeToUtf8Bytes(response, ContractJson.Options).Length > ContractLimits.MaximumContractBytes)
            throw new ChangePlanFailure(BridgeErrorCodes.LimitExceeded);
        _registry.Register(plan, now);
        return plan;
    }
}
