using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class BatchSetParameterPlanBuilderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T10:00:00Z");

    [Fact]
    public void Builds_two_ordered_operations_from_independent_canonical_observations()
    {
        PlanSetStringParametersBatchRequest request = Request();
        ChangePlan plan = BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");

        Assert.Equal(2, plan.Operations.Count);
        Assert.Equal("doc-1", plan.DocumentKey);
        Assert.Equal("rev-1", plan.ExpectedRevision);
        Assert.Equal(Now.AddMinutes(5), plan.ExpiresAtUtc);
        Assert.Equal(ChangePlanHasher.ComputeHash(plan), plan.PlanHash);
        Assert.Same(plan.Operations, BatchStringWritePreconditions.Validate(plan));
        Assert.Equal("canonical-2", plan.Operations[0].Target.UniqueId);
        Assert.Equal("canonical-1", plan.Operations[1].Target.UniqueId);
        Assert.Equal("Revit name 2", plan.Operations[0].Parameter.Name);
        Assert.Equal("old-2", plan.Operations[0].Before?.StringValue);
        Assert.Equal("new-2", plan.Operations[0].After?.StringValue);
        Assert.Equal("new-1", plan.Operations[1].After?.StringValue);
        Assert.All(plan.Operations, op => Assert.Equal(ChangeOperationKind.SetParameterValue, op.Kind));
        Assert.All(plan.Operations, op => Assert.Empty(op.Options));
    }

    [Fact]
    public void Accepts_batch_valid_supplementary_identity_when_complete_approval_text_fits()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 1_024));
        PlanSetStringParametersBatchRequest request = Request() with
        {
            Items = [Item(2) with { Parameter = Identity(2, name) }, Item(1)]
        };
        ResolvedSetParameterPlan actual = Observed(2) with { Parameter = Identity(2, name) };
        ChangePlan plan = BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
            [actual, Observed(1)], Now, "Testmodell");
        Assert.Equal(name, ContractJson.Deserialize<ChangePlan>(ContractJson.Serialize(plan)).Operations[0].Parameter.Name);
        Assert.Equal(ChangePlanHasher.ComputeHash(plan), plan.PlanHash);
    }

    [Fact]
    public void Batch_valid_supplementary_identity_is_rejected_if_complete_approval_text_would_overflow()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 2_049));
        PlanSetStringParametersBatchRequest request = Request() with
        {
            Items = [Item(2) with { Parameter = Identity(2, name) }, Item(1)]
        };
        Assert.Equal(name, ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(
            ContractJson.Serialize(request)).Items[0].Parameter.Name);
        ResolvedSetParameterPlan actual = Observed(2) with { Parameter = Identity(2, name) };
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
                [actual, Observed(1)], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Code);
    }

    [Fact]
    public void Rejects_17000_character_replacement_that_cannot_fit_complete_approval_prompt()
    {
        var request = Request() with
        {
            Items = [Item(2) with { NewString = new string('x', 17_000) }, Item(1)]
        };
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
                [Observed(2), Observed(1)], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Code);
    }

    [Fact]
    public void Rejects_document_title_that_only_exceeds_prompt_budget_after_escaping()
    {
        string actualTitle = new('"', 8_000);
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
                [Observed(2), Observed(1)], Now, actualTitle));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Code);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1_000)]
    public void Batch_request_with_approvable_supplementary_scalars_builds_and_roundtrips_a_change_plan(int scalarCount)
    {
        string replacement = string.Concat(Enumerable.Repeat("\U0001F600", scalarCount));
        PlanSetStringParametersBatchRequest request = Request() with
        {
            Items = [Item(2) with { NewString = replacement }, Item(1)]
        };
        request = ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(ContractJson.Serialize(request));
        ChangePlan plan = BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");
        Assert.Same(plan.Operations, BatchStringWritePreconditions.Validate(plan));
        string serialized = ContractJson.Serialize(plan);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(serialized) <= ContractLimits.MaximumContractBytes);
        ChangePlan roundtrip = ContractJson.Deserialize<ChangePlan>(serialized);
        Assert.Equal(replacement, roundtrip.Operations[0].After!.StringValue);
        Assert.Equal(plan.PlanHash, roundtrip.PlanHash);
    }

    [Fact]
    public void Batch_request_rejects_more_than_32768_supplementary_scalars()
    {
        string replacement = string.Concat(Enumerable.Repeat("\U0001F600", 32_769));
        var request = Request() with { Items = [Item(2) with { NewString = replacement }, Item(1)] };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(request));
        Assert.Throws<JsonException>(() => BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell"));
    }

    [Fact]
    public void Batch_change_plan_rejects_more_than_32768_supplementary_scalars_even_with_valid_hash()
    {
        ChangePlan original = BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");
        var operations = original.Operations.ToArray();
        operations[0] = operations[0] with { After = operations[0].After! with
        {
            StringValue = string.Concat(Enumerable.Repeat("\U0001F600", 32_769))
        } };
        ChangePlan plan = original with { Operations = operations };
        plan = plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(plan));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(JsonSerializer.Serialize(plan, ContractJson.Options)));
    }

    [Fact]
    public void Standalone_parameter_value_operation_and_single_plan_retain_utf16_limit()
    {
        ChangePlan original = BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");
        ParameterValue after = original.Operations[0].After! with
        {
            StringValue = string.Concat(Enumerable.Repeat("\U0001F600", 16_385))
        };
        ChangeOperation operation = original.Operations[0] with { After = after };
        ChangePlan single = original with { Operations = [operation] };
        single = single with { PlanHash = ChangePlanHasher.ComputeHash(single) };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(after));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterValue>(JsonSerializer.Serialize(after, ContractJson.Options)));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(operation));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(single));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(JsonSerializer.Serialize(single, ContractJson.Options)));
    }

    [Fact]
    public void Identical_batch_observations_at_same_instant_produce_identical_plan_hash_and_ids()
    {
        ChangePlan first = BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");
        ChangePlan second = BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(first.Operations.Select(op => op.OperationId), second.Operations.Select(op => op.OperationId));
    }

    [Fact]
    public void Builds_twenty_operations_without_reordering()
    {
        var request = Request() with { Items = Enumerable.Range(1, 20).Select(Item).ToArray() };
        var observations = Enumerable.Range(1, 20).Select(Observed).ToArray();
        string title = "Twenty \"short\" items";
        ChangePlan plan = BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1", observations, Now, title);
        string prompt = BatchApplyApprovalPrompt.Format(plan, title);
        Assert.Contains("""Dokument: Twenty \"short\" items""", prompt);
        Assert.Equal(20, prompt.Split("Nachher:").Length - 1);
        Assert.True(prompt.Length + BatchApplyApprovalPrompt.FixedHashText(plan.PlanHash).Length <=
            BatchApplyApprovalPrompt.MaximumPromptLength);
        Assert.Equal(20, plan.Operations.Count);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"canonical-{i}"),
            plan.Operations.Select(op => op.Target.UniqueId));
    }

    [Fact]
    public void Rejects_mismatched_payload_document_key()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request() with { DocumentKey = "other-doc" }, "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.DocumentNotFound, failure.Code);
    }

    [Fact]
    public void Rejects_stale_second_observation_without_returning_partial_plan()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1) with { DocumentRevision = "rev-2" }], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.DocumentChanged, failure.Code);
    }

    [Fact]
    public void Rejects_second_target_when_resolved_unique_id_does_not_match_requested_identity()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1) with { Target = Target(1) with { UniqueId = "other" } }], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void Rejects_second_parameter_when_resolved_api_identity_does_not_match_requested_identity()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1) with { Parameter = Identity(3, "Other") }], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void Rejects_read_only_second_parameter()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1) with { Before = Observed(1).Before with { IsReadOnly = true } }], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.ParameterReadOnly, failure.Code);
    }

    [Fact]
    public void Rejects_blocked_second_observation_before_returning_a_plan()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1) with { Before = Observed(1).Before with { BlockingReason = "locked" } }], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.ParameterReadOnly, failure.Code);
    }

    [Fact]
    public void Rejects_blocked_empty_second_observation_before_returning_a_plan()
    {
        ParameterValue emptyButBlocked = new()
        {
            Kind = ParameterValueKind.None, HasValue = false, IsReadOnly = false,
            BlockingReason = "locked"
        };
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1) with { Before = emptyButBlocked }], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.ParameterReadOnly, failure.Code);
    }

    [Fact]
    public void Rejects_worksharing_blocked_second_observation_with_ownership_code()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanBuilder.Build(
            Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1) with { Before = Observed(1).Before with
                { IsReadOnly = true, BlockingReason = "element_owned_by_other_user" } }], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.WorksharingOwnership, failure.Code);
    }

    [Fact]
    public void Rejects_duplicate_requested_target_and_parameter_before_plan_creation()
    {
        var request = Request() with { Items = [Item(2), Item(2)] };
        Assert.Throws<System.Text.Json.JsonException>(() => BatchSetParameterPlanBuilder.Build(
            request, "session-1", "doc-1", "rev-1", [Observed(2), Observed(2)], Now, "Testmodell"));
    }

    [Fact]
    public void Rejects_document_and_project_information_element_alias_for_same_builtin_parameter()
    {
        ParameterTarget document = new() { Kind = ParameterTargetKind.Document };
        ParameterTarget projectInformation = new()
        {
            Kind = ParameterTargetKind.Element, UniqueId = "project-information-uid", ElementId = 42
        };
        ParameterIdentity identity = Identity(1, "Project information parameter");
        var request = Request() with { Items =
        [
            new StringParameterBatchItem { Target = document, Parameter = identity, NewString = "first" },
            new StringParameterBatchItem { Target = projectInformation, Parameter = identity, NewString = "second" }
        ] };
        var before = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
            IsReadOnly = false, StringValue = "old" };
        ResolvedSetParameterPlan[] observations =
        [
            new(document, identity, before, "rev-1") { CanonicalOwnerUniqueId = "project-information-uid", CanonicalParameterId = identity.BuiltInId },
            new(projectInformation, identity, before, "rev-1") { CanonicalOwnerUniqueId = "project-information-uid", CanonicalParameterId = identity.BuiltInId }
        ];

        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1", observations, Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.AmbiguousTarget, failure.Code);
    }

    [Fact]
    public void Rejects_observation_without_revit_derived_owner_identity()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
                [Observed(2) with { CanonicalOwnerUniqueId = null }, Observed(1)], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void Rejects_shared_guid_and_parameter_element_alias_of_same_revit_parameter_id()
    {
        ParameterIdentity shared = new() { Kind = ParameterIdentityKind.SharedGuid,
            SharedGuid = Guid.Parse("12345678-1234-1234-1234-123456789abc"),
            StableId = "shared:12345678-1234-1234-1234-123456789abc", Name = "Shared" };
        ParameterIdentity project = new() { Kind = ParameterIdentityKind.ParameterElement,
            DefinitionId = 73, StableId = "parameter-element:parameter-uid-73",
            OwnerContext = "document:doc-1", DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
            Name = "Project" };
        var request = Request() with { Items =
        [
            new StringParameterBatchItem { Target = Target(1), Parameter = shared, NewString = "first" },
            new StringParameterBatchItem { Target = Target(1), Parameter = project, NewString = "second" }
        ] };
        var before = Observed(1).Before;
        ResolvedSetParameterPlan[] observations =
        [
            new(Target(1), shared, before, "rev-1") { CanonicalOwnerUniqueId = "canonical-1", CanonicalParameterId = 73 },
            new(Target(1), project, before, "rev-1") { CanonicalOwnerUniqueId = "canonical-1", CanonicalParameterId = 73 }
        ];
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1", observations, Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.AmbiguousTarget, failure.Code);
    }

    [Fact]
    public void Rejects_observation_without_revit_derived_parameter_id()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
                [Observed(2) with { CanonicalParameterId = null }, Observed(1)], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void Rejects_twenty_maximum_length_before_and_after_values_before_returning_unserializable_plan()
    {
        string maximum = new('x', ContractLimits.MaximumValueTextLength);
        string previous = new('o', ContractLimits.MaximumValueTextLength);
        var request = Request() with { Items = Enumerable.Range(1, 20)
            .Select(index => Item(index) with { NewString = maximum }).ToArray() };
        var observations = Enumerable.Range(1, 20).Select(index => Observed(index) with
        {
            Before = Observed(index).Before with { StringValue = previous }
        }).ToArray();

        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1", observations, Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Code);
    }

    [Fact]
    public void Rejects_twenty_unapprovable_values_even_when_the_plan_fits_the_transport_budget()
    {
        const int textLength = 25_000;
        string previous = new('o', textLength);
        string replacement = new('x', textLength);
        var request = Request() with { Items = Enumerable.Range(1, 20)
            .Select(index => Item(index) with { NewString = replacement }).ToArray() };
        var observations = Enumerable.Range(1, 20).Select(index => Observed(index) with
        {
            Before = Observed(index).Before with { StringValue = previous }
        }).ToArray();
        ChangePlan baseline = BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");
        ChangePlan transportCandidate = baseline with
        {
            Operations = Enumerable.Range(1, 20).Select(index => baseline.Operations[0] with
            {
                OperationId = $"operation-{index}",
                Before = baseline.Operations[0].Before! with { StringValue = previous },
                After = baseline.Operations[0].After! with { StringValue = replacement }
            }).ToArray()
        };
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(transportCandidate, ContractJson.Options).Length <
            ContractLimits.MaximumContractBytes - 4_096);
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1", observations, Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Code);
    }

    [Fact]
    public void Accepts_exact_combined_approval_budget_and_rejects_one_extra_character()
    {
        ChangePlan baseline = BatchSetParameterPlanBuilder.Build(Request(), "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");
        string prompt = BatchApplyApprovalPrompt.Format(baseline, "Testmodell");
        int padding = BatchApplyApprovalPrompt.MaximumPromptLength - prompt.Length -
            BatchApplyApprovalPrompt.FixedHashText(baseline.PlanHash).Length;
        string replacement = "new-2" + new string('x', padding);
        PlanSetStringParametersBatchRequest request = Request() with
        {
            Items = [Item(2) with { NewString = replacement }, Item(1)]
        };
        ChangePlan plan = BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
            [Observed(2), Observed(1)], Now, "Testmodell");
        Assert.Equal(BatchApplyApprovalPrompt.MaximumPromptLength,
            BatchApplyApprovalPrompt.Format(plan, "Testmodell").Length +
            BatchApplyApprovalPrompt.FixedHashText(plan.PlanHash).Length);
        Assert.Equal(replacement, plan.Operations[0].After!.StringValue);
        request = request with { Items = [Item(2) with { NewString = replacement + "x" }, Item(1)] };
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() =>
            BatchSetParameterPlanBuilder.Build(request, "session-1", "doc-1", "rev-1",
                [Observed(2), Observed(1)], Now, "Testmodell"));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Code);
    }

    [Fact]
    public void Existing_single_operation_rejects_batch_payload()
    {
        var envelope = new BridgeRequest
        {
            ProtocolVersion = ProtocolVersions.ProtocolVersion, SchemaVersion = ProtocolVersions.SchemaVersion,
            RequestId = "request-1", SessionId = "session-1", DocumentKey = "doc-1",
            ExpectedRevision = "rev-1", IdempotencyKey = "plan-key-1",
            Operation = BridgeOperations.PlanSetParameterValues,
            Payload = JsonSerializer.SerializeToElement(Request(), ContractJson.Options)
        };
        Assert.Throws<JsonException>(() => SetParameterPlanBuilder.Build(
            envelope, Target(2), Identity(2, "Revit name 2"), Observed(2).Before, "rev-1", Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unapprovable_batch_does_not_reserve_registry_capacity(bool oversizedTitle)
    {
        var registry = new ChangePlanRegistry(maximumPlans: 1);
        string title = oversizedTitle ? new string('"', 8_000) : "Testmodell";
        var dispatcher = new BatchSetParameterPlanDispatcher(
            (PlanSetStringParametersBatchRequest payload, string session, string document,
                string revision, DateTimeOffset now) =>
                BatchSetParameterPlanBuilder.Build(payload, session, document, revision,
                    payload.Items.Select(item => Observed((int)item.Target.ElementId!.Value)).ToArray(),
                    now, title), registry, () => Now);
        PlanSetStringParametersBatchRequest oversized = Request() with
        {
            Items = [Item(2) with { NewString = new string('x', 17_000) }, Item(1)]
        };
        PlanSetStringParametersBatchRequest firstPayload = oversizedTitle ? Request() : oversized;
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => dispatcher.Plan(Envelope(firstPayload)));
        Assert.Equal(BridgeErrorCodes.LimitExceeded, failure.Code);
        title = "Testmodell";
        ChangePlan shortPlan = dispatcher.Plan(Envelope(Request()));
        Assert.Equal(2, shortPlan.Operations.Count);
    }

    private static BridgeRequest Envelope(PlanSetStringParametersBatchRequest payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion, SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-1", SessionId = "session-1", DocumentKey = "doc-1",
        ExpectedRevision = "rev-1", IdempotencyKey = "plan-key-1",
        Operation = BridgeOperations.PlanSetStringParametersBatch,
        Payload = JsonSerializer.SerializeToElement(payload, ContractJson.Options)
    };

    private static PlanSetStringParametersBatchRequest Request() => new()
    {
        DocumentKey = "doc-1",
        Items = [Item(2), Item(1)]
    };

    private static StringParameterBatchItem Item(int number) => new()
    {
        Target = Target(number), Parameter = Identity(number, $"Client name {number}"),
        NewString = $"new-{number}"
    };

    private static ResolvedSetParameterPlan Observed(int number) => new(
        Target(number), Identity(number, $"Revit name {number}"),
        new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
            IsReadOnly = false, StringValue = $"old-{number}" }, "rev-1")
        { CanonicalOwnerUniqueId = $"canonical-{number}", CanonicalParameterId = -1001200 - number };

    private static ParameterTarget Target(int number) => new()
    {
        Kind = ParameterTargetKind.Element, UniqueId = $"canonical-{number}", ElementId = number
    };

    private static ParameterIdentity Identity(int number, string name) => new()
    {
        Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001200 - number,
        StableId = $"built-in:{-1001200 - number}", Name = name
    };
}
