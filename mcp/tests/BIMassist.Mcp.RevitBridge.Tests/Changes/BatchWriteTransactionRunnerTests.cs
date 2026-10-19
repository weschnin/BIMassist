using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class BatchWriteTransactionRunnerTests
{
    [Fact]
    public void Two_writes_commit_only_after_every_readback_and_revision_advance()
    {
        var fake = new Harness(2);
        ApplyOutcome result = fake.Run();
        Assert.Equal(WriteOutcome.Committed, result.State);
        Assert.Equal("rev-2", result.DocumentRevision);
        Assert.Equal(new[] { "start", "resolve:1", "resolve:2", "read:1", "read:2",
            "read:1", "set:1", "read:1", "read:2", "set:2", "read:2",
            "read:1", "read:2", "commit", "read:1", "read:2", "dispose" }, fake.Log);
    }

    [Fact]
    public void Second_Set_false_rolls_back_without_partial_success()
    {
        var fake = new Harness(2) { FalseSetAt = 2 };
        ApplyOutcome result = fake.Run();
        Assert.Equal(WriteOutcome.RolledBack, result.State);
        Assert.Null(result.DocumentRevision);
        Assert.Contains("set:1", fake.Log);
        Assert.Contains("set:2", fake.Log);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
        Assert.Equal("old-1", fake.Value(1));
    }

    [Fact]
    public void Second_readback_mismatch_rolls_back()
    {
        var fake = new Harness(2) { MismatchAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Cancellation_after_first_Set_prevents_second_Set_and_rolls_back()
    {
        var fake = new Harness(2) { CancelAfterSetAt = 1 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain("set:2", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Cancellation_after_last_Set_prevents_commit()
    {
        var fake = new Harness(2) { CancelAfterSetAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Expiry_after_first_Set_prevents_second_Set()
    {
        var fake = new Harness(2) { ExpireAfterSetAt = 1 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain("set:2", fake.Log);
    }

    [Fact]
    public void Failed_rollback_never_claims_rolled_back()
    {
        var fake = new Harness(2) { FalseSetAt = 2, RollbackStatus = BatchTransactionStatus.Pending };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
    }

    [Fact]
    public void Rollback_exception_never_reports_rolled_back()
    {
        var fake = new Harness(2) { FalseSetAt = 2, ThrowRollback = true };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Pending_commit_never_claims_success_or_rollback()
    {
        var fake = new Harness(2) { CommitStatus = BatchTransactionStatus.Pending };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.DoesNotContain("rollback", fake.Log);
    }

    [Fact]
    public void Explicit_rolled_back_commit_reports_rollback()
    {
        var fake = new Harness(2) { CommitStatus = BatchTransactionStatus.RolledBack };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain("rollback", fake.Log);
    }

    [Fact]
    public void Commit_exception_does_not_attempt_rollback()
    {
        var fake = new Harness(2) { ThrowCommit = true };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.DoesNotContain("rollback", fake.Log);
    }

    [Fact]
    public void Post_commit_mismatch_is_unknown_without_rollback()
    {
        var fake = new Harness(2) { PostCommitMismatchAt = 2 };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.DoesNotContain("rollback", fake.Log);
    }

    [Fact]
    public void Stale_revision_after_commit_is_unknown()
    {
        var fake = new Harness(2) { SuppressRevisionAdvance = true };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.DoesNotContain("rollback", fake.Log);
    }

    [Fact]
    public void Twenty_writes_commit_after_all_twenty_post_commit_readbacks()
    {
        var fake = new Harness(20);
        Assert.Equal(WriteOutcome.Committed, fake.Run().State);
        Assert.Equal(20, fake.Log.Count(s => s.StartsWith("set:", StringComparison.Ordinal)));
        Assert.Equal(40, fake.Log.Count(s => s.StartsWith("resolve:", StringComparison.Ordinal) || s.StartsWith("set:", StringComparison.Ordinal)));
        Assert.Equal(100, fake.Log.Count(s => s.StartsWith("read:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Duplicate_actual_owner_and_parameter_ids_through_aliases_prevent_all_writes()
    {
        var fake = new Harness(2) { AliasSecondToFirst = true };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Stale_second_Before_prevents_all_writes()
    {
        var fake = new Harness(2) { StaleBeforeAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("read:1", fake.Log);
        Assert.Contains("read:2", fake.Log);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Updater_changes_second_Before_after_first_Set_without_revision_advance()
    {
        var fake = new Harness(2) { FirstSetMutatesSecond = true };
        ApplyOutcome result = fake.Run();
        Assert.Equal(WriteOutcome.RolledBack, result.State);
        Assert.Null(result.DocumentRevision);
        Assert.Contains("set:1", fake.Log);
        Assert.DoesNotContain("set:2", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
        Assert.Contains("rollback", fake.Log);
        Assert.Equal("old-1", fake.Value(1));
        Assert.Equal("old-2", fake.Value(2));
    }

    [Fact]
    public void Second_Set_updater_mutates_first_After_without_revision_advance_rolls_back_before_commit()
    {
        var fake = new Harness(2) { SecondSetMutatesFirst = true };
        ApplyOutcome result = fake.Run();
        Assert.Equal(WriteOutcome.RolledBack, result.State);
        Assert.Null(result.DocumentRevision);
        Assert.Contains("set:2", fake.Log);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
        Assert.Equal("old-1", fake.Value(1));
        Assert.Equal("old-2", fake.Value(2));
    }

    [Theory]
    [InlineData("non-string-storage")]
    [InlineData("read-only-parameter")]
    [InlineData("non-user-modifiable-definition")]
    [InlineData("owned-by-other-user")]
    public void First_Set_updater_makes_second_unwritable_without_revision_advance(string blocker)
    {
        var fake = new Harness(2) { FirstSetBlocksSecond = true, LiveBlocker = blocker };
        ApplyOutcome result = fake.Run();
        Assert.Equal(WriteOutcome.RolledBack, result.State);
        Assert.Null(result.DocumentRevision);
        Assert.Contains("set:1", fake.Log);
        Assert.DoesNotContain("set:2", fake.Log);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void First_Before_changes_after_complete_preflight_but_before_first_Set()
    {
        var fake = new Harness(2) { MutateFirstAfterPreflight = true };
        ApplyOutcome result = fake.Run();
        Assert.Equal(WriteOutcome.RolledBack, result.State);
        Assert.Null(result.DocumentRevision);
        Assert.Equal("old-1", fake.Value(1));
        Assert.DoesNotContain(fake.Log, entry => entry.StartsWith("set:", StringComparison.Ordinal));
        Assert.Contains("rollback", fake.Log);
    }

    [Fact]
    public void Updater_clears_second_HasValue_after_first_Set()
    {
        var fake = new Harness(2) { FirstSetClearsSecondHasValue = true };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain("set:2", fake.Log);
        Assert.Equal(1, fake.Log.Count(entry => entry == "read:2")); // only preflight
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Theory]
    [InlineData(4)] // Pending
    [InlineData(0)] // Unknown
    public void Changed_live_Before_with_unconfirmed_rollback_is_unknown(int rollbackStatus)
    {
        var fake = new Harness(2) { FirstSetMutatesSecond = true, RollbackStatus = (BatchTransactionStatus)rollbackStatus };
        ApplyOutcome result = fake.Run();
        Assert.Equal(WriteOutcome.OutcomeUnknown, result.State);
        Assert.Null(result.DocumentRevision);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("set:2", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Changed_live_Before_with_throwing_rollback_is_unknown()
    {
        var fake = new Harness(2) { FirstSetMutatesSecond = true, ThrowRollback = true };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("set:2", fake.Log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Live_recheck_exception_after_first_Set_attempts_rollback(bool throwFromHasValue)
    {
        var fake = new Harness(2) { ThrowLiveRecheckAt = 2, ThrowLiveHasValue = throwFromHasValue };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("set:2", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
        Assert.Equal("old-1", fake.Value(1));
    }

    [Theory]
    [InlineData("non-string-storage")]
    [InlineData("read-only-parameter")]
    [InlineData("non-user-modifiable-definition")]
    [InlineData("owned-by-other-user")]
    public void Live_second_parameter_blocker_prevents_first_Set_despite_writable_DTO(string blocker)
    {
        var fake = new Harness(2) { LiveBlockerAt = 2, LiveBlocker = blocker };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain(fake.Log, entry => entry.StartsWith("set:", StringComparison.Ordinal));
        Assert.Equal("old-1", fake.Value(1));
    }

    [Fact]
    public void Second_in_transaction_readback_exception_rolls_back()
    {
        var fake = new Harness(2) { ThrowReadbackAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
        Assert.Equal("old-1", fake.Value(1));
    }

    [Fact]
    public void Empty_Before_uses_HasValue_without_calling_AsString()
    {
        var fake = new Harness(2) { NoValueAt = 2 };
        ChangeOperation second = fake.Plan.Operations[1];
        fake.Plan = fake.Plan with { Operations = [fake.Plan.Operations[0], second with
        {
            Before = new ParameterValue { Kind = ParameterValueKind.None, HasValue = false, IsReadOnly = false }
        }] };
        Assert.Equal(WriteOutcome.Committed, fake.Run().State);
    }

    [Fact]
    public void Resolved_parameter_from_another_document_is_rejected_before_any_Set()
    {
        var fake = new Harness(2) { ForeignDocumentAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("owner-id")]
    [InlineData("owner-uid")]
    [InlineData("parameter-id")]
    [InlineData("element-type")]
    public void Wrong_single_resolved_identity_prevents_every_write(string mismatch)
    {
        var fake = new Harness(2) { IdentityMismatchAt = 2, IdentityMismatch = mismatch };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("rollback", fake.Log);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Document_target_must_resolve_to_project_information()
    {
        var fake = new Harness(2) { DocumentTargetAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Document_target_on_project_information_can_commit()
    {
        var fake = new Harness(2) { DocumentTargetAt = 2, ProjectInformationAt = 2 };
        Assert.Equal(WriteOutcome.Committed, fake.Run().State);
    }

    [Fact]
    public void Element_target_resolving_actual_ProjectInformation_can_commit()
    {
        var fake = new Harness(2) { ProjectInformationAt = 2 };
        Assert.Equal(WriteOutcome.Committed, fake.Run().State);
    }

    [Fact]
    public void Document_and_Element_aliases_of_ProjectInformation_are_deduplicated_by_actual_ids()
    {
        var fake = new Harness(2) { DocumentTargetAt = 1, ProjectInformationAt = 1,
            AliasSecondToFirst = true };
        ChangeOperation[] operations = fake.Plan.Operations.ToArray();
        operations[1] = operations[1] with { Target = operations[0].Target with
            { Kind = ParameterTargetKind.Element, UniqueId = "uid-1", ElementId = 1 } };
        fake.Plan = fake.Plan with { Operations = operations };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain(fake.Log, entry => entry.StartsWith("set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Document_target_read_metadata_IsInstance_false_does_not_override_live_owner_identity()
    {
        var fake = new Harness(2) { DocumentTargetAt = 2, ProjectInformationAt = 2 };
        ChangeOperation[] operations = fake.Plan.Operations.ToArray();
        operations[1] = operations[1] with { Parameter = operations[1].Parameter with { IsInstance = false } };
        fake.Plan = fake.Plan with { Operations = operations };
        Assert.Equal(WriteOutcome.Committed, fake.Run().State);
    }

    [Theory]
    [InlineData("shared-guid")]
    [InlineData("shared-kind")]
    [InlineData("definition-id")]
    [InlineData("definition-stable-id")]
    [InlineData("definition-data-type")]
    public void Wrong_api_parameter_identity_prevents_all_writes(string mismatch)
    {
        var fake = new Harness(2) { ParameterFlavorAt = mismatch.StartsWith("shared", StringComparison.Ordinal) ? 2 : 3,
            IdentityMismatchAt = mismatch.StartsWith("shared", StringComparison.Ordinal) ? 2 : 1, IdentityMismatch = mismatch };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Shared_and_project_parameter_identities_can_commit(int flavor)
    {
        var fake = new Harness(2) { ParameterFlavorAt = flavor };
        Assert.Equal(WriteOutcome.Committed, fake.Run().State);
    }

    [Fact]
    public void Changed_revision_before_commit_never_commits()
    {
        var fake = new Harness(2) { AdvanceRevisionOnSetAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Contains("set:2", fake.Log);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Stale_revision_at_transaction_start_prevents_all_writes()
    {
        var fake = new Harness(2) { Revision = "stale" };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Revision_change_during_preflight_prevents_all_writes()
    {
        var fake = new Harness(2) { AdvanceRevisionOnReadAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(4)] // Pending
    [InlineData(0)] // Unknown
    [InlineData(2)] // Committed (inconsistent Start return)
    [InlineData(3)] // RolledBack
    public void Non_started_transaction_is_cleaned_without_false_outcome(int status)
    {
        var fake = new Harness(2) { StartStatus = (BatchTransactionStatus)status };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.Contains("dispose", fake.Log);
        Assert.False(fake.TransactionActive);
        Assert.DoesNotContain("commit", fake.Log);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_exception_cleans_up_without_false_rollback()
    {
        var fake = new Harness(2) { ThrowStart = true };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.Contains("dispose", fake.Log);
        Assert.False(fake.TransactionActive);
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Resolver_exception_rolls_back_before_any_write()
    {
        var fake = new Harness(2) { ThrowResolveAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Second_Set_exception_rolls_back_first_write()
    {
        var fake = new Harness(2) { ThrowSetAt = 2 };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.Equal("old-1", fake.Value(1));
        Assert.DoesNotContain("commit", fake.Log);
    }

    [Fact]
    public void Revision_provider_exception_before_writes_rolls_back()
    {
        var fake = new Harness(2) { ThrowRevision = true };
        Assert.Equal(WriteOutcome.RolledBack, fake.Run().State);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispose_exception_never_reports_a_confirmed_outcome()
    {
        var fake = new Harness(2) { ThrowDispose = true };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.Contains("dispose", fake.Log);
    }

    [Fact]
    public void Transaction_for_another_document_never_starts()
    {
        var fake = new Harness(2) { TransactionDocumentKey = "foreign-doc" };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.DoesNotContain("start", fake.Log);
        Assert.DoesNotContain(fake.Log, s => s.StartsWith("set:", StringComparison.Ordinal));
        Assert.Contains("dispose", fake.Log);
    }

    [Fact]
    public void Invalid_plan_disposes_supplied_transaction_without_starting()
    {
        var fake = new Harness(2);
        fake.Plan = fake.Plan with { Operations = [fake.Plan.Operations[0]] };
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => fake.Run());
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest, failure.Code);
        Assert.Equal(["dispose"], fake.Log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancellation_or_expiry_before_Start_disposes_supplied_transaction(bool cancel)
    {
        var fake = new Harness(2) { CancelBeforeStart = cancel, ExpireBeforeStart = !cancel };
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => fake.Run());
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest, failure.Code);
        Assert.Equal(["dispose"], fake.Log);
    }

    [Fact]
    public void Throwing_pre_start_clock_still_disposes_supplied_transaction()
    {
        var fake = new Harness(2) { ThrowClock = true };
        Assert.Equal(WriteOutcome.OutcomeUnknown, fake.Run().State);
        Assert.Equal(["dispose"], fake.Log);
    }

    private sealed class Harness : IBatchStringParameterResolver, IBatchWriteTransaction
    {
        private readonly Dictionary<string, FakeParameter> _parameters;
        internal readonly List<string> Log = [];
        internal int FalseSetAt;
        internal int MismatchAt;
        internal int CancelAfterSetAt;
        internal int ExpireAfterSetAt;
        internal int PostCommitMismatchAt;
        internal int StaleBeforeAt;
        internal int LiveBlockerAt;
        internal string LiveBlocker = "";
        internal int ThrowReadbackAt;
        internal int NoValueAt;
        internal int ForeignDocumentAt;
        internal int IdentityMismatchAt;
        internal string IdentityMismatch = "";
        internal int DocumentTargetAt;
        internal int ProjectInformationAt;
        internal int ParameterFlavorAt;
        internal int AdvanceRevisionOnReadAt;
        internal int AdvanceRevisionOnSetAt;
        internal int ThrowResolveAt;
        internal int ThrowSetAt;
        internal bool FirstSetMutatesSecond;
        internal bool SecondSetMutatesFirst;
        internal bool FirstSetBlocksSecond;
        internal bool MutateFirstAfterPreflight;
        internal bool FirstSetClearsSecondHasValue;
        internal int ThrowLiveRecheckAt;
        internal bool ThrowLiveHasValue;
        internal bool AliasSecondToFirst;
        internal bool ThrowStart;
        internal bool ThrowCommit;
        internal bool ThrowRollback;
        internal bool ThrowDispose;
        internal bool ThrowRevision;
        internal bool ThrowClock;
        internal bool CancelBeforeStart;
        internal bool ExpireBeforeStart;
        internal bool SuppressRevisionAdvance;
        internal BatchTransactionStatus StartStatus = BatchTransactionStatus.Started;
        internal BatchTransactionStatus RollbackStatus = BatchTransactionStatus.RolledBack;
        internal BatchTransactionStatus CommitStatus = BatchTransactionStatus.Committed;
        internal string Revision = "rev-1";
        internal string TransactionDocumentKey = "doc";
        public string DocumentKey => TransactionDocumentKey;
        private bool _committed;
        private bool _firstPreflightMutationDone;
        internal bool TransactionActive;
        private readonly CancellationTokenSource _cancellation = new();
        internal string Value(int number) => _parameters[$"op-{number}"].Value;
        internal ChangePlan Plan;
        internal Harness(int count)
        {
            Plan = new ChangePlan
            {
                PlanId = "plan", SessionId = "session", DocumentKey = "doc", ExpectedRevision = "rev-1",
                CreatedAtUtc = DateTimeOffset.Parse("2026-10-18T10:00:00Z"),
                ExpiresAtUtc = DateTimeOffset.Parse("2026-10-18T11:00:00Z"), PlanHash = new string('0', 64), Warnings = [],
                Operations = Enumerable.Range(1, count).Select(i => new ChangeOperation
                {
                    OperationId = $"op-{i}", Kind = ChangeOperationKind.SetParameterValue,
                    Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = $"uid-{i}", ElementId = i },
                    Parameter = new ParameterIdentity { Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001203,
                        StableId = "built-in:-1001203", Name = "Description" },
                    Before = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                        IsReadOnly = false, StringValue = $"old-{i}" },
                    After = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                        IsReadOnly = false, StringValue = $"new-{i}" }, Options = new Dictionary<string, string>()
                }).ToArray()
            };
            Plan = Plan with { PlanHash = ChangePlanHasher.ComputeHash(Plan) };
            _parameters = Enumerable.Range(1, count).ToDictionary(i => $"op-{i}", i => new FakeParameter(this, i));
        }
        public IBatchStringParameter Resolve(ChangeOperation operation)
        {
            int number = int.Parse(operation.OperationId.AsSpan(3));
            Log.Add($"resolve:{number}");
            if (ThrowResolveAt == number) throw new InvalidOperationException("resolution failure");
            return AliasSecondToFirst && number == 2 ? _parameters["op-1"] : _parameters[operation.OperationId];
        }
        public BatchTransactionStatus Start()
        {
            Log.Add("start");
            TransactionActive = true;
            if (ThrowStart) throw new InvalidOperationException("start may have activated transaction");
            return StartStatus;
        }
        public BatchTransactionStatus Commit()
        {
            Log.Add("commit");
            if (ThrowCommit) throw new InvalidOperationException("commit failed ambiguously");
            _committed = CommitStatus == BatchTransactionStatus.Committed;
            if (CommitStatus is BatchTransactionStatus.Committed or BatchTransactionStatus.RolledBack)
                TransactionActive = false;
            if (CommitStatus == BatchTransactionStatus.RolledBack)
                foreach (FakeParameter parameter in _parameters.Values) parameter.Restore();
            if (_committed && !SuppressRevisionAdvance) Revision = "rev-2";
            return CommitStatus;
        }
        public BatchTransactionStatus RollBack()
        {
            Log.Add("rollback");
            if (ThrowRollback) throw new InvalidOperationException("rollback failed");
            if (RollbackStatus == BatchTransactionStatus.RolledBack)
            {
                TransactionActive = false;
                foreach (FakeParameter parameter in _parameters.Values) parameter.Restore();
            }
            return RollbackStatus;
        }
        internal ApplyOutcome Run()
        {
            if (DocumentTargetAt > 0)
            {
                ChangeOperation[] operations = Plan.Operations.ToArray();
                operations[DocumentTargetAt - 1] = operations[DocumentTargetAt - 1] with
                { Target = new ParameterTarget { Kind = ParameterTargetKind.Document } };
                Plan = Plan with { Operations = operations };
            }
            if (ParameterFlavorAt > 0)
            {
                ChangeOperation[] operations = Plan.Operations.ToArray();
                int index = ParameterFlavorAt == 2 ? 1 : 0;
                operations[index] = operations[index] with { Parameter = ParameterFlavorAt == 2
                    ? new ParameterIdentity { Kind = ParameterIdentityKind.SharedGuid, SharedGuid = SharedId,
                        StableId = $"shared:{SharedId:D}", Name = "Shared" }
                    : new ParameterIdentity { Kind = ParameterIdentityKind.ParameterElement, DefinitionId = 201,
                        OwnerContext = "document:doc", StableId = "parameter-element:param-uid",
                        DataTypeId = "autodesk.spec.aec:string.text-2.0.0", Name = "Project" } };
                Plan = Plan with { Operations = operations };
            }
            Plan = Plan with { PlanHash = ChangePlanHasher.ComputeHash(Plan) };
            if (CancelBeforeStart) _cancellation.Cancel();
            return new BatchWriteTransactionRunner().Execute(Plan, this, this,
                () => ThrowRevision ? throw new InvalidOperationException("revision unavailable") : Revision,
                Clock, _cancellation.Token);
        }

        private DateTimeOffset Clock()
        {
            if (ThrowClock) throw new InvalidOperationException("clock unavailable");
            if (MutateFirstAfterPreflight && !_firstPreflightMutationDone &&
                Log.Contains("read:2") && !Log.Contains("set:1"))
            {
                _firstPreflightMutationDone = true;
                _parameters["op-1"].ChangeFromUpdater("updater-changed");
            }
            return ExpireBeforeStart || (ExpireAfterSetAt > 0 && Log.Contains($"set:{ExpireAfterSetAt}"))
                ? Plan.ExpiresAtUtc : DateTimeOffset.Parse("2026-10-18T10:01:00Z");
        }

        public void Dispose()
        {
            Log.Add("dispose");
            if (ThrowDispose) throw new InvalidOperationException("dispose failure");
            if (TransactionActive)
                foreach (FakeParameter parameter in _parameters.Values) parameter.Restore();
            TransactionActive = false;
        }

        private static readonly Guid SharedId = Guid.Parse("9b6ec07b-ce80-4ef6-b0ad-ce7e61159181");

        private sealed class FakeParameter(Harness owner, int number) : IBatchStringParameter
        {
            private string _value = $"old-{number}";
            internal string Value => _value;
            internal void Restore() => _value = $"old-{number}";
            internal void ChangeFromUpdater(string value) => _value = value;
            public long OwnerId => owner.IdentityMismatchAt == number && owner.IdentityMismatch == "owner-id" ? 999 : number;
            public string OwnerUniqueId => owner.IdentityMismatchAt == number && owner.IdentityMismatch == "owner-uid" ? "wrong" : $"uid-{number}";
            public bool IsProjectInformation => owner.ProjectInformationAt == number;
            public bool IsElementType => owner.IdentityMismatchAt == number && owner.IdentityMismatch == "element-type";
            public string DocumentKey => owner.ForeignDocumentAt == number ? "foreign-doc" : "doc";
            private bool IsShared => owner.ParameterFlavorAt == 2 && number == 2;
            private bool IsProjectParameter => owner.ParameterFlavorAt == 3 && number == 1;
            public long ParameterId => owner.IdentityMismatchAt == number && owner.IdentityMismatch == "parameter-id" ? -123 :
                IsProjectParameter ? 201 : IsShared ? 202 : -1001203;
            public ParameterIdentityKind ParameterKind => owner.IdentityMismatchAt == number && owner.IdentityMismatch == "shared-kind"
                ? ParameterIdentityKind.BuiltIn : IsProjectParameter ? ParameterIdentityKind.ParameterElement :
                    IsShared ? ParameterIdentityKind.SharedGuid : ParameterIdentityKind.BuiltIn;
            public Guid? SharedGuid => IsShared ? owner.IdentityMismatchAt == number && owner.IdentityMismatch == "shared-guid"
                ? Guid.NewGuid() : SharedId : null;
            public long? DefinitionId => IsProjectParameter ? owner.IdentityMismatchAt == number && owner.IdentityMismatch == "definition-id"
                ? 999 : 201 : null;
            public string? DefinitionStableId => IsProjectParameter ? owner.IdentityMismatchAt == number && owner.IdentityMismatch == "definition-stable-id"
                ? "parameter-element:wrong" : "parameter-element:param-uid" : null;
            public string? DataTypeId => IsProjectParameter ? owner.IdentityMismatchAt == number && owner.IdentityMismatch == "definition-data-type"
                ? "wrong" : "autodesk.spec.aec:string.text-2.0.0" : null;
            public bool HasStringStorage => (owner.LiveBlockerAt != number &&
                !(owner.FirstSetBlocksSecond && number == 2 && owner.Log.Contains("set:1"))) ||
                owner.LiveBlocker != "non-string-storage";
            public bool IsReadOnly => (owner.LiveBlockerAt == number ||
                (owner.FirstSetBlocksSecond && number == 2 && owner.Log.Contains("set:1"))) &&
                owner.LiveBlocker == "read-only-parameter";
            public bool IsUserModifiable => (owner.LiveBlockerAt != number &&
                !(owner.FirstSetBlocksSecond && number == 2 && owner.Log.Contains("set:1"))) ||
                owner.LiveBlocker != "non-user-modifiable-definition";
            public bool IsOwnedByOtherUser => (owner.LiveBlockerAt == number ||
                (owner.FirstSetBlocksSecond && number == 2 && owner.Log.Contains("set:1"))) &&
                owner.LiveBlocker == "owned-by-other-user";
            public bool HasValue => owner.ThrowLiveRecheckAt == number && owner.ThrowLiveHasValue &&
                owner.Log.Contains("set:1") ? throw new InvalidOperationException("live HasValue failure") :
                !(owner.FirstSetClearsSecondHasValue && number == 2 && owner.Log.Contains("set:1")) &&
                (owner.NoValueAt != number || _value != $"old-{number}");
            public string? AsString()
            {
                owner.Log.Add($"read:{number}");
                if (owner.ThrowLiveRecheckAt == number && !owner.ThrowLiveHasValue &&
                    owner.Log.Contains("set:1") && !owner.Log.Contains($"set:{number}"))
                    throw new InvalidOperationException("live AsString failure");
                if (owner.ThrowReadbackAt == number && owner.Log.Contains($"set:{number}"))
                    throw new InvalidOperationException("readback failure");
                if (owner.AdvanceRevisionOnReadAt == number) owner.Revision = "external-change";
                if (owner.NoValueAt == number && !HasValue) throw new InvalidOperationException("No value to read");
                if (owner.StaleBeforeAt == number && _value == $"old-{number}") return "stale";
                if (owner.MismatchAt == number && _value == $"new-{number}" && !owner._committed) return "mismatch";
                if (owner.PostCommitMismatchAt == number && owner._committed) return "mismatch";
                return _value;
            }
            public bool Set(string value)
            {
                owner.Log.Add($"set:{number}");
                if (owner.ThrowSetAt == number) throw new InvalidOperationException("set failure");
                if (owner.AdvanceRevisionOnSetAt == number) owner.Revision = "external-change";
                if (owner.CancelAfterSetAt == number) owner._cancellation.Cancel();
                if (owner.FalseSetAt == number) return false;
                _value = value;
                if (number == 1 && owner.FirstSetMutatesSecond)
                    owner._parameters["op-2"].ChangeFromUpdater("updater-changed");
                if (number == 2 && owner.SecondSetMutatesFirst)
                    owner._parameters["op-1"].ChangeFromUpdater("updater-changed");
                return true;
            }
        }
    }
}
