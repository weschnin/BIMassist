using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Changes;

internal interface IApplyOperationDispatcher
{
    ApplyOutcome Apply(BridgeRequest request, CancellationToken cancellationToken);
}
