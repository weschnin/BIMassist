using System.Text.Json;
using System.Text.Json.Nodes;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Schemas;

public sealed class PlanSetParameterContractTests
{
    internal const string ValidRequest = """
        {"protocolVersion":"1","schemaVersion":"1","requestId":"r1","sessionId":"s1","documentKey":"d1","expectedRevision":"rev1","idempotencyKey":"key1","operation":"parameterValue.set.plan","payload":{"target":{"kind":"element","uniqueId":"uid1","elementId":42},"parameter":{"kind":"sharedGuid","sharedGuid":"9f51461a-bda5-4a03-8e7d-fb52e6e7d50c","stableId":"shared:guid","name":"Name"},"after":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"new"}}}
        """;

    [Fact]
    public void Valid_plan_payload_is_accepted_by_schema_and_semantics()
    {
        AssertParity(ValidRequest, true);
    }

    [Fact]
    public void Duplicate_plan_request_envelope_member_names_are_rejected()
    {
        string duplicate = ValidRequest.Replace("\"operation\":\"parameterValue.set.plan\"", "\"operation\":\"status.get\",\"operation\":\"parameterValue.set.plan\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(duplicate));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(System.Text.Encoding.UTF8.GetBytes(duplicate)));
    }

    [Fact]
    public void Duplicate_plan_payload_member_names_are_rejected()
    {
        string duplicate = ValidRequest.Replace("\"stringValue\":\"new\"", "\"stringValue\":\"old\",\"stringValue\":\"new\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(duplicate));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetParameterValueRequest>(JsonDocument.Parse(duplicate).RootElement.GetProperty("payload").GetRawText()));
    }

    [Fact]
    public void Standalone_payload_rejects_explicit_null_field_disallowed_by_schema()
    {
        JsonNode node = JsonNode.Parse(ValidRequest)!["payload"]!;
        node["after"]!["displayValue"] = null;
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetParameterValueRequest>(node.ToJsonString()));
    }

    [Fact]
    public void Plan_payload_root_uses_registered_source_generated_contract()
    {
        string json = JsonNode.Parse(ValidRequest)!["payload"]!.ToJsonString();
        PlanSetParameterValueRequest payload = ContractJson.Deserialize<PlanSetParameterValueRequest>(json);
        Assert.Equal(json, ContractJson.Serialize(payload));
        JsonNode invalid = JsonNode.Parse(json)!;
        invalid["after"]!["isReadOnly"] = true;
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PlanSetParameterValueRequest>(invalid.ToJsonString()));
    }

    [Theory]
    [InlineData("payload", "before", "{}")]
    [InlineData("payload", "options", "{}")]
    [InlineData("payload.after", "displayValue", "\"display\"")]
    [InlineData("payload.after", "displayValue", "null")]
    [InlineData("payload.after", "blockingReason", "null")]
    [InlineData("payload.target", "familyName", "null")]
    [InlineData("payload.parameter", "definitionId", "null")]
    [InlineData("payload.after", "blockingReason", "\"blocked\"")]
    [InlineData("payload.after", "formula", "\"formula\"")]
    [InlineData("payload.after", "integerValue", "1")]
    [InlineData("payload.after", "isReadOnly", "true")]
    [InlineData("payload.after", "hasValue", "false")]
    [InlineData("payload.after", "kind", "\"integer\"")]
    [InlineData("payload.target", "kind", "\"family\"")]
    [InlineData("payload.parameter", "kind", "\"parameterElement\"")]
    [InlineData("payload.parameter", "name", "\"   \"")]
    [InlineData("payload.target", "uniqueId", "\"   \"")]
    [InlineData("payload.parameter", "sharedGuid", "null")]
    [InlineData("payload", "unexpected", "1")]
    public void Invalid_plan_payload_is_rejected_by_schema_and_semantics(string path, string property, string value)
    {
        JsonNode node = JsonNode.Parse(ValidRequest)!;
        JsonNode target = node;
        foreach (string segment in path.Split('.')) target = target[segment]!;
        target[property] = JsonNode.Parse(value);
        AssertParity(node.ToJsonString(), false);
    }

    [Fact]
    public void Empty_plan_payload_is_rejected_by_both()
    {
        JsonNode node = JsonNode.Parse(ValidRequest)!;
        node["payload"] = new JsonObject();
        AssertParity(node.ToJsonString(), false);
    }

    [Fact]
    public void Built_in_identity_is_accepted_by_both()
    {
        JsonNode node = JsonNode.Parse(ValidRequest)!;
        node["payload"]!["parameter"] = JsonNode.Parse("""{"kind":"builtIn","builtInId":-1001203,"stableId":"builtin:description","name":"Description"}""");
        AssertParity(node.ToJsonString(), true);
    }

    [Theory]
    [InlineData("target", "elementId", "9223372036854775808")]
    [InlineData("parameter", "builtInId", "9223372036854775808")]
    [InlineData("parameter", "builtInId", "-9223372036854775809")]
    public void Plan_payload_rejects_integer_values_outside_Int64_range(string path, string property, string number)
    {
        JsonNode node = JsonNode.Parse(ValidRequest)!;
        JsonNode payload = node["payload"]!;
        if (path == "parameter")
        {
            payload["parameter"] = JsonNode.Parse("""{"kind":"builtIn","builtInId":-1001203,"stableId":"builtin:description","name":"Description"}""");
        }
        payload[path]![property] = JsonNode.Parse(number);
        AssertParity(node.ToJsonString(), false);
    }

    [Theory]
    [InlineData("builtInId")]
    [InlineData("doubleValue")]
    public void Change_plan_schema_accepts_fields_already_supported_by_semantics(string field)
    {
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "change-plan.valid.json"));
        JsonNode node = JsonNode.Parse(fixture)!;
        JsonNode operation = node["operations"]![0]!;
        if (field == "builtInId")
        {
            operation["parameter"] = JsonNode.Parse("""{"kind":"builtIn","builtInId":-1001203,"stableId":"builtin:description","name":"Description"}""");
        }
        else
        {
            // A double requires both displayed and internal values plus unit metadata.
            foreach (string side in new[] { "before", "after" })
                operation[side] = JsonNode.Parse("""{"kind":"double","hasValue":true,"isReadOnly":false,"doubleValue":2.5,"internalDoubleValue":2.5,"specTypeId":"spec:length","inputUnitTypeId":"unit:meters"}""");
        }
        ChangePlan unverified = JsonSerializer.Deserialize<ChangePlan>(node.ToJsonString(), ContractJson.Options)!;
        node["planHash"] = ChangePlanHasher.ComputeHash(unverified);
        string json = node.ToJsonString();
        Assert.True(Evaluate("change-plan.schema.json", json));
        Assert.NotNull(ContractJson.Deserialize<ChangePlan>(json));
    }

    private static void AssertParity(string json, bool expected)
    {
        Assert.Equal(expected, Evaluate("bridge-request.schema.json", json));
        if (expected) Assert.NotNull(ContractJson.Deserialize<BridgeRequest>(json));
        else Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    private static bool Evaluate(string name, string json) =>
        ContractSchemaFixtureTests.Evaluate(name, json).IsValid;
}
