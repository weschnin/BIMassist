using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class SetParameterPlanDispatcherTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T18:00:00Z");

    [Fact]
    public void Registers_only_a_plan_built_from_resolved_bridge_state()
    {
        var registry = new ChangePlanRegistry();
        var source = new FakeSource();
        var dispatcher = new SetParameterPlanDispatcher(source, registry, () => Now);
        BridgeRequest request = Request();

        ChangePlan plan = dispatcher.Plan(request);

        Assert.Equal(1, source.CallCount);
        Assert.Equal("old", Assert.Single(plan.Operations).Before?.StringValue);
        Assert.Equal("Actual Name", plan.Operations[0].Parameter.Name);
        Assert.Equal("session-1:4", plan.ExpectedRevision);
        Assert.Equal(plan.PlanHash, ChangePlanHasher.ComputeHash(plan));
        Assert.Equal(BridgeErrorCodes.DocumentChanged, Assert.Throws<ChangePlanFailure>(() =>
            registry.Apply(new ApplyChangePlanRequest
            {
                PlanId = plan.PlanId, PlanHash = plan.PlanHash, ExpectedRevision = plan.ExpectedRevision,
                IdempotencyKey = "apply-key", Approval = new ApprovalMetadata
                {
                    ApprovedBy = "claim", Source = "test", ApprovedAtUtc = Now.AddSeconds(1)
                }
            }, plan.SessionId, plan.DocumentKey, "session-1:5", Now.AddSeconds(2),
            _ => throw new Exception("No transaction may run in this test"))).Code);
    }

    [Fact]
    public void Source_failure_does_not_register_a_plan()
    {
        var registry = new ChangePlanRegistry(maximumPlans: 1);
        var dispatcher = new SetParameterPlanDispatcher(new FailingSource(), registry, () => Now);
        Assert.Equal(BridgeErrorCodes.TargetNotFound,
            Assert.Throws<ChangePlanFailure>(() => dispatcher.Plan(Request())).Code);
        // Capacity must remain available after the failed plan attempt.
        ChangePlan plan = new SetParameterPlanDispatcher(new FakeSource(), registry, () => Now).Plan(Request());
        Assert.NotNull(plan);
    }

    private static BridgeRequest Request() => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "r1", SessionId = "session-1", DocumentKey = "doc-1",
        ExpectedRevision = "session-1:4", IdempotencyKey = "plan-key",
        Operation = BridgeOperations.PlanSetParameterValues,
        Payload = JsonDocument.Parse("""{"target":{"kind":"element","uniqueId":"uid-1","elementId":42},"parameter":{"kind":"builtIn","builtInId":-1001203,"stableId":"built-in:-1001203","name":"Client Name"},"after":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"new"}}""").RootElement.Clone()
    };

    private sealed class FakeSource : ISetParameterPlanSource
    {
        public int CallCount { get; private set; }
        public ResolvedSetParameterPlan Read(BridgeRequest request)
        {
            CallCount++;
            return new ResolvedSetParameterPlan(
                new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "uid-1", ElementId = 42 },
                new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001203,
                    StableId = "built-in:-1001203", Name = "Actual Name"
                },
                new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "old" },
                "session-1:4");
        }
    }

    private sealed class FailingSource : ISetParameterPlanSource
    {
        public ResolvedSetParameterPlan Read(BridgeRequest request) =>
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
    }
}
