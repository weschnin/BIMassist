using System.Text.Json;
using System.Text.Json.Nodes;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class BatchPlanDispatchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");

    [Fact]
    public void Batch_dispatch_observes_all_changes_and_registers_hashed_plan_without_invoking_scalar_source()
    {
        var registry = new ChangePlanRegistry();
        var batch = new FakeBatchSource();
        var scalar = new ScalarSource();
        var dispatcher = new BatchSetParameterPlanDispatcher(batch, registry, () => Now);
        var processor = Processor(dispatcher, new SetParameterPlanDispatcher(scalar, registry, () => Now));
        BridgeResponse response = processor.Process(Request());
        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal(1, batch.Calls);
        Assert.Equal(0, scalar.Calls);
        ChangePlan plan = ContractJson.Deserialize<ChangePlan>(response.Result!.Value.GetRawText());
        Assert.Equal(2, plan.Operations.Count);
        Assert.Equal("old-0", plan.Operations[0].Before!.StringValue);
        Assert.Equal(plan.PlanHash, ChangePlanHasher.ComputeHash(plan));
        Assert.Equal(BridgeErrorCodes.DocumentChanged,
            Assert.Throws<ChangePlanFailure>(() => registry.Apply(new ApplyChangePlanRequest
            {
                PlanId = plan.PlanId, PlanHash = plan.PlanHash, ExpectedRevision = plan.ExpectedRevision,
                IdempotencyKey = "apply-key", Approval = new ApprovalMetadata
                { ApprovedBy = "claim", Source = "test", ApprovedAtUtc = Now.AddSeconds(1) }
            }, "s1", "d1", "later", Now.AddSeconds(2), _ => throw new Exception("no write"))).Code);
    }

    [Fact]
    public void Batch_plan_registry_is_inaccessible_to_existing_single_apply_registry()
    {
        var batchRegistry = new ChangePlanRegistry();
        var singleApplyRegistry = new ChangePlanRegistry();
        BridgeResponse response = Processor(new BatchSetParameterPlanDispatcher(
            new FakeBatchSource(), batchRegistry, () => Now)).Process(Request());
        Assert.True(response.Success);
        ChangePlan plan = ContractJson.Deserialize<ChangePlan>(response.Result!.Value.GetRawText());
        Assert.Equal(BridgeErrorCodes.PlanNotFound, Assert.Throws<ChangePlanFailure>(() =>
            singleApplyRegistry.Apply(new ApplyChangePlanRequest
            {
                PlanId = plan.PlanId, PlanHash = plan.PlanHash, ExpectedRevision = plan.ExpectedRevision,
                IdempotencyKey = "apply-key", Approval = new ApprovalMetadata
                { ApprovedBy = "claim", Source = "test", ApprovedAtUtc = Now.AddSeconds(1) }
            }, "s1", "d1", "rev1", Now.AddSeconds(2),
                _ => throw new Exception("no write"))).Code);
    }

    [Theory]
    [InlineData("foreign", "d1", "rev1", BridgeErrorCodes.SessionNotFound)]
    [InlineData("s1", "foreign", "rev1", BridgeErrorCodes.DocumentNotFound)]
    [InlineData("s1", "d1", "old", BridgeErrorCodes.DocumentChanged)]
    public void Invalid_session_document_and_revision_never_reach_batch_source(string session, string doc, string revision, string code)
    {
        var batch = new FakeBatchSource();
        BridgeRequest request = Request();
        JsonNode payload = JsonNode.Parse(request.Payload.GetRawText())!;
        payload["documentKey"] = doc;
        BridgeResponse response = Processor(new BatchSetParameterPlanDispatcher(batch, new ChangePlanRegistry(), () => Now))
            .Process(request with { SessionId = session, DocumentKey = doc, ExpectedRevision = revision,
                Payload = JsonSerializer.SerializeToElement(payload) });
        Assert.False(response.Success);
        Assert.Equal(code, response.Error?.Code);
        Assert.Equal(0, batch.Calls);
    }

    [Fact]
    public void Batch_source_failure_does_not_register_any_partial_plan()
    {
        var registry = new ChangePlanRegistry(maximumPlans: 1);
        var batch = new FakeBatchSource { DuplicateActualParameter = true };
        var dispatcher = new BatchSetParameterPlanDispatcher(batch, registry, () => Now);
        BridgeRequest request = Request();
        JsonNode payload = JsonNode.Parse(request.Payload.GetRawText())!;
        payload["items"]![0]!["target"] = JsonNode.Parse("""{"kind":"document"}""");
        payload["items"]![1]!["target"]!["uniqueId"] = "uid-1";
        request = request with { Payload = JsonSerializer.SerializeToElement(payload) };
        BridgeResponse failure = Processor(dispatcher).Process(request);
        Assert.False(failure.Success);
        Assert.Equal(BridgeErrorCodes.AmbiguousTarget, failure.Error?.Code);
        batch.DuplicateActualParameter = false;
        Assert.True(Processor(dispatcher).Process(Request()).Success);
    }

    [Fact]
    public void Oversized_observed_values_return_limit_error_without_reserving_registry_capacity()
    {
        var registry = new ChangePlanRegistry(maximumPlans: 1);
        var batch = new FakeBatchSource { HugeObservedValues = true };
        var dispatcher = new BatchSetParameterPlanDispatcher(batch, registry, () => Now);
        BridgeResponse failure = Processor(dispatcher).Process(Request(20));
        Assert.False(failure.Success);
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Error?.Code);
        batch.HugeObservedValues = false;
        Assert.True(Processor(dispatcher).Process(Request()).Success);
    }

    [Fact]
    public void Single_plan_still_routes_only_to_original_dispatcher()
    {
        var scalar = new ScalarSource();
        var batch = new FakeBatchSource();
        var registry = new ChangePlanRegistry();
        var processor = Processor(new BatchSetParameterPlanDispatcher(batch, registry, () => Now),
            new SetParameterPlanDispatcher(scalar, registry, () => Now));
        BridgeRequest request = Request() with
        {
            Operation = BridgeOperations.PlanSetParameterValues,
            Payload = JsonDocument.Parse("""{"target":{"kind":"element","uniqueId":"uid-0","elementId":42},"parameter":{"kind":"builtIn","builtInId":-101,"stableId":"built-in:-101","name":"Actual"},"after":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"new"}}""").RootElement.Clone()
        };
        Assert.True(processor.Process(request).Success);
        Assert.Equal(1, scalar.Calls);
        Assert.Equal(0, batch.Calls);
    }

    private static BridgeRequestProcessor Processor(IPlanOperationDispatcher batch, IPlanOperationDispatcher? scalar = null) =>
        new(new Context(), null, scalar, null, batch);

    private static BridgeRequest Request(int count = 2)
    {
        var items = new JsonArray();
        for (int i = 0; i < count; i++)
            items.Add(JsonNode.Parse($$"""{"target":{"kind":"element","uniqueId":"uid-{{i}}","elementId":{{42 + i}}},"parameter":{"kind":"builtIn","builtInId":-101,"stableId":"built-in:-101","name":"Name"},"newString":"new-{{i}}"}""")!);
        return new BridgeRequest
        {
            ProtocolVersion = "1", SchemaVersion = "1", RequestId = "r1", SessionId = "s1",
            DocumentKey = "d1", ExpectedRevision = "rev1", IdempotencyKey = "plan-key",
            Operation = BridgeOperations.PlanSetStringParametersBatch,
            Payload = JsonSerializer.SerializeToElement(new JsonObject { ["documentKey"] = "d1", ["items"] = items })
        };
    }

    private sealed class FakeBatchSource : IBatchSetParameterPlanSource
    {
        public int Calls { get; private set; }
        public bool DuplicateActualParameter { get; set; }
        public bool HugeObservedValues { get; set; }
        public ChangePlan BuildBatchPlan(PlanSetStringParametersBatchRequest payload, string sessionId,
            string documentKey, string expectedRevision, DateTimeOffset now)
        {
            Calls++;
            var observations = new List<ResolvedSetParameterPlan>();
            foreach (var (item, index) in payload.Items.Select((item, index) => (item, index)))
                observations.Add(new ResolvedSetParameterPlan(item.Target, item.Parameter,
                    new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false,
                        StringValue = HugeObservedValues ? string.Concat(Enumerable.Repeat("\U0001F600", 16_000)) : $"old-{index}" },
                    expectedRevision)
                {
                    CanonicalOwnerUniqueId = DuplicateActualParameter ? "uid-1" : item.Target.UniqueId,
                    CanonicalParameterId = -101
                });
            return BatchSetParameterPlanBuilder.Build(payload, sessionId, documentKey, expectedRevision, observations, now, "Disposable");
        }
    }

    private sealed class ScalarSource : ISetParameterPlanSource
    {
        public int Calls { get; private set; }
        public ResolvedSetParameterPlan Read(BridgeRequest request)
        {
            Calls++;
            return new ResolvedSetParameterPlan(
                new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "uid-0", ElementId = 42 },
                new ParameterIdentity { Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -101,
                    StableId = "built-in:-101", Name = "Actual" },
                new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "old" }, "rev1");
        }
    }

    private sealed class Context : IRevitContextSnapshotProvider
    {
        public SessionDescriptor GetSessionSnapshot() => new()
        {
            SessionId = "s1", RevitProcessId = 1, RevitMajor = 2026, BridgeVersion = ProtocolVersions.AddonVersion,
            ProtocolVersion = "1", SchemaVersion = "1", StartedAtUtc = Now,
            Documents = [new DocumentDescriptor { DocumentKey = "d1", Title = "Disposable", Revision = "rev1",
                PathStatus = DocumentPathStatus.Saved, IsFamilyDocument = false, IsWorkshared = false, IsReadOnly = false }]
        };
        public DocumentDescriptor? GetActiveDocument() => GetSessionSnapshot().Documents[0];
    }
}
