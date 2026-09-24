using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Tests.Sessions;

public sealed class RevitSessionRegistryTests
{
    [Fact]
    public void Registry_publishes_an_immutable_current_snapshot()
    {
        var registry = new RevitSessionRegistry(CreateSession([]));
        var document = CreateDocument("doc-1");

        registry.ReplaceDocuments([document]);
        SessionDescriptor snapshot = registry.GetSnapshot();

        Assert.Equal("session-1", snapshot.SessionId);
        Assert.Single(snapshot.Documents);
        Assert.Equal("doc-1", snapshot.Documents[0].DocumentKey);
        Assert.NotSame(snapshot.Documents, registry.GetSnapshot().Documents);
    }

    [Fact]
    public void Registry_rejects_duplicate_document_keys()
    {
        var registry = new RevitSessionRegistry(CreateSession([]));

        Assert.Throws<ArgumentException>(() =>
            registry.ReplaceDocuments([CreateDocument("doc-1"), CreateDocument("doc-1")]));
    }

    [Fact]
    public void Registry_rejects_requests_after_shutdown_begins()
    {
        var registry = new RevitSessionRegistry(CreateSession([]));
        registry.BeginShutdown();

        Assert.False(registry.TryGetSnapshot("session-1", out _));
        Assert.Throws<InvalidOperationException>(() => registry.ReplaceDocuments([]));
    }

    [Fact]
    public void Registry_requires_exact_session_identity()
    {
        var registry = new RevitSessionRegistry(CreateSession([]));

        Assert.True(registry.TryGetSnapshot("session-1", out SessionDescriptor? matching));
        Assert.NotNull(matching);
        Assert.False(registry.TryGetSnapshot("SESSION-1", out _));
    }

    private static SessionDescriptor CreateSession(IReadOnlyList<DocumentDescriptor> documents) => new()
    {
        SessionId = "session-1",
        RevitProcessId = 1234,
        RevitMajor = 2026,
        BridgeVersion = "0.1.0",
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        StartedAtUtc = DateTimeOffset.Parse("2026-09-23T10:00:00Z"),
        Documents = documents
    };

    private static DocumentDescriptor CreateDocument(string key) => new()
    {
        DocumentKey = key,
        Title = "Model",
        PathStatus = DocumentPathStatus.Saved,
        Path = "C:\\Models\\Model.rvt",
        IsFamilyDocument = false,
        IsWorkshared = false,
        IsReadOnly = false,
        Revision = "1"
    };
}
