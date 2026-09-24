using BIMassist.Mcp.Contracts.Reads;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record SharedDefinitionReadSnapshot(
    string DocumentKey,
    string DocumentRevision,
    IReadOnlyList<SharedDefinitionDescriptor> Definitions);

internal interface IRevitSharedDefinitionReadSource
{
    SharedDefinitionReadSnapshot ReadSharedDefinitions(string sessionId, string documentKey);
}
