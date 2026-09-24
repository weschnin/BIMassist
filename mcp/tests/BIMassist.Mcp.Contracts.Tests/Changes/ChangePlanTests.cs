using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Changes;

public sealed class ChangePlanTests
{
    [Fact]
    public void Change_plan_rejects_duplicate_operation_ids()
    {
        ChangePlan original = CreatePlan();
        ChangeOperation duplicate = original.Operations[0] with { After = original.Operations[0].After };
        ChangePlan plan = original with { Operations = [original.Operations[0], duplicate], PlanHash = string.Empty };
        plan = plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };

        Assert.Throws<JsonException>(() => ContractJson.Serialize(plan));
    }

    [Fact]
    public void Change_plan_rejects_invalid_warning_entries()
    {
        ChangePlan plan = CreatePlan() with
        {
            Warnings = [new BIMassist.Mcp.Contracts.Protocol.BridgeWarning("", "warning")]
        };
        plan = plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };

        Assert.Throws<System.Text.Json.JsonException>(() => ContractValidator.Validate(plan));
    }

    [Fact]
    public void Plan_hash_is_stable_across_roundtrip()
    {
        ChangePlan plan = CreatePlan();
        string firstHash = ChangePlanHasher.ComputeHash(plan);

        ChangePlan restored = ContractJson.Deserialize<ChangePlan>(ContractJson.Serialize(plan));
        string secondHash = ChangePlanHasher.ComputeHash(restored);

        Assert.Equal(firstHash, secondHash);
        Assert.Equal(64, firstHash.Length);
        Assert.Matches("^[0-9a-f]{64}$", firstHash);
    }

    [Fact]
    public void Plan_hash_changes_when_requested_value_changes()
    {
        ChangePlan original = CreatePlan();
        ChangeOperation changedOperation = original.Operations[0] with
        {
            After = original.Operations[0].After! with { StringValue = "EI60" }
        };
        ChangePlan changed = original with { Operations = [changedOperation] };

        Assert.NotEqual(
            ChangePlanHasher.ComputeHash(original),
            ChangePlanHasher.ComputeHash(changed));
    }

    [Fact]
    public void Plan_hash_is_independent_of_dictionary_insertion_order()
    {
        ChangePlan first = CreatePlan();
        ChangePlan second = CreatePlan();

        first = first with
        {
            Operations =
            [
                first.Operations[0] with
                {
                    Options = new Dictionary<string, string>
                    {
                        ["zeta"] = "last",
                        ["alpha"] = "first"
                    }
                }
            ]
        };
        second = second with
        {
            Operations =
            [
                second.Operations[0] with
                {
                    Options = new Dictionary<string, string>
                    {
                        ["alpha"] = "first",
                        ["zeta"] = "last"
                    }
                }
            ]
        };

        Assert.Equal(
            ChangePlanHasher.ComputeHash(first),
            ChangePlanHasher.ComputeHash(second));
    }

    [Fact]
    public void Plan_hash_normalizes_equivalent_timestamp_offsets()
    {
        ChangePlan utc = CreatePlan();
        ChangePlan offset = utc with
        {
            CreatedAtUtc = DateTimeOffset.Parse("2026-09-21T14:00:00+02:00"),
            ExpiresAtUtc = DateTimeOffset.Parse("2026-09-21T14:05:00+02:00")
        };

        Assert.Equal(
            ChangePlanHasher.ComputeHash(utc),
            ChangePlanHasher.ComputeHash(offset));
    }

    [Fact]
    public void Apply_request_roundtrip_preserves_approval_and_idempotency_preconditions()
    {
        var request = new ApplyChangePlanRequest
        {
            PlanId = "plan-1",
            PlanHash = new string('a', 64),
            ExpectedRevision = "revision-4",
            IdempotencyKey = "idempotency-1",
            Approval = new ApprovalMetadata
            {
                ApprovedBy = "user:wesch",
                ApprovedAtUtc = DateTimeOffset.Parse("2026-09-21T12:01:00Z"),
                Source = "hermes"
            }
        };

        ApplyChangePlanRequest restored = ContractJson.Deserialize<ApplyChangePlanRequest>(
            ContractJson.Serialize(request));

        Assert.Equal("user:wesch", restored.Approval.ApprovedBy);
        Assert.Equal("hermes", restored.Approval.Source);
        Assert.Equal(request.PlanHash, restored.PlanHash);
        Assert.Equal(request.ExpectedRevision, restored.ExpectedRevision);
        Assert.Equal(request.IdempotencyKey, restored.IdempotencyKey);
    }

    private static ChangePlan CreatePlan()
    {
        var plan = new ChangePlan
        {
            PlanId = "plan-1",
            SessionId = "session-1",
            DocumentKey = "document-1",
            ExpectedRevision = "revision-4",
            CreatedAtUtc = DateTimeOffset.Parse("2026-09-21T12:00:00Z"),
            ExpiresAtUtc = DateTimeOffset.Parse("2026-09-21T12:05:00Z"),
            PlanHash = new string('0', 64),
            Operations =
            [
                new ChangeOperation
                {
                    OperationId = "operation-1",
                    Kind = ChangeOperationKind.SetParameterValue,
                    Target = new ParameterTarget
                    {
                        Kind = ParameterTargetKind.Element,
                        UniqueId = "unique-id-1",
                        ElementId = 42
                    },
                    Parameter = new ParameterIdentity
                    {
                        Kind = ParameterIdentityKind.SharedGuid,
                        SharedGuid = Guid.Parse("9f51461a-bda5-4a03-8e7d-fb52e6e7d50c"),
                        StableId = "shared:9f51461a-bda5-4a03-8e7d-fb52e6e7d50c",
                        Name = "Brandschutzklasse"
                    },
                    Before = new ParameterValue
                    {
                        Kind = ParameterValueKind.String,
                        HasValue = true,
                        StringValue = "EI30",
                        IsReadOnly = false
                    },
                    After = new ParameterValue
                    {
                        Kind = ParameterValueKind.String,
                        HasValue = true,
                        StringValue = "EI90",
                        IsReadOnly = false
                    },
                    Options = new Dictionary<string, string>()
                }
            ],
            Warnings = []
        };

        return plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
    }
}
