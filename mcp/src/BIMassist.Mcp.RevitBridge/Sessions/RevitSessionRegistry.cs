using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;

namespace BIMassist.Mcp.RevitBridge.Sessions;

public sealed class RevitSessionRegistry
{
    private readonly object _sync = new();
    private readonly SessionDescriptor _session;
    private IReadOnlyList<DocumentDescriptor> _documents;
    private bool _shuttingDown;

    public RevitSessionRegistry(SessionDescriptor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ContractValidator.Validate(session);
        EnsureUniqueDocumentKeys(session.Documents);
        _documents = session.Documents.ToArray();
        _session = session with { Documents = _documents };
    }

    public SessionDescriptor GetSnapshot()
    {
        lock (_sync)
        {
            return CreateSnapshot();
        }
    }

    public bool TryGetSnapshot(string sessionId, out SessionDescriptor? snapshot)
    {
        lock (_sync)
        {
            if (_shuttingDown || !string.Equals(_session.SessionId, sessionId, StringComparison.Ordinal))
            {
                snapshot = null;
                return false;
            }

            snapshot = CreateSnapshot();
            return true;
        }
    }

    public void ReplaceDocuments(IReadOnlyList<DocumentDescriptor> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (DocumentDescriptor document in documents)
        {
            ContractValidator.Validate(document);
        }

        EnsureUniqueDocumentKeys(documents);
        lock (_sync)
        {
            if (_shuttingDown)
            {
                throw new InvalidOperationException("The Revit session registry is shutting down.");
            }

            _documents = documents.ToArray();
        }
    }

    public void BeginShutdown()
    {
        lock (_sync)
        {
            _shuttingDown = true;
            _documents = [];
        }
    }

    private SessionDescriptor CreateSnapshot() => _session with { Documents = _documents.ToArray() };

    private static void EnsureUniqueDocumentKeys(IReadOnlyList<DocumentDescriptor> documents)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (DocumentDescriptor document in documents)
        {
            if (document is null || !keys.Add(document.DocumentKey))
            {
                throw new ArgumentException("Document keys must be non-null and unique.", nameof(documents));
            }
        }
    }
}
