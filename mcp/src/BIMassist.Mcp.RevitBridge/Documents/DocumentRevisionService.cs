using System.Collections.Concurrent;

namespace BIMassist.Mcp.RevitBridge.Documents;

public sealed class DocumentRevisionService
{
    private readonly string _sessionId;
    private readonly ConcurrentDictionary<string, long> _revisions = new(StringComparer.Ordinal);

    public DocumentRevisionService(string sessionId)
    {
        _sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("A session ID is required.", nameof(sessionId))
            : sessionId;
    }

    public string GetCurrent(string documentKey)
    {
        ValidateDocumentKey(documentKey);
        long revision = _revisions.GetOrAdd(documentKey, 0);
        return Format(revision);
    }

    public string MarkChanged(string documentKey)
    {
        ValidateDocumentKey(documentKey);
        long revision = _revisions.AddOrUpdate(documentKey, 1, static (_, current) => checked(current + 1));
        return Format(revision);
    }

    public void Forget(string documentKey)
    {
        ValidateDocumentKey(documentKey);
        _revisions.TryRemove(documentKey, out _);
    }

    private string Format(long revision) => $"{_sessionId}:{revision}";

    private static void ValidateDocumentKey(string documentKey)
    {
        if (string.IsNullOrWhiteSpace(documentKey))
        {
            throw new ArgumentException("A document key is required.", nameof(documentKey));
        }
    }
}
