using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Changes;
using BIMassist.Mcp.RevitBridge.Documents;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class ChangePlanRegistryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-21T12:00:00Z");

    [Fact]
    public void Same_key_replays_committed_outcome_without_reexecuting_after_revision_changes()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        int executions = 0;
        ApplyChangePlanRequest apply = CreateApply(plan);
        ApplyOutcome first = registry.Apply(
            apply, plan.SessionId, plan.DocumentKey, plan.ExpectedRevision, Start.AddMinutes(1),
            _ => { executions++; return new ApplyOutcome(WriteOutcome.Committed, "session-1:5"); });
        ApplyOutcome replay = registry.Apply(
            apply, plan.SessionId, plan.DocumentKey, "session-1:5", Start.AddMinutes(9),
            _ => { executions++; return new ApplyOutcome(WriteOutcome.Committed, "session-1:6"); });

        Assert.Equal(1, executions);
        Assert.Equal(first, replay);
        Assert.Equal("session-1:5", replay.DocumentRevision);
    }

    [Fact]
    public void Reusing_key_for_another_plan_fails_closed()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan first = CreatePlan();
        ChangePlan second = CreatePlan() with { PlanId = "plan-2" };
        second = second with { PlanHash = ChangePlanHasher.ComputeHash(second) };
        registry.Register(first, Start);
        registry.Register(second, Start);
        registry.Apply(CreateApply(first), first.SessionId, first.DocumentKey, first.ExpectedRevision, Start.AddMinutes(1),
            _ => new ApplyOutcome(WriteOutcome.Committed, "session-1:5"));
        ApplyChangePlanRequest conflicting = CreateApply(second) with { IdempotencyKey = "same-key" };

        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => registry.Apply(
            conflicting, second.SessionId, second.DocumentKey, second.ExpectedRevision, Start,
            _ => throw new Exception("must not execute")));
        Assert.Equal(BridgeErrorCodes.IdempotencyConflict, failure.Code);
    }

    [Fact]
    public void Stale_revision_fails_before_executor_and_does_not_reserve_key()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        ApplyChangePlanRequest apply = CreateApply(plan);
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => registry.Apply(
            apply, plan.SessionId, plan.DocumentKey, "session-1:6", Start,
            _ => throw new Exception("must not execute")));
        Assert.Equal(BridgeErrorCodes.DocumentChanged, failure.Code);
        ApplyOutcome success = registry.Apply(apply, plan.SessionId, plan.DocumentKey, plan.ExpectedRevision, Start.AddMinutes(1),
            _ => new ApplyOutcome(WriteOutcome.Committed, "session-1:5"));
        Assert.Equal(WriteOutcome.Committed, success.State);
    }

    [Fact]
    public void Executor_exception_is_remembered_as_unknown_and_never_retried()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        int executions = 0;
        ApplyOutcome first = registry.Apply(CreateApply(plan), plan.SessionId, plan.DocumentKey,
            plan.ExpectedRevision, Start.AddMinutes(1), _ => { executions++; throw new InvalidOperationException("private data"); });
        ApplyOutcome second = registry.Apply(CreateApply(plan), plan.SessionId, plan.DocumentKey,
            plan.ExpectedRevision, Start.AddMinutes(8), _ => { executions++; throw new Exception("must not execute"); });
        Assert.Equal(WriteOutcome.OutcomeUnknown, first.State);
        Assert.Equal(first, second);
        Assert.Equal(1, executions);
    }

    [Fact]
    public void Expired_and_tampered_plans_do_not_execute()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        Assert.Equal(BridgeErrorCodes.PlanExpired, Assert.Throws<ChangePlanFailure>(() => registry.Apply(
            CreateApply(plan), plan.SessionId, plan.DocumentKey, plan.ExpectedRevision, plan.ExpiresAtUtc,
            _ => throw new Exception("must not execute"))).Code);
        Assert.Equal(BridgeErrorCodes.PlanHashMismatch, Assert.Throws<ChangePlanFailure>(() => registry.Apply(
            CreateApply(plan) with { PlanHash = new string('a', 64) }, plan.SessionId, plan.DocumentKey,
            plan.ExpectedRevision, Start, _ => throw new Exception("must not execute"))).Code);
    }

    [Fact]
    public void Closing_a_document_invalidates_its_unapplied_plans_even_if_key_and_revision_are_reused()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        registry.InvalidateDocument(plan.DocumentKey);

        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => registry.Apply(
            CreateApply(plan), plan.SessionId, plan.DocumentKey, plan.ExpectedRevision,
            Start.AddMinutes(1), _ => throw new Exception("must not execute")));
        Assert.Equal(BridgeErrorCodes.PlanNotFound, failure.Code);
    }

    [Fact]
    public void Closing_or_save_as_invalidates_plan_and_advances_document_revision()
    {
        var registry = new ChangePlanRegistry();
        var revisions = new DocumentRevisionService("session-1");
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        string initial = revisions.GetCurrent(plan.DocumentKey);

        new ChangePlanLifecycle(registry, revisions).Invalidate(plan.DocumentKey);

        Assert.NotEqual(initial, revisions.GetCurrent(plan.DocumentKey));
        Assert.Equal(BridgeErrorCodes.PlanNotFound, Assert.Throws<ChangePlanFailure>(() => registry.Apply(
            CreateApply(plan), plan.SessionId, plan.DocumentKey, plan.ExpectedRevision,
            Start.AddMinutes(1), _ => throw new Exception("must not execute"))).Code);
    }

    [Fact]
    public void Expired_unattempted_plan_does_not_exhaust_registry_capacity()
    {
        var registry = new ChangePlanRegistry(maximumPlans: 1);
        ChangePlan old = CreatePlan();
        registry.Register(old, Start);
        ChangePlan newer = CreatePlan() with
        {
            PlanId = "plan-2", CreatedAtUtc = Start.AddMinutes(6), ExpiresAtUtc = Start.AddMinutes(11)
        };
        newer = newer with { PlanHash = ChangePlanHasher.ComputeHash(newer) };
        registry.Register(newer, Start.AddMinutes(6));
        ApplyChangePlanRequest approved = CreateApply(newer) with
        {
            Approval = new ApprovalMetadata
            {
                ApprovedBy = "test-user", Source = "test", ApprovedAtUtc = Start.AddMinutes(6).AddSeconds(1)
            }
        };
        ApplyOutcome result = registry.Apply(approved, newer.SessionId, newer.DocumentKey,
            newer.ExpectedRevision, Start.AddMinutes(7), _ => new ApplyOutcome(WriteOutcome.Committed, "session-1:5"));
        Assert.Equal(WriteOutcome.Committed, result.State);
    }

    [Fact]
    public void Plan_registration_copies_mutable_operation_data_and_enforces_capacity()
    {
        var registry = new ChangePlanRegistry(maximumPlans: 1);
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        ChangePlan second = CreatePlan() with { PlanId = "plan-2" };
        second = second with { PlanHash = ChangePlanHasher.ComputeHash(second) };
        Assert.Equal(BridgeErrorCodes.LimitExceeded, Assert.Throws<ChangePlanFailure>(() => registry.Register(second, Start)).Code);
    }

    private static ChangePlan CreatePlan()
    {
        var plan = new ChangePlan
        {
            PlanId = "plan-1", SessionId = "session-1", DocumentKey = "document-1",
            ExpectedRevision = "session-1:4", CreatedAtUtc = Start,
            ExpiresAtUtc = Start.AddMinutes(5), PlanHash = new string('0', 64),
            Operations = [new ChangeOperation
            {
                OperationId = "op-1", Kind = ChangeOperationKind.SetParameterValue,
                Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "uid-1", ElementId = 42 },
                Parameter = new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.SharedGuid, SharedGuid = Guid.Parse("9f51461a-bda5-4a03-8e7d-fb52e6e7d50c"),
                    StableId = "shared:9f51461a-bda5-4a03-8e7d-fb52e6e7d50c", Name = "Test"
                },
                Before = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "old" },
                After = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "new" },
                Options = new Dictionary<string, string>()
            }], Warnings = []
        };
        return plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
    }

    private static ApplyChangePlanRequest CreateApply(ChangePlan plan) => new()
    {
        PlanId = plan.PlanId, PlanHash = plan.PlanHash, ExpectedRevision = plan.ExpectedRevision,
        IdempotencyKey = "same-key", Approval = new ApprovalMetadata
        {
            ApprovedBy = "test-user", Source = "test", ApprovedAtUtc = Start.AddSeconds(1)
        }
    };
}
