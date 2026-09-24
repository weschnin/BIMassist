using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Adapters;

internal sealed record ReadOperationResult(JsonElement Result, string DocumentRevision);

internal interface IReadOperationDispatcher
{
    ReadOperationResult Process(BridgeRequest request);
}
