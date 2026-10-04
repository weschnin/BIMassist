using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Adapters;

internal sealed record ReadOperationResult(JsonElement Result, string DocumentRevision);

internal interface IReadOperationDispatcher
{
    ReadOperationResult Process(BridgeRequest request);
    ReadOperationResult Process(BridgeRequest request, CancellationToken cancellationToken) => Process(request);
    ReadOperationResult Process(BridgeRequest request, CancellationToken cancellationToken,
        DocumentParameterScanBudget budget) => Process(request, cancellationToken);
}
