using BIMassist.Mcp.Contracts.Reads;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record ElementReadSnapshot(
    string DocumentKey,
    string DocumentRevision,
    IReadOnlyList<ElementSummary> Elements);

internal interface IRevitElementReadSource
{
    ElementReadSnapshot ReadElements(
        string sessionId,
        string documentKey,
        ListElementsRequest request);
}
