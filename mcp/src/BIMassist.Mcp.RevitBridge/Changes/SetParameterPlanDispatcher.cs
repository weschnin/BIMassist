using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Adapters;

namespace BIMassist.Mcp.RevitBridge.Changes;

internal sealed record ResolvedSetParameterPlan(
    ParameterTarget Target,
    ParameterIdentity Parameter,
    ParameterValue Before,
    string DocumentRevision);

internal interface ISetParameterPlanSource
{
    ResolvedSetParameterPlan Read(BridgeRequest request);
}

internal sealed class SetParameterPlanDispatcher : IPlanOperationDispatcher
{
    private readonly ISetParameterPlanSource _source;
    private readonly ChangePlanRegistry _registry;
    private readonly Func<DateTimeOffset> _utcNow;

    internal SetParameterPlanDispatcher(
        ISetParameterPlanSource source,
        ChangePlanRegistry registry,
        Func<DateTimeOffset> utcNow)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public ChangePlan Plan(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ResolvedSetParameterPlan state = _source.Read(request);
        DateTimeOffset now = _utcNow();
        ChangePlan plan = SetParameterPlanBuilder.Build(
            request, state.Target, state.Parameter, state.Before, state.DocumentRevision, now);
        _registry.Register(plan, now);
        return plan;
    }
}
