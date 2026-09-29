using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Changes;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Adapters;

public sealed class BridgePlanRequestProcessorTests
{
    [Fact]
    public void Valid_plan_request_dispatches_without_mutating_and_returns_plan_at_current_revision()
    {
        SessionDescriptor session = Session();
        var planner = new FakePlanner();
        var processor = new BridgeRequestProcessor(new FakeContext(session), null, planner);

        BridgeResponse response = processor.Process(PlanRequest());

        Assert.True(response.Success);
        Assert.Equal("session-1:0", response.DocumentRevision);
        Assert.Equal(1, planner.CallCount);
        ChangePlan plan = ContractJson.Deserialize<ChangePlan>(response.Result!.Value.GetRawText());
        Assert.Equal(ChangePlanHasher.ComputeHash(plan), plan.PlanHash);
        Assert.Single(plan.Operations);
        Assert.Equal("old", plan.Operations[0].Before?.StringValue);
        Assert.Equal("new", plan.Operations[0].After?.StringValue);
    }

    [Fact]
    public void Valid_apply_request_dispatches_to_trusted_writer_and_returns_recorded_state()
    {
        var writer = new FakeWriter();
        var processor = new BridgeRequestProcessor(new FakeContext(Session()), null, null, writer);
        BridgeRequest request = PlanRequest() with
        {
            Operation = BridgeOperations.ApplyChangePlan,
            Payload = JsonSerializer.SerializeToElement(new ApplyChangePlanRequest
            {
                PlanId = "plan-1", PlanHash = new string('a', 64), ExpectedRevision = "session-1:0",
                IdempotencyKey = "apply-key-1", Approval = new ApprovalMetadata
                {
                    ApprovedBy = "claim", Source = "client", ApprovedAtUtc = DateTimeOffset.Parse("2026-09-28T10:01:00Z")
                }
            }, ContractJson.Options),
            IdempotencyKey = "apply-key-1"
        };
        BridgeResponse response = processor.Process(request);
        Assert.True(response.Success);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal("committed", response.Result?.GetProperty("state").GetString());
        Assert.Equal("session-1:1", response.DocumentRevision);
    }

