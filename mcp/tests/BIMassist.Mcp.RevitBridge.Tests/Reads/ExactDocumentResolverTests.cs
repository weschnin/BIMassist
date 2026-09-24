using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class ExactDocumentResolverTests
{
    [Fact]
    public void Resolves_only_the_exact_document_key()
    {
        DocumentCandidate[] documents = [new("doc-1"), new("doc-2")];

        DocumentCandidate result = ExactDocumentResolver.Resolve(documents, "doc-2", document => document.Key);

        Assert.Equal("doc-2", result.Key);
    }

    [Fact]
    public void Missing_document_never_falls_back_to_another_open_document()
    {
        DocumentCandidate[] documents = [new("active-doc")];

        ReadCursorException error = Assert.Throws<ReadCursorException>(() =>
            ExactDocumentResolver.Resolve(documents, "missing-doc", document => document.Key));

        Assert.Equal(BridgeErrorCodes.DocumentNotFound, error.ErrorCode);
    }

    private sealed record DocumentCandidate(string Key);
}
