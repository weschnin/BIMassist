using System.Text.Json;
using System.Collections.Concurrent;

using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Schemas;
using BIMassist.Mcp.Contracts.Serialization;
using Json.Schema;

namespace BIMassist.Mcp.Contracts.Tests.Schemas;

public sealed class ContractSchemaFixtureTests
{
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> SchemaCache = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("bridge-request.schema.json", "bridge-request.valid.json")]
    [InlineData("bridge-response.schema.json", "bridge-response.error.json")]
    [InlineData("change-plan.schema.json", "change-plan.valid.json")]
    [InlineData("apply-change-plan.schema.json", "apply-change-plan.valid.json")]
    public void Valid_fixtures_satisfy_their_json_schemas(string schemaName, string fixtureName)
    {
        EvaluationResults result = Evaluate(schemaName, ReadFixture(fixtureName));

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Bridge_request_schema_rejects_unknown_operation()
    {
        const string json = """
            {
              "protocolVersion":"1",
              "schemaVersion":"1",
              "requestId":"request-1",
              "operation":"arbitrary.execute",
              "payload":{}
            }
            """;

        Assert.False(Evaluate("bridge-request.schema.json", json).IsValid);
    }

    [Fact]
    public void Bridge_response_schema_enforces_result_error_invariants()
    {
        const string successWithoutResult = """
            {"requestId":"request-1","success":true,"warnings":[],"durationMs":1}
            """;
        const string failureWithResult = """
            {"requestId":"request-1","success":false,"result":{},"warnings":[],"error":{"code":"TEST","message":"failed"},"durationMs":1}
            """;

        Assert.False(Evaluate("bridge-response.schema.json", successWithoutResult).IsValid);
        Assert.False(Evaluate("bridge-response.schema.json", failureWithResult).IsValid);
    }

    [Fact]
    public void Apply_schema_rejects_invalid_date_time_format()
    {
        string json = ReadFixture("apply-change-plan.valid.json")
            .Replace("2026-09-21T12:01:00Z", "not-a-date", StringComparison.Ordinal);

        Assert.False(Evaluate("apply-change-plan.schema.json", json).IsValid);
    }

    [Fact]
    public void Change_plan_schema_rejects_oversized_identifiers_and_values()
    {
        string oversizedId = new('u', ContractLimits.MaximumIdentifierLength + 1);
        string oversizedValue = new('x', ContractLimits.MaximumValueTextLength + 1);
        string identifierJson = ReadFixture("change-plan.valid.json")
            .Replace("unique-fixture-1", oversizedId, StringComparison.Ordinal);
        string valueJson = ReadFixture("change-plan.valid.json")
            .Replace("EI30", oversizedValue, StringComparison.Ordinal);

        Assert.False(Evaluate("change-plan.schema.json", identifierJson).IsValid);
        Assert.False(Evaluate("change-plan.schema.json", valueJson).IsValid);
    }

    [Fact]
    public void Change_plan_schema_rejects_operation_kind_extraneous_state()
    {
        string json = ReadFixture("change-plan.valid.json")
            .Replace("\"options\": {}", "\"afterBinding\":{\"kind\":\"projectInstance\",\"categoryIds\":[\"-2000011\"]},\"options\":{}", StringComparison.Ordinal);

        Assert.False(Evaluate("change-plan.schema.json", json).IsValid);
    }

    [Fact]
    public void Change_plan_schema_rejects_reference_value_with_scalar_payload()
    {
        string json = ReadFixture("change-plan.valid.json")
            .Replace(
                "\"kind\": \"string\",\n        \"hasValue\": true,\n        \"isReadOnly\": false,\n        \"stringValue\": \"EI30\"",
                "\"kind\": \"elementReference\",\n        \"hasValue\": true,\n        \"isReadOnly\": false,\n        \"stringValue\": \"unexpected\",\n        \"reference\":{\"uniqueId\":\"uid-1\",\"elementId\":42,\"referenceKind\":\"element\"}",
                StringComparison.Ordinal);

        Assert.False(Evaluate("change-plan.schema.json", json).IsValid);
    }

    [Fact]
    public void Change_plan_schema_rejects_incomplete_nested_objects()
    {
        string json = ReadFixture("change-plan.valid.json")
            .Replace("\"target\": {", "\"target\": { \"unexpected\": true,", StringComparison.Ordinal);

        Assert.False(Evaluate("change-plan.schema.json", json).IsValid);
    }

    [Theory]
    [InlineData("bridge-request.schema.json")]
    [InlineData("bridge-response.schema.json")]
    [InlineData("change-plan.schema.json")]
    [InlineData("apply-change-plan.schema.json")]
    public void Embedded_schema_is_strict_draft_2020_12_object_schema(string schemaName)
    {
        string schemaJson = JsonSchemaCatalog.Get(schemaName);
        using JsonDocument schema = JsonDocument.Parse(schemaJson);
        JsonElement root = schema.RootElement;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.NotEmpty(root.GetProperty("required").EnumerateArray());
    }

    [Fact]
    public void Valid_bridge_request_fixture_deserializes()
    {
        BridgeRequest request = ContractJson.Deserialize<BridgeRequest>(ReadFixture("bridge-request.valid.json"));

        Assert.Equal("request-fixture-1", request.RequestId);
        Assert.Equal("status.get", request.Operation);
    }

    [Fact]
    public void Unknown_fixture_field_is_rejected()
    {
        Assert.Throws<JsonException>(() =>
            ContractJson.Deserialize<BridgeRequest>(ReadFixture("bridge-request.unknown-field.json")));
    }

    [Fact]
    public void Error_response_fixture_deserializes_with_stable_error_code()
    {
        BridgeResponse response = ContractJson.Deserialize<BridgeResponse>(ReadFixture("bridge-response.error.json"));

        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.DocumentChanged, response.Error?.Code);
    }

    [Fact]
    public void Change_plan_fixture_hash_matches_embedded_hash()
    {
        ChangePlan plan = ContractJson.Deserialize<ChangePlan>(ReadFixture("change-plan.valid.json"));

        Assert.Equal(plan.PlanHash, ChangePlanHasher.ComputeHash(plan));
    }

    [Fact]
    public void Apply_fixture_deserializes_with_explicit_approval()
    {
        ApplyChangePlanRequest request = ContractJson.Deserialize<ApplyChangePlanRequest>(
            ReadFixture("apply-change-plan.valid.json"));

        Assert.Equal("user:wesch", request.Approval.ApprovedBy);
        Assert.Equal(64, request.PlanHash.Length);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static EvaluationResults Evaluate(string schemaName, string json)
    {
        JsonSchema schema = SchemaCache.GetOrAdd(
            schemaName,
            name => new Lazy<JsonSchema>(
                () => JsonSchema.FromText(JsonSchemaCatalog.Get(name)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        using JsonDocument document = JsonDocument.Parse(json);
        return schema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });
    }
}
