using BIMassist.Mcp.RevitBridge.Documents;

namespace BIMassist.Mcp.RevitBridge.Tests.Documents;

public sealed class DocumentRevisionServiceTests
{
    [Fact]
    public void Revision_is_monotonic_per_document_and_session_bound()
    {
        var revisions = new DocumentRevisionService("session-1");

        Assert.Equal("session-1:0", revisions.GetCurrent("doc-1"));
        Assert.Equal("session-1:1", revisions.MarkChanged("doc-1"));
        Assert.Equal("session-1:2", revisions.MarkChanged("doc-1"));
        Assert.Equal("session-1:0", revisions.GetCurrent("doc-2"));
    }

    [Fact]
    public void Forget_removes_closed_document_revision_state()
    {
        var revisions = new DocumentRevisionService("session-1");
        revisions.MarkChanged("doc-1");

        revisions.Forget("doc-1");

        Assert.Equal("session-1:0", revisions.GetCurrent("doc-1"));
    }
}
