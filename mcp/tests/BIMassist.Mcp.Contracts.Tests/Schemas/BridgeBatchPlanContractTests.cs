using System.Text.Json;
using System.Text.Json.Nodes;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Schemas;

public sealed class BridgeBatchPlanContractTests
{
    internal const string Valid = """
        {"protocolVersion":"1","schemaVersion":"1","requestId":"r1","sessionId":"s1","documentKey":"d1","expectedRevision":"rev1","idempotencyKey":"key1","operation":"parameterValue.set.batch.plan","payload":{"documentKey":"d1","items":[{"target":{"kind":"element","uniqueId":"uid1","elementId":42},"parameter":{"kind":"builtIn","builtInId":-101,"stableId":"built-in:-101","name":"Name"},"newString":"one"},{"target":{"kind":"element","uniqueId":"uid2","elementId":43},"parameter":{"kind":"builtIn","builtInId":-101,"stableId":"built-in:-101","name":"Name"},"newString":"two"}]}}
        """;

    [Fact]
    public void Dedicated_batch_operation_roundtrips_and_matches_schema()
    {
        AssertParity(Valid, true);
        BridgeRequest request = ContractJson.Deserialize<BridgeRequest>(Valid);
        Assert.Equal(Valid, ContractJson.Serialize(request));
    }

    [Fact]
    public void Batch_transport_document_key_accepts_257_supplementary_scalars_without_relaxing_single_path()
    {
        string key = string.Concat(Enumerable.Repeat("\U0001F600", 257));
        JsonNode batch = JsonNode.Parse(Valid)!;
        batch["documentKey"] = key;
        batch["payload"]!["documentKey"] = key;
        string json = batch.ToJsonString();
        AssertParity(json, true);
        Assert.Equal(key, ContractJson.Deserialize<BridgeRequest>(ContractJson.Serialize(ContractJson.Deserialize<BridgeRequest>(json))).DocumentKey);
        batch["operation"] = BridgeOperations.PlanSetParameterValues;
        batch["payload"] = JsonNode.Parse(PlanSetParameterContractTests.ValidRequest)!["payload"]!.DeepClone();
        // Existing single-write UTF-16 bound remains stricter than schema's Unicode-scalar maxLength.
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(batch.ToJsonString()));
    }

    [Fact]
    public void Batch_envelope_document_key_must_match_payload_even_when_both_valid()
    {
        JsonNode batch = JsonNode.Parse(Valid)!;
        batch["payload"]!["documentKey"] = "foreign";
        // Cross-field equality cannot be expressed in the structural schema.
        Assert.True(ContractSchemaFixtureTests.Evaluate("bridge-request.schema.json", batch.ToJsonString()).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(batch.ToJsonString()));
    }

    [Theory]
    [InlineData("{\"kind\":\"element\",\"uniqueId\":\"uid1\",\"elementId\":42,\"elementId\":42}")]
    [InlineData("{\"kind\":\"element\",\"uniqueId\":\"uid1\",\"elementId\":42,\"familyName\":null}")]
    public void Nested_duplicate_and_null_fields_rejected_before_typed_conversion(string target)
    {
        string json = Valid.Replace("{\"kind\":\"element\",\"uniqueId\":\"uid1\",\"elementId\":42}", target, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("payload", "after", "{}")]
    [InlineData("payload.items.0", "newString", "null")]
    [InlineData("payload.items.0.parameter", "kind", "\"familyDefinition\"")]
    [InlineData("payload.items.0.target", "elementId", "9223372036854775808")]
    public void Invalid_batch_payload_fails_schema_and_runtime(string path, string field, string value)
    {
        JsonNode root = JsonNode.Parse(Valid)!;
        JsonNode node = root;
        foreach (string part in path.Split('.')) node = part == "0" ? node[0]! : node[part]!;
        node[field] = JsonNode.Parse(value);
        AssertParity(root.ToJsonString(), false);
    }

    private static void AssertParity(string json, bool valid)
    {
        Assert.Equal(valid, ContractSchemaFixtureTests.Evaluate("bridge-request.schema.json", json).IsValid);
        if (valid) Assert.NotNull(ContractJson.Deserialize<BridgeRequest>(json));
        else Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }
}
