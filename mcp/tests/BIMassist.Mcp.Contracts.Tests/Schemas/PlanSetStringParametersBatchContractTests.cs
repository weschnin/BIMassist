using System.Text.Json;
using System.Text.Json.Nodes;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Schemas;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Schemas;

public sealed class PlanSetStringParametersBatchContractTests
{
    private const string Schema = "plan-set-string-parameters-batch.schema.json";
    private const string Valid = """
        {"documentKey":"doc-1","items":[{"target":{"kind":"element","uniqueId":"uid-1","elementId":42},"parameter":{"kind":"sharedGuid","sharedGuid":"9f51461a-bda5-4a03-8e7d-fb52e6e7d50c","stableId":"shared:one","name":"Name"},"newString":"first"},{"target":{"kind":"elementType","uniqueId":"uid-2","elementId":43},"parameter":{"kind":"builtIn","builtInId":-1001203,"stableId":"builtin:description","name":"Description"},"newString":""}]}
        """;

    [Fact]
    public void Standalone_batch_schema_is_published_and_accepts_two_precise_string_changes()
    {
        Assert.Contains(Schema, JsonSchemaCatalog.Names);
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, Valid).IsValid);
    }

    [Fact]
    public void Batch_roundtrips_through_registered_source_generated_contract()
    {
        PlanSetStringParametersBatchRequest batch = ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(Valid);
        Assert.Equal("doc-1", batch.DocumentKey);
        Assert.Equal(2, batch.Items.Count);
        Assert.Equal("", batch.Items[1].NewString);
        Assert.Equal(JsonNode.Parse(Valid)!.ToJsonString(), ContractJson.Serialize(batch));
        Assert.NotNull(ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(System.Text.Encoding.UTF8.GetBytes(Valid)));
    }

    [Theory]
    [InlineData("target", "Element")]
    [InlineData("target", "eLeMeNtTyPe")]
    [InlineData("target", "DOCUMENT")]
    [InlineData("parameter", "SharedGuid")]
    [InlineData("parameter", "sHaReDgUiD")]
    [InlineData("parameter", "bUiLtIn")]
    public void Batch_and_item_raw_enum_spelling_must_match_schema_exactly(string field, string spelling)
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        node["items"]![0]![field]!["kind"] = spelling;
        string json = node.ToJsonString();
        string itemJson = node["items"]![0]!.ToJsonString();
        Assert.False(ContractSchemaFixtureTests.Evaluate(Schema, json).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(System.Text.Encoding.UTF8.GetBytes(json)));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<StringParameterBatchItem>(itemJson));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<StringParameterBatchItem>(System.Text.Encoding.UTF8.GetBytes(itemJson)));
    }

    [Fact]
    public void Batch_rejects_duplicate_canonical_identity_despite_different_metadata_and_value()
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        JsonNode copy = node["items"]![0]!.DeepClone();
        copy["parameter"]!["stableId"] = "client:other";
        copy["parameter"]!["name"] = "other display";
        copy["parameter"]!["sharedGuid"] = "9F51461A-BDA5-4A03-8E7D-FB52E6E7D50C";
        copy["newString"] = "second";
        node["items"]![1] = copy;
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, node.ToJsonString()).IsValid); // uniqueItems cannot see identity-only duplicates
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(node.ToJsonString()));
    }

    [Fact]
    public void Batch_rejects_duplicate_element_id_even_when_unique_id_disagrees()
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        node["items"]![1]!["target"] = JsonNode.Parse("""{"kind":"element","uniqueId":"conflicting-uid","elementId":42}""");
        node["items"]![1]!["parameter"] = node["items"]![0]!["parameter"]!.DeepClone();
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, node.ToJsonString()).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(node.ToJsonString()));
    }

    [Theory]
    [InlineData("items", "[]")]
    [InlineData("items", "[{}]")]
    [InlineData("documentKey", "null")]
    [InlineData("documentKey", "\"   \"")]
    [InlineData("items.0.target", "{\"kind\":\"element\",\"uniqueId\":\"uid-1\"}")]
    [InlineData("items.0.parameter", "{\"kind\":\"familyDefinition\",\"stableId\":\"x\",\"name\":\"x\"}")]
    [InlineData("items.0.newString", "null")]
    [InlineData("items.0.newString", "17")]
    public void Invalid_shapes_fail_schema_and_contract(string path, string jsonValue)
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        string[] parts = path.Split('.');
        JsonNode cursor = node;
        for (int i = 0; i < parts.Length - 1; i++) cursor = parts[i] == "0" ? cursor[0]! : cursor[parts[i]]!;
        cursor[parts[^1]] = JsonNode.Parse(jsonValue);
        string json = node.ToJsonString();
        Assert.False(ContractSchemaFixtureTests.Evaluate(Schema, json).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json));
    }

    [Fact]
    public void Typed_batch_rejects_null_entries_and_too_many_items()
    {
        PlanSetStringParametersBatchRequest batch = ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(Valid);
        Assert.Throws<JsonException>(() => ContractJson.Serialize(batch with { Items = [batch.Items[0], null!] }));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(batch with { Items = Enumerable.Repeat(batch.Items[0], 21).ToArray() }));
    }

    [Fact]
    public void Duplicate_and_explicit_null_members_are_rejected_before_deserialization()
    {
        string duplicate = Valid.Replace("\"newString\":\"first\"", "\"newString\":\"old\",\"newString\":\"first\"", StringComparison.Ordinal);
        foreach (string json in new[] {
            duplicate,
            Valid.Replace("\"documentKey\":\"doc-1\"", "\"documentKey\":\"doc-0\",\"documentKey\":\"doc-1\"", StringComparison.Ordinal),
            Valid.Replace("\"name\":\"Name\"", "\"name\":\"Name\",\"definitionId\":null", StringComparison.Ordinal)
        })
        {
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json));
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(System.Text.Encoding.UTF8.GetBytes(json)));
        }
    }

    [Fact]
    public void Both_sides_accept_document_and_project_parameter_identity()
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        node["items"]![0]!["target"] = JsonNode.Parse("""{"kind":"document"}""");
        node["items"]![0]!["parameter"] = JsonNode.Parse("""{"kind":"parameterElement","definitionId":42,"ownerContext":"document:doc-1","dataTypeId":"autodesk.spec.aec:string.text-2.0.0","stableId":"parameter-element:42","name":"Project"}""");
        string json = node.ToJsonString();
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, json).IsValid);
        Assert.Equal(ParameterIdentityKind.ParameterElement, ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json).Items[0].Parameter.Kind);
    }

    [Fact]
    public void Twenty_distinct_changes_are_accepted_and_twenty_one_rejected()
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        JsonArray items = (JsonArray)node["items"]!;
        for (int i = 2; i <= 20; i++)
        {
            JsonNode item = items[0]!.DeepClone();
            item["target"]!["uniqueId"] = $"uid-{i}";
            item["target"]!["elementId"] = 42 + i;
            items.Add(item);
        }
        string tooMany = node.ToJsonString();
        Assert.False(ContractSchemaFixtureTests.Evaluate(Schema, tooMany).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(tooMany));
        items.RemoveAt(items.Count - 1);
        string twenty = node.ToJsonString();
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, twenty).IsValid);
        Assert.Equal(20, ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(twenty).Items.Count);
    }

    [Fact]
    public void Item_root_rejects_duplicate_member_and_null_identity_metadata()
    {
        string item = JsonNode.Parse(Valid)!["items"]![0]!.ToJsonString();
        Assert.NotNull(ContractJson.Deserialize<StringParameterBatchItem>(item));
        string duplicate = item.Replace("\"uniqueId\":\"uid-1\"", "\"uniqueId\":\"alias\",\"uniqueId\":\"uid-1\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<StringParameterBatchItem>(duplicate));
        string explicitNull = item.Replace("\"name\":\"Name\"", "\"name\":\"Name\",\"ownerContext\":null", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<StringParameterBatchItem>(explicitNull));
    }

    [Fact]
    public void Project_parameter_owner_context_must_equal_batch_document_key()
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        node["items"]![0]!["parameter"] = JsonNode.Parse("""{"kind":"parameterElement","definitionId":42,"ownerContext":"document:doc-2","dataTypeId":"autodesk.spec.aec:string.text-2.0.0","stableId":"parameter-element:uid-42","name":"Project"}""");
        string json = node.ToJsonString();
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, json).IsValid); // Cross-field equality requires semantic validation.
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json));
    }

    [Fact]
    public void Supplementary_unicode_string_length_matches_schema_code_point_limit()
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        node["items"]![0]!["newString"] = string.Concat(Enumerable.Repeat("\U0001F600", 16_385));
        string json = node.ToJsonString();
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, json).IsValid);
        Assert.Equal(16_385, ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json).Items[0].NewString.EnumerateRunes().Count());
    }

    [Theory]
    [InlineData("documentKey", 512)]
    [InlineData("items.0.target.uniqueId", 128)]
    [InlineData("items.0.parameter.stableId", 128)]
    [InlineData("items.0.parameter.name", 4096)]
    [InlineData("items.0.parameter.ownerContext", 128)]
    [InlineData("items.0.parameter.dataTypeId", 128)]
    [InlineData("items.0.newString", 32768)]
    public void Batch_string_fields_accept_maximum_unicode_scalars_in_schema_and_runtime(string path, int maximum)
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        SetBatchField(node, path, new string('a', maximum - 1) + "\U0001F600");
        string json = node.ToJsonString();
        Assert.True(ContractSchemaFixtureTests.Evaluate(Schema, json).IsValid);
        PlanSetStringParametersBatchRequest batch = ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json);
        Assert.Equal(json, ContractJson.Serialize(batch));
        Assert.NotNull(ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(System.Text.Encoding.UTF8.GetBytes(json)));
        if (path.StartsWith("items.", StringComparison.Ordinal))
        {
            string itemJson = node["items"]![0]!.ToJsonString();
            Assert.NotNull(ContractJson.Deserialize<StringParameterBatchItem>(itemJson));
        }
    }

    [Theory]
    [InlineData("documentKey", 512)]
    [InlineData("items.0.target.uniqueId", 128)]
    [InlineData("items.0.parameter.stableId", 128)]
    [InlineData("items.0.parameter.name", 4096)]
    [InlineData("items.0.parameter.ownerContext", 128)]
    [InlineData("items.0.parameter.dataTypeId", 128)]
    [InlineData("items.0.newString", 32768)]
    public void Batch_string_fields_reject_more_than_maximum_unicode_scalars(string path, int maximum)
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        SetBatchField(node, path, new string('a', maximum) + "\U0001F600");
        string json = node.ToJsonString();
        Assert.False(ContractSchemaFixtureTests.Evaluate(Schema, json).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(json));
        if (path.StartsWith("items.", StringComparison.Ordinal))
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<StringParameterBatchItem>(node["items"]![0]!.ToJsonString()));
    }

    [Theory]
    [InlineData(257, 2)]
    [InlineData(512, 20)]
    public void Multi_string_change_plan_accepts_scalar_bounded_document_key_and_roundtrips_with_hash(int scalars, int operations)
    {
        string documentKey = string.Concat(Enumerable.Repeat("\U0001F600", scalars));
        JsonNode batchNode = JsonNode.Parse(Valid)!;
        batchNode["documentKey"] = documentKey;
        Assert.Equal(documentKey, ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(batchNode.ToJsonString()).DocumentKey);

        ChangePlan plan = CreateStringPlan(documentKey, operations);
        string serialized = ContractJson.Serialize(plan);
        ChangePlan restored = ContractJson.Deserialize<ChangePlan>(serialized);
        Assert.Equal(documentKey, restored.DocumentKey);
        Assert.Equal(operations, restored.Operations.Count);
        Assert.Equal(plan.PlanHash, restored.PlanHash);
        Assert.Equal(ChangePlanHasher.ComputeHash(restored), restored.PlanHash);
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
    public void Batch_observed_identity_survives_hashed_multi_string_plan_and_serializer(
        string field, int scalarCount, bool accepted)
    {
        string scalarText = string.Concat(Enumerable.Repeat("\U0001F600", scalarCount));
        JsonNode batchNode = JsonNode.Parse(Valid)!;
        JsonNode batchIdentity = field == "uniqueId"
            ? batchNode["items"]![0]!["target"]!
            : batchNode["items"]![0]!["parameter"]!;
        batchIdentity[field] = scalarText;
        string batchJson = batchNode.ToJsonString();
        Assert.Equal(accepted, ContractSchemaFixtureTests.Evaluate(Schema, batchJson).IsValid);
        if (accepted)
            Assert.NotNull(ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(batchJson));
        else
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(batchJson));

        ChangePlan plan = CreateStringPlan("doc-1", 2);
        ChangeOperation first = plan.Operations[0];
        first = field == "uniqueId"
            ? first with { Target = first.Target with { UniqueId = scalarText } }
            : first with { Parameter = first.Parameter with { Name = field == "name" ? scalarText : first.Parameter.Name,
                StableId = field == "stableId" ? scalarText : first.Parameter.StableId,
                OwnerContext = field == "ownerContext" ? scalarText : first.Parameter.OwnerContext,
                DataTypeId = field == "dataTypeId" ? scalarText : first.Parameter.DataTypeId } };
        plan = plan with { Operations = [first, plan.Operations[1]] };
        plan = plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
        if (accepted)
        {
            string json = ContractJson.Serialize(plan);
            ChangePlan restored = ContractJson.Deserialize<ChangePlan>(json);
            Assert.Equal(plan.PlanHash, ChangePlanHasher.ComputeHash(restored));
            Assert.Equal(scalarText, field switch
            {
                "uniqueId" => restored.Operations[0].Target.UniqueId,
                "name" => restored.Operations[0].Parameter.Name,
                "stableId" => restored.Operations[0].Parameter.StableId,
                "ownerContext" => restored.Operations[0].Parameter.OwnerContext,
                _ => restored.Operations[0].Parameter.DataTypeId
            });
        }
        else
        {
            Assert.Throws<JsonException>(() => ContractJson.Serialize(plan));
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(JsonSerializer.Serialize(plan, ContractJson.Options)));
        }
    }

    [Fact]
    public void Multi_string_change_plan_rejects_document_key_over_512_scalars()
    {
        string documentKey = string.Concat(Enumerable.Repeat("\U0001F600", 513));
        JsonNode batchNode = JsonNode.Parse(Valid)!;
        batchNode["documentKey"] = documentKey;
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(batchNode.ToJsonString()));

        ChangePlan plan = CreateStringPlan(documentKey, 2);
        Assert.Throws<JsonException>(() => ContractJson.Serialize(plan));
        string json = JsonSerializer.Serialize(plan, ContractJson.Options);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(json));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(21)]
    public void Non_batch_change_plan_keeps_legacy_utf16_document_key_limit(int operations)
    {
        ChangePlan plan = CreateStringPlan(string.Concat(Enumerable.Repeat("\U0001F600", 257)), operations);
        Assert.Throws<JsonException>(() => ContractJson.Serialize(plan));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(JsonSerializer.Serialize(plan, ContractJson.Options)));
    }

    [Fact]
    public void Mixed_change_plan_keeps_legacy_utf16_document_key_limit()
    {
        ChangePlan plan = CreateStringPlan(string.Concat(Enumerable.Repeat("\U0001F600", 257)), 2);
        ChangeOperation numericOperation = plan.Operations[1] with
        {
            Before = new ParameterValue { Kind = ParameterValueKind.Integer, HasValue = true, IsReadOnly = false, IntegerValue = 1 },
            After = new ParameterValue { Kind = ParameterValueKind.Integer, HasValue = true, IsReadOnly = false, IntegerValue = 2 }
        };
        plan = plan with { Operations = [plan.Operations[0], numericOperation] };
        plan = plan with { PlanHash = ChangePlanHasher.ComputeHash(plan) };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(plan));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(JsonSerializer.Serialize(plan, ContractJson.Options)));
    }

    private static ChangePlan CreateStringPlan(string documentKey, int operationCount)
    {
        JsonNode node = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "change-plan.valid.json")))!;
        node["documentKey"] = documentKey;
        JsonArray operations = (JsonArray)node["operations"]!;
        for (int index = 1; index < operationCount; index++)
        {
            JsonNode operation = operations[0]!.DeepClone();
            operation["operationId"] = $"operation-fixture-{index + 1}";
            operations.Add(operation);
        }
        ChangePlan unhashed = JsonSerializer.Deserialize<ChangePlan>(node.ToJsonString(), ContractJson.Options)!;
        return unhashed with { PlanHash = ChangePlanHasher.ComputeHash(unhashed) };
    }

    [Fact]
    public void Standalone_and_single_operation_identity_keep_utf16_limits()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 2_049));
        ChangePlan single = CreateStringPlan("doc-1", 1);
        ChangeOperation operation = single.Operations[0] with
        {
            Parameter = single.Operations[0].Parameter with { Name = name }
        };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(operation.Parameter));
        single = single with { Operations = [operation] };
        single = single with { PlanHash = ChangePlanHasher.ComputeHash(single) };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(single));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(JsonSerializer.Serialize(single, ContractJson.Options)));
    }

    [Fact]
    public void Single_write_still_uses_legacy_utf16_length_for_stable_id()
    {
        JsonNode single = JsonNode.Parse(PlanSetParameterContractTests.ValidRequest)!["payload"]!;
        single["parameter"]!["stableId"] = string.Concat(Enumerable.Repeat("\U0001F600", 65));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetParameterValueRequest>(single.ToJsonString()));
    }

    private static void SetBatchField(JsonNode node, string path, string value)
    {
        string[] segments = path.Split('.');
        JsonNode parent = node;
        for (int i = 0; i < segments.Length - 1; i++)
            parent = segments[i] == "0" ? parent[0]! : parent[segments[i]]!;
        parent[segments[^1]] = value;
    }

    [Fact]
    public void Integer_overflow_and_oversized_string_are_rejected_by_both()
    {
        JsonNode node = JsonNode.Parse(Valid)!;
        node["items"]![0]!["target"]!["elementId"] = JsonNode.Parse("9223372036854775808");
        Assert.False(ContractSchemaFixtureTests.Evaluate(Schema, node.ToJsonString()).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(node.ToJsonString()));
        node = JsonNode.Parse(Valid)!;
        node["items"]![0]!["newString"] = new string('x', 32769);
        Assert.False(ContractSchemaFixtureTests.Evaluate(Schema, node.ToJsonString()).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetStringParametersBatchRequest>(node.ToJsonString()));
    }
}
