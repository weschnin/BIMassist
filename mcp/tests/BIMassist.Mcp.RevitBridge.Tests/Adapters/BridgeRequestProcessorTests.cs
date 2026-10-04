using System.Diagnostics;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Parameters;
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
            IdempotencyKey = "idempotency-1",
            Payload = JsonDocument.Parse("""{"target":{"kind":"element","uniqueId":"uid-1"},"parameter":{"kind":"builtIn","builtInId":-1001203,"stableId":"builtin:description","name":"Description"},"after":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"new"}}""").RootElement.Clone()
        };

        BridgeResponse response = processor.Process(request);

        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.OperationNotSupported, response.Error?.Code);
    }

    [Fact]
    public void Search_rejects_cancellation_before_dispatch_with_structured_limit()
    {
        SessionDescriptor session = CreateSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reads = new CancellationAwareReadDispatcher();
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]), reads);
        BridgeResponse response = processor.Process(SearchRequest(session), cancellation.Token);
        Assert.False(reads.ObservedCancellation);
        Assert.Equal(BridgeErrorCodes.LimitExceeded, response.Error?.Code);
    }

    [Fact]
    public void Search_budget_includes_callback_setup_before_processor_execution()
    {
        SessionDescriptor session = CreateSession();
        var reads = new FakeReadDispatcher(new ReadOperationResult(JsonDocument.Parse("{}").RootElement.Clone(),
            session.Documents[0].Revision));
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]), reads);
        long callbackStarted = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 6);
        BridgeResponse response = processor.Process(SearchRequest(session), CancellationToken.None, callbackStarted);
        Assert.Equal(BridgeErrorCodes.LimitExceeded, response.Error?.Code);
        Assert.Null(reads.LastRequest);
    }

    [Fact]
    public void Search_cancelled_during_session_resolution_never_dispatches()
    {
        SessionDescriptor session = CreateSession();
        using var cancellation = new CancellationTokenSource();
        var reads = new FakeReadDispatcher(new ReadOperationResult(JsonDocument.Parse("{}").RootElement.Clone(),
            session.Documents[0].Revision));
        var processor = new BridgeRequestProcessor(new CancellingContext(session, cancellation), reads);
        BridgeResponse response = processor.Process(SearchRequest(session), cancellation.Token);
        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.LimitExceeded, response.Error?.Code);
        Assert.Null(reads.LastRequest);
    }

    [Fact]
    public void Search_cancelled_after_dispatch_never_returns_partial_success()
    {
        SessionDescriptor session = CreateSession();
        using var cancellation = new CancellationTokenSource();
        var reads = new CancellingReadDispatcher(session.Documents[0].Revision, cancellation);
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]), reads);
        BridgeResponse response = processor.Process(SearchRequest(session), cancellation.Token);
        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.LimitExceeded, response.Error?.Code);
        Assert.Null(response.Result);
    }

    [Fact]
    public void Search_oversized_serialized_success_returns_structured_limit_before_transport()
    {
        SessionDescriptor session = CreateSession();
        var page = new PageResult<DocumentParameterMatch>
        {
            TotalCount = 350,
            Items = Enumerable.Range(1, 350).Select(i => new DocumentParameterMatch
            {
                Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = $"uid-{i}", ElementId = i },
                Parameter = new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1,
                    StableId = "built-in:-1", Name = new string('x', 4096), IsInstance = true
                },
                StorageType = ParameterStorageType.String, IsReadOnly = true
            }).ToArray()
        };
        var reads = new FakeReadDispatcher(new ReadOperationResult(
            JsonSerializer.SerializeToElement(page, ContractJson.Options), session.Documents[0].Revision));
        var processor = new BridgeRequestProcessor(new FakeContext(session, session.Documents[0]), reads);
        BridgeResponse response = processor.Process(SearchRequest(session));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, response.Error?.Code);
        Assert.Null(response.Result);
        Assert.True(ContractJson.Serialize(response).Length < ContractLimits.MaximumContractBytes);
    }

    private static BridgeRequest SearchRequest(SessionDescriptor session) =>
        CreateRequest(BridgeOperations.SearchDocumentParameters) with
        {
            SessionId = session.SessionId, DocumentKey = session.Documents[0].DocumentKey,
            Payload = JsonDocument.Parse("{\"page\":{\"pageSize\":1000},\"nameContains\":\"x\",\"includeTypes\":false}").RootElement.Clone()
        };

    private sealed class CancellationAwareReadDispatcher : IReadOperationDispatcher
    {
        public bool ObservedCancellation { get; private set; }
        public ReadOperationResult Process(BridgeRequest request) => throw new NotSupportedException();
        public ReadOperationResult Process(BridgeRequest request, CancellationToken cancellationToken)
        {
            ObservedCancellation = cancellationToken.IsCancellationRequested;
            throw new ReadCursorException(BridgeErrorCodes.LimitExceeded);
        }
    }

    private sealed class CancellingReadDispatcher(string revision, CancellationTokenSource cancellation) : IReadOperationDispatcher
    {
        public ReadOperationResult Process(BridgeRequest request)
        {
            cancellation.Cancel();
            return new ReadOperationResult(JsonDocument.Parse("{}").RootElement.Clone(), revision);
        }
    }

    private sealed class CancellingContext(SessionDescriptor session, CancellationTokenSource cancellation)
        : IRevitContextSnapshotProvider
    {
        public SessionDescriptor GetSessionSnapshot()
        {
            cancellation.Cancel();
            return session;
        }
        public DocumentDescriptor? GetActiveDocument() => null;
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
