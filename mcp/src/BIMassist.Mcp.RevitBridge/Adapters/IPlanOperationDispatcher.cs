using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Adapters;

internal interface IPlanOperationDispatcher
{
    ChangePlan Plan(BridgeRequest request);
}
