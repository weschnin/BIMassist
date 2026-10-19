using BIMassist.Mcp.RevitBridge.Documents;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Invalidates plans before a document's identity or lifetime changes.</summary>
internal sealed class ChangePlanLifecycle
{
    private readonly ChangePlanRegistry _registry;
    private readonly ChangePlanRegistry? _batchRegistry;
    private readonly DocumentRevisionService _revisions;

    internal ChangePlanLifecycle(ChangePlanRegistry registry, DocumentRevisionService revisions)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    internal ChangePlanLifecycle(ChangePlanRegistry registry, ChangePlanRegistry batchRegistry,
        DocumentRevisionService revisions)
        : this(registry, revisions)
    {
        _batchRegistry = batchRegistry ?? throw new ArgumentNullException(nameof(batchRegistry));
    }

    internal void Invalidate(string documentKey)
    {
        _registry.InvalidateDocument(documentKey);
        _batchRegistry?.InvalidateDocument(documentKey);
        _revisions.MarkChanged(documentKey);
    }
}
