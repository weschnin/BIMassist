using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Contracts.Tests.Sessions;

public sealed class SessionDocumentContractTests
{
    [Fact]
    public void Session_rejects_excessive_document_collection()
    {
        var document = new DocumentDescriptor
        {
            DocumentKey = "document-1",
            Title = "Test",
            PathStatus = DocumentPathStatus.Unsaved,
            IsFamilyDocument = false,
            IsWorkshared = false,
            IsReadOnly = false,
            Revision = "revision-1"
        };
        var session = new SessionDescriptor
        {
            SessionId = "session-1",
            RevitProcessId = 1,
            RevitMajor = 2026,
            BridgeVersion = "0.1.0",
            ProtocolVersion = "1",
            SchemaVersion = "1",
            StartedAtUtc = DateTimeOffset.Parse("2026-09-21T12:00:00Z"),
            Documents = Enumerable.Repeat(document, ContractLimits.MaximumSessionDocuments + 1).ToArray()
        };

        Assert.Throws<System.Text.Json.JsonException>(() => ContractJson.Serialize(session));
    }

    [Fact]
    public void Session_rejects_incompatible_protocol_and_schema_versions()
    {
        const string json = """
            {"sessionId":"session-1","revitProcessId":1,"revitMajor":2026,"bridgeVersion":"0.1.0","protocolVersion":"1","schemaVersion":"2","startedAtUtc":"2026-09-21T12:00:00Z","documents":[]}
            """;

        Assert.Throws<System.Text.Json.JsonException>(() => ContractJson.Deserialize<SessionDescriptor>(json));
    }

    [Fact]
    public void Document_rejects_empty_identity_and_revision()
    {
        const string json = """
            {"documentKey":"","title":"","pathStatus":"saved","isFamilyDocument":false,"isWorkshared":false,"isReadOnly":false,"revision":""}
            """;

        Assert.Throws<System.Text.Json.JsonException>(() => ContractJson.Deserialize<DocumentDescriptor>(json));
    }

    [Fact]
    public void Session_roundtrip_preserves_process_and_active_documents()
    {
        var session = new SessionDescriptor
        {
            SessionId = "session-1",
            RevitProcessId = 1234,
            RevitMajor = 2026,
            BridgeVersion = "0.1.0",
            ProtocolVersion = "1",
            SchemaVersion = "1",
            StartedAtUtc = DateTimeOffset.Parse("2026-09-21T12:00:00Z"),
            Documents =
            [
                new DocumentDescriptor
                {
                    DocumentKey = "document-1",
                    Title = "Mcp-ProjectParameters-2026",
                    PathStatus = DocumentPathStatus.Saved,
                    Path = "C:/Tests/Mcp-ProjectParameters-2026.rvt",
                    IsFamilyDocument = false,
                    IsWorkshared = true,
                    IsReadOnly = false,
                    ActiveViewUniqueId = "view-uid-1",
                    Revision = "revision-4"
                }
            ]
        };

        SessionDescriptor restored = ContractJson.Deserialize<SessionDescriptor>(ContractJson.Serialize(session));

        Assert.Equal(1234, restored.RevitProcessId);
        Assert.Equal("1", restored.SchemaVersion);
        Assert.Single(restored.Documents);
        Assert.Equal(DocumentPathStatus.Saved, restored.Documents[0].PathStatus);
        Assert.Equal("revision-4", restored.Documents[0].Revision);
    }
}
