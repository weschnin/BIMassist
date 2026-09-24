using System.Text.Json;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Adapters;

public sealed class BridgeRequestProcessorTests
{
    [Fact]
    public void Status_returns_current_session_snapshot()
    {
        SessionDescriptor session = CreateSession();
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]));

        BridgeResponse response = processor.Process(CreateRequest(BridgeOperations.GetStatus));

        Assert.True(response.Success);
        Assert.Null(response.Error);
        SessionDescriptor result = ContractJson.Deserialize<SessionDescriptor>(response.Result!.Value.GetRawText());
        Assert.Equal(session.SessionId, result.SessionId);
        Assert.Single(result.Documents);
    }

    [Fact]
    public void Document_context_requires_exact_session()
    {
        SessionDescriptor session = CreateSession();
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]));
        BridgeRequest request = CreateRequest(BridgeOperations.GetDocumentContext) with { SessionId = "stale-session" };

        BridgeResponse response = processor.Process(request);

        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.SessionNotFound, response.Error?.Code);
        Assert.Null(response.Result);
    }

    [Fact]
    public void Document_context_returns_machine_readable_no_active_document()
    {
        SessionDescriptor session = CreateSession();
        var processor = new BridgeRequestProcessor(new FakeContext(session, null));
        BridgeRequest request = CreateRequest(BridgeOperations.GetDocumentContext) with { SessionId = session.SessionId };

        BridgeResponse response = processor.Process(request);

        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.NoActiveDocument, response.Error?.Code);
    }

    [Fact]
    public void Read_operation_dispatches_exact_document_and_returns_revision()
    {
        SessionDescriptor session = CreateSession();
        var page = new PageResult<FamilySummary>
        {
            Items =
            [
                new FamilySummary
                {
                    UniqueId = "family-uid-1",
                    ElementId = 42,
                    Name = "Door - Single",
                    IsInPlace = false,
                    IsEditable = true,
                    IsShared = false,
                    TypeCount = 1
                }
            ],
            TotalCount = 1
        };
        var reads = new FakeReadDispatcher(new ReadOperationResult(
            JsonSerializer.SerializeToElement(page, ContractJson.Options),
            session.Documents[0].Revision));
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]), reads);
        BridgeRequest request = CreateRequest(BridgeOperations.ListFamilies) with
        {
            SessionId = session.SessionId,
            DocumentKey = session.Documents[0].DocumentKey,
            Payload = JsonDocument.Parse("{\"page\":{\"pageSize\":25},\"includeInPlace\":false}").RootElement.Clone()
        };

        BridgeResponse response = processor.Process(request);

        Assert.True(response.Success);
        Assert.Equal(session.Documents[0].Revision, response.DocumentRevision);
        Assert.Equal(BridgeOperations.ListFamilies, reads.LastRequest?.Operation);
        Assert.Single(ContractJson.Deserialize<PageResult<FamilySummary>>(response.Result!.Value.GetRawText()).Items);
    }

    [Fact]
    public void Read_operation_maps_machine_readable_failures_without_leaking_exception_text()
    {
        SessionDescriptor session = CreateSession();
        var processor = new BridgeRequestProcessor(
            new FakeContext(session, session.Documents[0]),
            new ThrowingReadDispatcher());
        BridgeRequest request = CreateRequest(BridgeOperations.ListFamilies) with
        {
            SessionId = session.SessionId,
            DocumentKey = session.Documents[0].DocumentKey,
            Payload = JsonDocument.Parse("{\"page\":{\"pageSize\":25},\"includeInPlace\":false}").RootElement.Clone()
        };

        BridgeResponse response = processor.Process(request);

        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.DocumentChanged, response.Error?.Code);
        Assert.DoesNotContain("secret", response.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Processor_rejects_unimplemented_operations()
    {
        SessionDescriptor session = CreateSession();
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]));
        BridgeRequest request = CreateRequest(BridgeOperations.PlanSetParameterValues) with
        {
            SessionId = session.SessionId,
            DocumentKey = session.Documents[0].DocumentKey,
            ExpectedRevision = session.Documents[0].Revision,
            IdempotencyKey = "idempotency-1"
        };

        BridgeResponse response = processor.Process(request);

        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.OperationNotSupported, response.Error?.Code);
    }

    private static BridgeRequest CreateRequest(string operation) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-1",
        Operation = operation,
        Payload = JsonDocument.Parse("{}").RootElement.Clone()
    };

    private static SessionDescriptor CreateSession()
    {
        var document = new DocumentDescriptor
        {
            DocumentKey = "doc-1",
            Title = "Model",
            PathStatus = DocumentPathStatus.Saved,
            Path = "C:\\Models\\Model.rvt",
            IsFamilyDocument = false,
            IsWorkshared = false,
            IsReadOnly = false,
            Revision = "session-1:0"
        };
        return new SessionDescriptor
        {
            SessionId = "session-1",
            RevitProcessId = 1234,
            RevitMajor = 2026,
            BridgeVersion = "0.1.0",
            ProtocolVersion = ProtocolVersions.ProtocolVersion,
            SchemaVersion = ProtocolVersions.SchemaVersion,
            StartedAtUtc = DateTimeOffset.Parse("2026-09-23T10:00:00Z"),
            Documents = [document]
        };
    }

    private sealed class ThrowingReadDispatcher : IReadOperationDispatcher
    {
        public ReadOperationResult Process(BridgeRequest request) =>
            throw new ReadCursorException(BridgeErrorCodes.DocumentChanged);
    }

    private sealed class FakeReadDispatcher(ReadOperationResult result) : IReadOperationDispatcher
    {
        public BridgeRequest? LastRequest { get; private set; }

        public ReadOperationResult Process(BridgeRequest request)
        {
            LastRequest = request;
            return result;
        }
    }

    private sealed class FakeContext(SessionDescriptor session, DocumentDescriptor? activeDocument)
        : IRevitContextSnapshotProvider
    {
        public SessionDescriptor GetSessionSnapshot() => session;

        public DocumentDescriptor? GetActiveDocument() => activeDocument;
    }
}
