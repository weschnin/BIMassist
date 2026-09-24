using BIMassist.Mcp.Contracts.Reads;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record ProjectBindingReadSnapshot(
    string DocumentKey,
    string DocumentRevision,
    IReadOnlyList<ProjectBindingDescriptor> Bindings);

internal interface IRevitProjectBindingReadSource
{
    ProjectBindingReadSnapshot ReadProjectBindings(string sessionId, string documentKey);
}
