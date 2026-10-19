using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class BatchApplyApprovalPromptTests
{
    [Fact]
    public void Twenty_brief_operations_show_every_exact_target_parameter_and_before_after_without_truncation()
    {
        ChangePlan plan = CreatePlan(20);
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");

        Assert.Contains("Sitzung: session-1", prompt);
        Assert.Contains("Dokument: Testmodell", prompt);
        Assert.Contains("Dokumentschlüssel: document-1", prompt);
        Assert.Contains($"Plan-Hash (SHA-256): {plan.PlanHash}", prompt);
        Assert.Equal(20, prompt.Split("Vorher:").Length - 1);
        Assert.Equal(20, prompt.Split("Nachher:").Length - 1);
        for (int i = 0; i < 20; i++)
        {
            Assert.Contains($"Element: element-{i} ({i + 1})", prompt);
            Assert.Contains($"Built-in ID {-1001 - i}", prompt);
            Assert.Contains($"built-in:{-1001 - i}", prompt);
            Assert.Contains($"Vorher: \"before-{i}\"", prompt);
            Assert.Contains($"Nachher: \"after-{i}\"", prompt);
        }
    }

    [Fact]
    public void Plan_id_is_visible_and_escaped_in_the_exact_plan_approval()
    {
        ChangePlan plan = Rehash(CreatePlan(2) with { PlanId = "plan\n\"\\\u202Eidentity" });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Contains("Plan-ID: plan\\u000A\\\"\\\\\\u202Eidentity", prompt);
        Assert.DoesNotContain("plan\n", prompt);
        Assert.DoesNotContain('\u202E', prompt);
    }

    [Fact]
    public void Identical_writes_with_different_revisions_are_distinguishable_without_the_hash()
    {
        ChangePlan original = CreatePlan(2);
        ChangePlan changed = Rehash(original with { ExpectedRevision = "session-1:1" });
        string first = BatchApplyApprovalPrompt.Format(original, "Testmodell");
        string second = BatchApplyApprovalPrompt.Format(changed, "Testmodell");
        Assert.Contains("Erwartete Revision: session-1:0", first);
        Assert.Contains("Erwartete Revision: session-1:1", second);
        Assert.NotEqual(first.Split("Plan-Hash (SHA-256):")[0], second.Split("Plan-Hash (SHA-256):")[0]);
    }

    [Fact]
    public void Creation_is_printed_as_invariant_round_trip_utc_including_fractional_ticks()
    {
        ChangePlan plan = Rehash(CreatePlan(2) with
        {
            CreatedAtUtc = new DateTimeOffset(2026, 10, 1, 14, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567)
        });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Contains("Erstellt (UTC): 2026-10-01T12:00:00.1234567+00:00", prompt);
    }

    [Fact]
    public void Identical_writes_with_different_expiry_are_distinguishable_without_the_hash()
    {
        ChangePlan original = CreatePlan(2);
        ChangePlan changed = Rehash(original with { ExpiresAtUtc = original.ExpiresAtUtc.AddTicks(1) });
        string first = BatchApplyApprovalPrompt.Format(original, "Testmodell");
        string second = BatchApplyApprovalPrompt.Format(changed, "Testmodell");
        Assert.Contains("Gültig bis (UTC): 2026-10-01T12:05:00.0000000+00:00", first);
        Assert.Contains("Gültig bis (UTC): 2026-10-01T12:05:00.0000001+00:00", second);
        Assert.NotEqual(first.Split("Plan-Hash (SHA-256):")[0], second.Split("Plan-Hash (SHA-256):")[0]);
    }

    [Fact]
    public void Revision_controls_quotes_backslashes_and_bidi_are_escaped()
    {
        ChangePlan plan = Rehash(CreatePlan(2) with { ExpectedRevision = "revision\n\"\\\u202Ehidden" });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Contains("Erwartete Revision: revision\\u000A\\\"\\\\\\u202Ehidden", prompt);
        Assert.DoesNotContain("revision\n", prompt);
        Assert.DoesNotContain('\u202E', prompt);
    }

    [Fact]
    public void Expanded_metadata_is_part_of_combined_prompt_and_fixed_hash_budget()
    {
        ChangePlan plan = CreatePlan(2);
        string baseline = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        int padding = BatchApplyApprovalPrompt.MaximumPromptLength - baseline.Length -
            BatchApplyApprovalPrompt.FixedHashText(plan.PlanHash).Length - 100;
        ChangeOperation first = plan.Operations[0] with
        {
            After = plan.Operations[0].After! with { StringValue = "after-0" + new string('x', padding) }
        };
        plan = Rehash(plan with { Operations = [first, plan.Operations[1]] });
        Assert.Equal(BatchApplyApprovalPrompt.MaximumPromptLength - 100,
            BatchApplyApprovalPrompt.Format(plan, "Testmodell").Length +
            BatchApplyApprovalPrompt.FixedHashText(plan.PlanHash).Length);
        plan = Rehash(plan with { ExpectedRevision = new string('r', 256) });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Large_individual_values_are_not_silently_truncated_or_accepted_above_prompt_limit()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            Before = plan.Operations[0].Before! with { StringValue = new string('b', 9000) },
            After = plan.Operations[0].After! with { StringValue = new string('a', 9000) }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Prompt_budget_includes_heading_and_separate_fixed_hash_region()
    {
        ChangePlan plan = CreatePlan(2);
        string baseline = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        int padding = BatchApplyApprovalPrompt.MaximumPromptLength - baseline.Length - 1;
        ChangeOperation operation = plan.Operations[0] with
        {
            After = plan.Operations[0].After! with { StringValue = "after-0" + new string('x', padding) }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Entire_prompt_at_exact_budget_is_accepted_without_truncation()
    {
        ChangePlan plan = CreatePlan(2);
        string baseline = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        int padding = BatchApplyApprovalPrompt.MaximumPromptLength - baseline.Length -
            BatchApplyApprovalPrompt.FixedHashText(plan.PlanHash).Length;
        string replacement = "after-0" + new string('x', padding);
        ChangeOperation operation = plan.Operations[0] with
        {
            After = plan.Operations[0].After! with { StringValue = replacement }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Equal(BatchApplyApprovalPrompt.MaximumPromptLength,
            prompt.Length + BatchApplyApprovalPrompt.FixedHashText(plan.PlanHash).Length);
        Assert.Contains("Nachher: \"" + replacement + "\"", prompt);
    }

    [Fact]
    public void Mismatched_hash_cannot_label_a_different_batch_as_approved()
    {
        ChangePlan plan = CreatePlan(2);
        plan = plan with { PlanHash = new string('f', 64) };
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Hash_bound_warnings_cannot_be_hidden_from_the_approval_prompt()
    {
        ChangePlan plan = Rehash(CreatePlan(2) with
        {
            Warnings = [new BIMassist.Mcp.Contracts.Protocol.BridgeWarning("model-warning", "unsafe value")]
        });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    // The batch modal's UIApplication owner and its cancellation lifecycle require
    // a live Revit acceptance test. ApprovalGateTests covers the pure expiry,
    // cancellation, scroll, and HWND predicates without loading RevitAPIUI.

    [Fact]
    public void Hidden_target_labels_are_rejected_instead_of_being_omitted()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            Target = plan.Operations[0].Target with { FamilyName = "hidden-target-label" }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Project_parameter_identity_includes_definition_stable_id_document_owner_and_data_type()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            Parameter = new ParameterIdentity
            {
                Kind = ParameterIdentityKind.ParameterElement, DefinitionId = 8742,
                StableId = "parameter-element:project-unique-id", Name = "Projekttext",
                OwnerContext = "document:document-1", DataTypeId = "autodesk.spec.aec:string.text-2.0.0"
            }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Contains("ParameterElement-ID 8742", prompt);
        Assert.Contains("parameter-element:project-unique-id", prompt);
        Assert.Contains("document:document-1", prompt);
        Assert.Contains("autodesk.spec.aec:string.text-2.0.0", prompt);
    }

    [Fact]
    public void Duplicate_operation_ids_are_rejected_as_an_ambiguous_batch()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation second = plan.Operations[1] with { OperationId = plan.Operations[0].OperationId };
        plan = Rehash(plan with { Operations = [plan.Operations[0], second] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Hash_bound_before_display_value_is_shown_and_escaped()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            Before = plan.Operations[0].Before! with { DisplayValue = "shown\ntext\\\"" }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Contains("Vorher (Anzeigewert): \"shown\\u000Atext\\\\\\\"\"", prompt);
        Assert.DoesNotContain("shown\ntext", prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unsupported_before_unit_metadata_is_rejected_not_hidden(bool spec)
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            Before = spec
                ? plan.Operations[0].Before! with { SpecTypeId = "spec-id" }
                : plan.Operations[0].Before! with { InputUnitTypeId = "unit-id" }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Theory]
    [InlineData("DisplayValue")]
    [InlineData("SpecTypeId")]
    [InlineData("InputUnitTypeId")]
    [InlineData("Formula")]
    public void Unsupported_hash_bound_after_metadata_is_rejected_not_hidden(string field)
    {
        ChangePlan plan = CreatePlan(2);
        ParameterValue after = plan.Operations[0].After!;
        after = field switch
        {
            "DisplayValue" => after with { DisplayValue = "hidden" },
            "SpecTypeId" => after with { SpecTypeId = "spec-id" },
            "InputUnitTypeId" => after with { InputUnitTypeId = "unit-id" },
            _ => after with { Formula = "formula", IsReadOnly = true, BlockingReason = "formula-controlled" }
        };
        plan = Rehash(plan with { Operations = [plan.Operations[0] with { After = after }, plan.Operations[1]] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Theory]
    [InlineData("before-read-only")]
    [InlineData("after-read-only")]
    [InlineData("before-blocked")]
    [InlineData("after-blocked")]
    [InlineData("no-op")]
    public void Formatter_refuses_unwritable_or_no_op_batch_before_any_approval(string condition)
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation op = plan.Operations[0];
        op = condition switch
        {
            "before-read-only" => op with { Before = op.Before! with { IsReadOnly = true } },
            "after-read-only" => op with { After = op.After! with { IsReadOnly = true } },
            "before-blocked" => op with { Before = op.Before! with { BlockingReason = "locked" } },
            "after-blocked" => op with { After = op.After! with { BlockingReason = "locked" } },
            _ => op with { After = op.After! with { StringValue = op.Before!.StringValue } }
        };
        plan = Rehash(plan with { Operations = [op, plan.Operations[1]] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Before_with_a_hidden_value_is_rejected_instead_of_displaying_no_value()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            Before = plan.Operations[0].Before! with { HasValue = false }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Controls_quotes_backslashes_and_bidi_markers_are_escaped_in_every_display_field()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            OperationId = "operation-1\nforged",
            Target = plan.Operations[0].Target with { UniqueId = "element-0\nforged" },
            Parameter = plan.Operations[0].Parameter with { Name = "Name\u202Eforged" },
            Before = plan.Operations[0].Before! with { StringValue = "before\rforged\\literal" },
            After = plan.Operations[0].After! with { StringValue = "after\u2028forged\"quote" }
        };
        plan = Rehash(plan with { SessionId = "session-1\nforged", DocumentKey = "document-1\u202Eforged",
            Operations = [operation, plan.Operations[1]] });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Title\nforged");
        Assert.Contains("session-1\\u000Aforged", prompt);
        Assert.Contains("document-1\\u202Eforged", prompt);
        Assert.Contains("Title\\u000Aforged", prompt);
        Assert.Contains("element-0\\u000Aforged", prompt);
        Assert.Contains("Name\\u202Eforged", prompt);
        Assert.Contains("before\\u000Dforged\\\\literal", prompt);
        Assert.Contains("after\\u2028forged\\\"quote", prompt);
        Assert.DoesNotContain("session-1\nforged", prompt);
        Assert.DoesNotContain("Title\nforged", prompt);
        Assert.DoesNotContain("element-0\nforged", prompt);
        Assert.DoesNotContain('\u202E', prompt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(21)]
    public void Batch_formatter_rejects_out_of_range_operation_counts(int count)
    {
        ChangePlan plan = CreatePlan(count);
        Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
            Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(plan, "Testmodell")).Code);
    }

    [Fact]
    public void Unsupported_and_malformed_string_operations_are_rejected()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation valid = plan.Operations[0];
        ChangeOperation[] invalid =
        [
            valid with { Kind = ChangeOperationKind.BindSharedParameter },
            valid with { Options = new Dictionary<string, string> { ["silent"] = "true" } },
            valid with { Before = null },
            valid with { After = valid.After! with { Kind = ParameterValueKind.Integer } },
            valid with { Target = valid.Target with { UniqueId = null } },
            valid with { Parameter = valid.Parameter with { Kind = ParameterIdentityKind.FamilyDefinition } }
        ];
        foreach (ChangeOperation operation in invalid)
        {
            ChangePlan candidate = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
            Assert.Equal(BIMassist.Mcp.Contracts.Protocol.BridgeErrorCodes.InvalidRequest,
                Assert.Throws<ChangePlanFailure>(() => BatchApplyApprovalPrompt.Format(candidate, "Testmodell")).Code);
        }
    }

    [Fact]
    public void Value_longer_than_single_prompt_limit_remains_complete_when_batch_is_inspectable()
    {
        ChangePlan plan = CreatePlan(2);
        string value = new string('z', 600);
        ChangeOperation operation = plan.Operations[0] with { After = plan.Operations[0].After! with { StringValue = value } };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Contains($"Nachher: \"{value}\"", prompt);
        Assert.True(prompt.Length <= BatchApplyApprovalPrompt.MaximumPromptLength);
    }

    [Fact]
    public void Document_target_and_absent_previous_value_remain_unambiguous()
    {
        ChangePlan plan = CreatePlan(2);
        ChangeOperation operation = plan.Operations[0] with
        {
            Target = new ParameterTarget { Kind = ParameterTargetKind.Document },
            Before = new ParameterValue { Kind = ParameterValueKind.None, HasValue = false, IsReadOnly = false }
        };
        plan = Rehash(plan with { Operations = [operation, plan.Operations[1]] });
        string prompt = BatchApplyApprovalPrompt.Format(plan, "Testmodell");
        Assert.Contains("Element: Projektinformationen (Dokument)", prompt);
        Assert.Contains("Vorher: <kein Wert>", prompt);
        Assert.Contains("Nachher: \"after-0\"", prompt);
    }

    private static ChangePlan Rehash(ChangePlan plan) => plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };

    private static ChangePlan CreatePlan(int count)
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        ChangeOperation[] operations = Enumerable.Range(0, count).Select(i => new ChangeOperation
        {
            OperationId = $"operation-{i + 1}", Kind = ChangeOperationKind.SetParameterValue,
            Target = new ParameterTarget { Kind = ParameterTargetKind.Element,
                UniqueId = $"element-{i}", ElementId = i + 1 },
            Parameter = new ParameterIdentity { Kind = ParameterIdentityKind.BuiltIn,
                BuiltInId = -1001 - i, StableId = $"built-in:{-1001 - i}", Name = $"Name-{i}" },
            Before = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                IsReadOnly = false, StringValue = $"before-{i}" },
            After = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                IsReadOnly = false, StringValue = $"after-{i}" },
            Options = new Dictionary<string, string>()
        }).ToArray();
        var plan = new ChangePlan
        {
            PlanId = "plan-batch", SessionId = "session-1", DocumentKey = "document-1",
            ExpectedRevision = "session-1:0", CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(5),
            PlanHash = new string('0', 64), Operations = operations, Warnings = []
        };
        return plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
    }
}