    [Fact]
    public void Abandoned_apply_is_not_dispatched()
    {
        var writer = new FakeWriter();
        var processor = new BridgeRequestProcessor(new FakeContext(Session()), null, null, writer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        BridgeResponse response = processor.Process(ApplyRequest(), cancellation.Token);
        Assert.False(response.Success);
        Assert.Equal(0, writer.CallCount);
    }

    [Theory]
    [InlineData(WriteOutcome.RolledBack, "rolledBack")]
    [InlineData(WriteOutcome.OutcomeUnknown, "outcomeUnknown")]
    public void Noncommitted_apply_is_never_a_success_response(WriteOutcome state, string stateName)
    {
        var processor = new BridgeRequestProcessor(new FakeContext(Session()), null, null, new FakeWriter(state));
        BridgeRequest request = ApplyRequest();
        BridgeResponse response = processor.Process(request);
        Assert.False(response.Success);
        Assert.Null(response.Result);
        Assert.Equal(BridgeErrorCodes.TransactionFailed, response.Error?.Code);
        Assert.Equal(stateName, response.Error?.Details?["state"]);
    }

    private static BridgeRequest ApplyRequest() => PlanRequest() with
    {
        Operation = BridgeOperations.ApplyChangePlan,
        Payload = JsonSerializer.SerializeToElement(new ApplyChangePlanRequest
        {
            PlanId = "plan-1", PlanHash = new string('a', 64), ExpectedRevision = "session-1:0",
            IdempotencyKey = "apply-key-1", Approval = new ApprovalMetadata
            {
                ApprovedBy = "claim", Source = "client", ApprovedAtUtc = DateTimeOffset.Parse("2026-09-28T10:01:00Z")
            }
        }, ContractJson.Options),
        IdempotencyKey = "apply-key-1"
    };

    private sealed class FakeWriter(WriteOutcome state = WriteOutcome.Committed) : IApplyOperationDispatcher
    {
        public int CallCount { get; private set; }
        public ApplyOutcome Apply(BridgeRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return new ApplyOutcome(state, state == WriteOutcome.Committed ? "session-1:1" : null);
        }
    }

    [Theory]
    [InlineData("other-session", "doc-1", "session-1:0", BridgeErrorCodes.SessionNotFound)]
    [InlineData("session-1", "other-document", "session-1:0", BridgeErrorCodes.DocumentNotFound)]
    [InlineData("session-1", "doc-1", "session-1:9", BridgeErrorCodes.DocumentChanged)]
    public void Invalid_context_never_invokes_planner(string sessionId, string documentKey, string revision, string expectedError)
    {
        var planner = new FakePlanner();
        var processor = new BridgeRequestProcessor(new FakeContext(Session()), null, planner);
        BridgeRequest request = PlanRequest() with
        {
            SessionId = sessionId,
            DocumentKey = documentKey,
            ExpectedRevision = revision
        };

        BridgeResponse response = processor.Process(request);

        Assert.False(response.Success);
        Assert.Equal(expectedError, response.Error?.Code);
        Assert.Equal(0, planner.CallCount);
    }

    private static BridgeRequest PlanRequest() => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-1",
        SessionId = "session-1",
        DocumentKey = "doc-1",
        ExpectedRevision = "session-1:0",
        IdempotencyKey = "plan-key-1",
        Operation = BridgeOperations.PlanSetParameterValues,
        Payload = JsonDocument.Parse("""{"target":{"kind":"element","uniqueId":"uid-1","elementId":42},"parameter":{"kind":"builtIn","builtInId":-1001203,"stableId":"built-in:-1001203","name":"Description"},"after":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"new"}}""").RootElement.Clone()
    };

    private static SessionDescriptor Session() => new()
    {
        SessionId = "session-1", RevitProcessId = 1234, RevitMajor = 2026,
        BridgeVersion = ProtocolVersions.AddonVersion,
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        StartedAtUtc = DateTimeOffset.Parse("2026-09-28T10:00:00Z"),
        Documents = [new DocumentDescriptor
        {
            DocumentKey = "doc-1", Title = "Disposable", PathStatus = DocumentPathStatus.Saved,
            Path = "C:\\Models\\Disposable.rvt", IsFamilyDocument = false,
            IsWorkshared = false, IsReadOnly = false, Revision = "session-1:0"
        }]
    };

    private sealed class FakePlanner : IPlanOperationDispatcher
    {
        public int CallCount { get; private set; }
        public ChangePlan Plan(BridgeRequest request)
        {
            CallCount++;
            var plan = new ChangePlan
            {
                PlanId = "plan-1", SessionId = request.SessionId!, DocumentKey = request.DocumentKey!,
                ExpectedRevision = request.ExpectedRevision!, CreatedAtUtc = DateTimeOffset.Parse("2026-09-28T10:00:00Z"),
                ExpiresAtUtc = DateTimeOffset.Parse("2026-09-28T10:05:00Z"),
                PlanHash = new string('0', 64), Warnings = [],
                Operations = [new ChangeOperation
                {
                    OperationId = "op-1", Kind = ChangeOperationKind.SetParameterValue,
                    Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "uid-1", ElementId = 42 },
                    Parameter = new ParameterIdentity
                    {
                        Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001203,
                        StableId = "built-in:-1001203", Name = "Description"
                    },
                    Before = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "old" },
                    After = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "new" },
                    Options = new Dictionary<string, string>()
                }]
            };
            return plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
        }
    }

    private sealed class FakeContext(SessionDescriptor session) : IRevitContextSnapshotProvider
    {
        public SessionDescriptor GetSessionSnapshot() => session;
        public DocumentDescriptor? GetActiveDocument() => session.Documents[0];
    }
}
