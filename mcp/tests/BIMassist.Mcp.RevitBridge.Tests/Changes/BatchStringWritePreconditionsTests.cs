using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class BatchStringWritePreconditionsTests
{
    [Fact]
    public void Accepts_twenty_string_writes_with_three_stable_parameter_identity_kinds()
    {
        ChangePlan plan = Plan(20);
        var operations = plan.Operations.ToArray();
        operations[1] = operations[1] with { Parameter = Shared() };
        operations[2] = operations[2] with { Parameter = Project("doc-1") };
        plan = plan with { Operations = operations };

        Assert.Same(plan.Operations, BatchStringWritePreconditions.Validate(plan));
    }

    [Theory]
    [InlineData("name", 2_049, true)]
    [InlineData("name", 4_097, false)]
    [InlineData("stableId", 65, true)]
    [InlineData("stableId", 129, false)]
    [InlineData("uniqueId", 65, true)]
    [InlineData("uniqueId", 129, false)]
    [InlineData("ownerContext", 65, true)]
    [InlineData("ownerContext", 129, false)]
    [InlineData("dataTypeId", 65, true)]
    [InlineData("dataTypeId", 129, false)]
    public void Observed_batch_identity_uses_unicode_scalar_lengths(string field, int scalarCount, bool accepted)
    {
        string value = string.Concat(Enumerable.Repeat("\U0001F600", scalarCount));
        ChangePlan plan = Plan();
        ChangeOperation operation = plan.Operations[0];
        operation = field switch
        {
            "uniqueId" => operation with { Target = operation.Target with { UniqueId = value } },
            "name" => operation with { Parameter = operation.Parameter with { Name = value } },
            "stableId" => operation with { Parameter = operation.Parameter with { StableId = value } },
            "ownerContext" => operation with { Parameter = operation.Parameter with { OwnerContext = value } },
            _ => operation with { Parameter = operation.Parameter with { DataTypeId = value } }
        };
        plan = plan with { Operations = [operation, plan.Operations[1]] };
        if (accepted)
            Assert.Same(plan.Operations, BatchStringWritePreconditions.Validate(plan));
        else
            Invalid(plan);
    }

    [Theory]
    [InlineData(257, true)]
    [InlineData(513, false)]
    public void Batch_document_key_uses_unicode_scalar_limit(int scalarCount, bool accepted)
    {
        ChangePlan plan = Plan() with { DocumentKey = string.Concat(Enumerable.Repeat("\U0001F600", scalarCount)) };
        if (accepted)
            Assert.Same(plan.Operations, BatchStringWritePreconditions.Validate(plan));
        else
            Invalid(plan);
    }

    [Fact]
    public void Accepts_supplementary_batch_after_value_within_scalar_limit()
    {
        ChangePlan plan = Plan();
        string replacement = string.Concat(Enumerable.Repeat("\U0001F600", 16_385));
        var operations = plan.Operations.ToArray();
        operations[0] = operations[0] with { After = operations[0].After! with { StringValue = replacement } };
        plan = plan with { Operations = operations };
        Assert.Same(plan.Operations, BatchStringWritePreconditions.Validate(plan));
    }

    [Fact]
    public void Rejects_supplementary_batch_after_value_beyond_scalar_limit()
    {
        ChangePlan plan = Plan();
        string replacement = string.Concat(Enumerable.Repeat("\U0001F600", 32_769));
        var operations = plan.Operations.ToArray();
        operations[0] = operations[0] with { After = operations[0].After! with { StringValue = replacement } };
        Invalid(plan with { Operations = operations });
    }

    [Fact]
    public void Accepts_document_and_element_type_targets_and_empty_before_state()
    {
        ChangePlan plan = Plan();
        var operations = plan.Operations.ToArray();
        operations[0] = operations[0] with { Target = new ParameterTarget { Kind = ParameterTargetKind.Document },
            Before = new ParameterValue { Kind = ParameterValueKind.None, HasValue = false, IsReadOnly = false } };
        operations[1] = operations[1] with { Target = operations[1].Target with { Kind = ParameterTargetKind.ElementType } };
        plan = plan with { Operations = operations };
        Assert.Same(plan.Operations, BatchStringWritePreconditions.Validate(plan));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(21)]
    public void Rejects_operation_count_outside_two_through_twenty(int count) => Invalid(Plan(count));

    [Fact]
    public void Rejects_null_operation_and_duplicate_operation_ids()
    {
        ChangePlan plan = Plan();
        Invalid(plan with { Operations = [plan.Operations[0], null!] });
        Invalid(plan with { Operations = [plan.Operations[0], plan.Operations[1] with { OperationId = plan.Operations[0].OperationId }] });
    }

    [Fact]
    public void Rejects_non_write_kind_options_and_binding_or_definition_states()
    {
        ChangeOperation op = Plan().Operations[0];
        InvalidWith(op with { Kind = ChangeOperationKind.BindSharedParameter });
        InvalidWith(op with { Options = new Dictionary<string, string> { ["unexpected"] = "value" } });
        InvalidWith(op with { Options = null! });
        InvalidWith(op with { BeforeBinding = new ParameterBindingState { Kind = ParameterBindingKind.None, CategoryIds = [] } });
        InvalidWith(op with { AfterBinding = new ParameterBindingState { Kind = ParameterBindingKind.None, CategoryIds = [] } });
        InvalidWith(op with { BeforeDefinition = new SharedParameterDefinitionState { SharedGuid = Guid.NewGuid(), Name = "x", DataTypeId = "x", GroupName = "x", IsInstance = true } });
        InvalidWith(op with { AfterDefinition = new SharedParameterDefinitionState { SharedGuid = Guid.NewGuid(), Name = "x", DataTypeId = "x", GroupName = "x", IsInstance = true } });
    }

    [Fact]
    public void Rejects_imprecise_or_unsupported_targets()
    {
        ChangeOperation op = Plan().Operations[0];
        InvalidWith(op with { Target = null! });
        InvalidWith(op with { Target = op.Target with { UniqueId = null } });
        InvalidWith(op with { Target = op.Target with { UniqueId = " " } });
        InvalidWith(op with { Target = op.Target with { ElementId = null } });
        InvalidWith(op with { Target = op.Target with { ElementId = 0 } });
        InvalidWith(op with { Target = op.Target with { Kind = ParameterTargetKind.Family } });
        InvalidWith(op with { Target = new ParameterTarget { Kind = ParameterTargetKind.Document, ElementId = 1 } });
    }

    [Fact]
    public void Rejects_invalid_parameter_identity_in_each_discriminator()
    {
        ChangeOperation op = Plan().Operations[0];
        InvalidWith(op with { Parameter = null! });
        InvalidWith(op with { Parameter = op.Parameter with { Kind = ParameterIdentityKind.FamilyDefinition } });
        InvalidWith(op with { Parameter = op.Parameter with { BuiltInId = null } });
        InvalidWith(op with { Parameter = op.Parameter with { BuiltInId = 0 } });
        InvalidWith(op with { Parameter = Shared() with { SharedGuid = null } });
        InvalidWith(op with { Parameter = Project("doc-1") with { DefinitionId = 0 } });
        InvalidWith(op with { Parameter = Project("doc-1") with { StableId = "wrong" } });
    }

    [Theory]
    [InlineData("document:other")]
    [InlineData("document:DOC-1")]
    [InlineData("doc-1")]
    [InlineData(null)]
    public void Rejects_project_parameter_from_any_other_document(string? ownerContext) =>
        InvalidWith(Plan().Operations[0] with { Parameter = Project("doc-1") with { OwnerContext = ownerContext } });

    [Fact]
    public void Rejects_missing_before_or_after_and_inconsistent_string_payloads()
    {
        ChangeOperation op = Plan().Operations[0];
        InvalidWith(op with { Before = null });
        InvalidWith(op with { After = null });
        InvalidWith(op with { Before = op.Before! with { Kind = ParameterValueKind.Integer } });
        InvalidWith(op with { Before = op.Before! with { HasValue = false } });
        InvalidWith(op with { Before = op.Before! with { StringValue = null } });
        InvalidWith(op with { Before = op.Before! with { Kind = ParameterValueKind.None, HasValue = true } });
        InvalidWith(op with { Before = op.Before! with { Kind = ParameterValueKind.None, HasValue = false, StringValue = null },
            After = op.After! with { StringValue = null } });
        InvalidWith(op with { After = op.After! with { HasValue = false } });
        InvalidWith(op with { After = op.After! with { Kind = ParameterValueKind.None } });
        InvalidWith(op with { After = op.After! with { StringValue = null } });
    }

    [Fact]
    public void Rejects_unbounded_operation_ids_and_blocked_values()
    {
        ChangeOperation op = Plan().Operations[0];
        InvalidWith(op with { OperationId = new string('x', 2048) });
        InvalidWith(op with { Before = op.Before! with { BlockingReason = "locked" } });
        InvalidWith(op with { After = op.After! with { BlockingReason = "locked" } });
    }

    [Fact]
    public void Rejects_read_only_and_no_op_values()
    {
        ChangeOperation op = Plan().Operations[0];
        InvalidWith(op with { Before = op.Before! with { IsReadOnly = true } });
        InvalidWith(op with { After = op.After! with { IsReadOnly = true } });
        InvalidWith(op with { After = op.After! with { StringValue = op.Before!.StringValue } });
    }

    private static void InvalidWith(ChangeOperation operation)
    {
        ChangePlan plan = Plan();
        Invalid(plan with { Operations = [operation, plan.Operations[1]] });
    }

    private static void Invalid(ChangePlan plan) => Assert.Equal(BridgeErrorCodes.InvalidRequest,
        Assert.Throws<ChangePlanFailure>(() => BatchStringWritePreconditions.Validate(plan)).Code);

    private static ChangePlan Plan(int count = 2) => new()
    {
        PlanId = "plan-1", SessionId = "session-1", DocumentKey = "doc-1", ExpectedRevision = "rev-1",
        CreatedAtUtc = DateTimeOffset.Parse("2026-10-05T10:00:00Z"),
        ExpiresAtUtc = DateTimeOffset.Parse("2026-10-05T10:05:00Z"),
        PlanHash = new string('0', 64), Warnings = [],
        Operations = Enumerable.Range(1, count).Select(i => new ChangeOperation
        {
            OperationId = $"operation-{i}", Kind = ChangeOperationKind.SetParameterValue,
            Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = $"uid-{i}", ElementId = i },
            Parameter = new ParameterIdentity { Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001203,
                StableId = "built-in:-1001203", Name = "Description" },
            Before = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                IsReadOnly = false, StringValue = $"old-{i}" },
            After = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                IsReadOnly = false, StringValue = $"new-{i}" },
            Options = new Dictionary<string, string>()
        }).ToArray()
    };

    private static ParameterIdentity Shared() => new()
    {
        Kind = ParameterIdentityKind.SharedGuid, SharedGuid = Guid.Parse("12345678-1234-1234-1234-123456789abc"),
        StableId = "shared:12345678-1234-1234-1234-123456789abc", Name = "Shared"
    };

    private static ParameterIdentity Project(string documentKey) => new()
    {
        Kind = ParameterIdentityKind.ParameterElement, DefinitionId = 73,
        StableId = "parameter-element:parameter-uid-73", OwnerContext = $"document:{documentKey}",
        DataTypeId = "autodesk.spec.aec:string.text-2.0.0", Name = "Project"
    };
}
