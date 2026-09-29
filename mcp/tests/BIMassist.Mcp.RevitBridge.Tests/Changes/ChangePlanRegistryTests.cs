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
    public void Approval_prompt_escapes_control_characters_and_keeps_hash_visible()
    {
        ChangePlan plan = CreatePlan();
        ChangeOperation operation = plan.Operations[0] with
        {
            After = plan.Operations[0].After! with { StringValue = "replacement\nPlan-Hash: forged\r\u202E" }
        };
        plan = plan with { Operations = [operation] };
        string prompt = ApplyApprovalPrompt.Format(plan, "Model\nSitzung: forged");
        Assert.Contains("Nachher: replacement\\u000APlan-Hash: forged\\u000D\\u202E", prompt);
        Assert.Contains($"Plan-Hash (SHA-256): {plan.PlanHash}", prompt);
        Assert.Equal(1, prompt.Split("Plan-Hash (SHA-256):").Length - 1);
        Assert.DoesNotContain("Model\n", prompt);
    }

    [Fact]
    public void Approval_prompt_rejects_uninspectably_long_values_instead_of_hiding_them()
    {
        ChangePlan plan = CreatePlan();
        ChangeOperation operation = plan.Operations[0] with
        {
            After = plan.Operations[0].After! with { StringValue = new string('x', 513) }
        };
        Assert.Equal(BridgeErrorCodes.InvalidRequest, Assert.Throws<ChangePlanFailure>(() =>
            ApplyApprovalPrompt.Format(plan with { Operations = [operation] }, "Model")).Code);
    }

    [Fact]
    public void Undo_or_other_document_revision_does_not_replay_a_stale_committed_success()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        ApplyChangePlanRequest request = CreateApply(plan);
        ApplyApproved(registry, request, plan.SessionId, plan.DocumentKey, plan.ExpectedRevision,
            Start.AddMinutes(1), _ => new ApplyOutcome(WriteOutcome.Committed, "session-1:5"));
        ApplyOutcome replay = ApplyApproved(registry, request, plan.SessionId, plan.DocumentKey, "session-1:6",
            Start.AddMinutes(2), _ => throw new Exception("must not execute"));
        Assert.Equal(WriteOutcome.OutcomeUnknown, replay.State);
        Assert.Null(replay.DocumentRevision);
    }

    [Fact]
    public void Invalidation_makes_committed_attempt_a_historical_unknown_not_current_success()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        ApplyChangePlanRequest request = CreateApply(plan);
        ApplyApproved(registry, request, plan.SessionId, plan.DocumentKey, plan.ExpectedRevision,
            Start.AddMinutes(1), _ => new ApplyOutcome(WriteOutcome.Committed, "session-1:5"));
        registry.InvalidateDocument(plan.DocumentKey);

        ApplyOutcome replay = ApplyApproved(registry, request, plan.SessionId, plan.DocumentKey, "session-1:5",
            Start.AddMinutes(2), _ => throw new Exception("must not execute"));
        Assert.Equal(WriteOutcome.OutcomeUnknown, replay.State);
        Assert.Null(replay.DocumentRevision);
    }

    [Fact]
    public void Approval_expiring_while_dialog_is_open_never_reserves_or_executes()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        int calls = 0;
        Assert.Equal(BridgeErrorCodes.PlanExpired, Assert.Throws<ChangePlanFailure>(() =>
            registry.Apply(CreateApply(plan), plan.SessionId, plan.DocumentKey, plan.ExpectedRevision,
                Start.AddMinutes(1), _ => { calls++; return new ApplyOutcome(WriteOutcome.Committed, "session-1:5"); },
                _ => true, () => plan.ExpiresAtUtc)).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Abandoned_request_after_approval_never_reserves_or_executes()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        using var abandoned = new CancellationTokenSource();
        Assert.Equal(BridgeErrorCodes.InvalidRequest, Assert.Throws<ChangePlanFailure>(() =>
            registry.Apply(CreateApply(plan), plan.SessionId, plan.DocumentKey, plan.ExpectedRevision,
                Start.AddMinutes(1), _ => throw new Exception("must not execute"),
                _ => { abandoned.Cancel(); return true; }, () => Start.AddMinutes(1), abandoned.Token)).Code);
    }

    [Fact]
    public void Client_approval_metadata_alone_never_authorizes_a_write()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        Assert.Equal(BridgeErrorCodes.InvalidRequest, Assert.Throws<ChangePlanFailure>(() =>
            registry.Apply(CreateApply(plan), plan.SessionId, plan.DocumentKey,
                plan.ExpectedRevision, Start.AddMinutes(1),
                _ => throw new Exception("must not execute"))).Code);
    }

    [Fact]
    public void Rejected_native_approval_does_not_reserve_and_retry_requires_fresh_trusted_approval()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        int executions = 0;
        ApplyChangePlanRequest request = CreateApply(plan);
        Assert.Equal(BridgeErrorCodes.InvalidRequest, Assert.Throws<ChangePlanFailure>(() =>
            ApplyApproved(registry, request, plan.SessionId, plan.DocumentKey, plan.ExpectedRevision,
                Start.AddMinutes(1), _ => { executions++; return new ApplyOutcome(WriteOutcome.Committed, "session-1:5"); },
                _ => false)).Code);
        Assert.Equal(0, executions);
        ApplyOutcome outcome = ApplyApproved(registry, request, plan.SessionId, plan.DocumentKey,
            plan.ExpectedRevision, Start.AddMinutes(1), _ =>
            { executions++; return new ApplyOutcome(WriteOutcome.RolledBack, null); }, _ => true);
        Assert.Equal(WriteOutcome.RolledBack, outcome.State);
        Assert.Equal(1, executions);
        Assert.Equal(BridgeErrorCodes.IdempotencyConflict, Assert.Throws<ChangePlanFailure>(() =>
            ApplyApproved(registry, request with { IdempotencyKey = "different-key" },
                plan.SessionId, plan.DocumentKey, plan.ExpectedRevision, Start.AddMinutes(1),
                _ => throw new Exception("must not execute"))).Code);
    }

    [Fact]
    public void Same_key_replays_committed_outcome_without_reexecuting_after_revision_changes()
    {
        var registry = new ChangePlanRegistry();
        ChangePlan plan = CreatePlan();
        registry.Register(plan, Start);
        int executions = 0;
        ApplyChangePlanRequest apply = CreateApply(plan);
        ApplyOutcome first = ApplyApproved(registry,
            apply, plan.SessionId, plan.DocumentKey, plan.ExpectedRevision, Start.AddMinutes(1),
            _ => { executions++; return new ApplyOutcome(WriteOutcome.Committed, "session-1:5"); });
        ApplyOutcome replay = ApplyApproved(registry,
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
        ApplyApproved(registry, CreateApply(first), first.SessionId, first.DocumentKey, first.ExpectedRevision, Start.AddMinutes(1),
            _ => new ApplyOutcome(WriteOutcome.Committed, "session-1:5"));
        ApplyChangePlanRequest conflicting = CreateApply(second) with { IdempotencyKey = "same-key" };

        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => ApplyApproved(registry,
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
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => ApplyApproved(registry,
            apply, plan.SessionId, plan.DocumentKey, "session-1:6", Start,
            _ => throw new Exception("must not execute")));
        Assert.Equal(BridgeErrorCodes.DocumentChanged, failure.Code);
        ApplyOutcome success = ApplyApproved(registry, apply, plan.SessionId, plan.DocumentKey, plan.ExpectedRevision, Start.AddMinutes(1),
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
        ApplyOutcome first = ApplyApproved(registry, CreateApply(plan), plan.SessionId, plan.DocumentKey,
            plan.ExpectedRevision, Start.AddMinutes(1), _ => { executions++; throw new InvalidOperationException("private data"); });
        ApplyOutcome second = ApplyApproved(registry, CreateApply(plan), plan.SessionId, plan.DocumentKey,
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
        Assert.Equal(BridgeErrorCodes.PlanExpired, Assert.Throws<ChangePlanFailure>(() => ApplyApproved(registry,
            CreateApply(plan), plan.SessionId, plan.DocumentKey, plan.ExpectedRevision, plan.ExpiresAtUtc,
            _ => throw new Exception("must not execute"))).Code);
        Assert.Equal(BridgeErrorCodes.PlanHashMismatch, Assert.Throws<ChangePlanFailure>(() => ApplyApproved(registry,
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

        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => ApplyApproved(registry,
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
        Assert.Equal(BridgeErrorCodes.PlanNotFound, Assert.Throws<ChangePlanFailure>(() => ApplyApproved(registry,
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
        ApplyOutcome result = ApplyApproved(registry, approved, newer.SessionId, newer.DocumentKey,
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

    private static ApplyOutcome ApplyApproved(ChangePlanRegistry registry, ApplyChangePlanRequest request,
        string sessionId, string documentKey, string revision, DateTimeOffset now,
        Func<ChangePlan, ApplyOutcome> execute, Func<ChangePlan, bool>? approve = null) =>
        registry.Apply(request, sessionId, documentKey, revision, now, execute,
            approve ?? (static _ => true), () => now);

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
