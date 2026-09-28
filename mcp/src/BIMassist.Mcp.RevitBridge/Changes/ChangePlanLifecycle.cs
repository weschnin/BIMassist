using BIMassist.Mcp.RevitBridge.Documents;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Invalidates plans before a document's identity or lifetime changes.</summary>
internal sealed class ChangePlanLifecycle
{
    private readonly ChangePlanRegistry _registry;
    private readonly DocumentRevisionService _revisions;

    internal ChangePlanLifecycle(ChangePlanRegistry registry, DocumentRevisionService revisions)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    internal void Invalidate(string documentKey)
    {
        _registry.InvalidateDocument(documentKey);
        _revisions.MarkChanged(documentKey);
    }
}
