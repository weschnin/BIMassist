using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Serialization;

public sealed class ContractValidationTests
{
    [Fact]
    public void Document_operation_requires_session_and_document_context()
    {
        const string json = """
            {"protocolVersion":"1","schemaVersion":"1","requestId":"request-1","operation":"parameter.list","payload":{}}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Planning_operation_requires_revision_and_idempotency_key()
    {
        const string json = """
            {"protocolVersion":"1","schemaVersion":"1","requestId":"request-1","sessionId":"session-1","documentKey":"document-1","operation":"parameterValue.set.plan","payload":{}}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Apply_operation_rejects_empty_payload()
    {
        const string json = """
            {"protocolVersion":"1","schemaVersion":"1","requestId":"request-1","sessionId":"session-1","documentKey":"document-1","expectedRevision":"revision-1","idempotencyKey":"idem-1","operation":"changePlan.apply","payload":{}}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Apply_operation_rejects_transport_payload_idempotency_mismatch()
    {
        const string json = """
            {
              "protocolVersion":"1","schemaVersion":"1","requestId":"request-1",
              "sessionId":"session-1","documentKey":"document-1","expectedRevision":"revision-1",
              "idempotencyKey":"transport-idem","operation":"changePlan.apply",
              "payload":{
                "planId":"plan-1","planHash":"7a504df4e28608f192c022251b55d0668cfa8e8f1c697a9d111e3d2d1556ddcb",
                "expectedRevision":"revision-1","idempotencyKey":"payload-idem",
                "approval":{"approvedBy":"user:test","approvedAtUtc":"2026-09-21T12:00:00Z","source":"interactive"}
              }
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Serialization_rejects_semantically_invalid_contracts()
    {
        var response = new BridgeResponse
        {
            RequestId = "request-1",
            Success = true,
            Warnings = [],
            DurationMs = -1
        };

        Assert.Throws<JsonException>(() => ContractJson.Serialize(response));
    }

    [Fact]
    public void Response_rejects_negative_duration()
    {
        const string json = """
            {"requestId":"request-1","success":true,"warnings":[],"durationMs":-1}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeResponse>(json));
    }

    [Fact]
    public void Response_rejects_more_than_maximum_warnings()
    {
        string warnings = string.Join(',', Enumerable.Repeat("{\"code\":\"W\",\"message\":\"warning\"}", ContractLimits.MaximumWarnings + 1));
        string json = $"{{\"requestId\":\"request-1\",\"success\":true,\"warnings\":[{warnings}],\"durationMs\":1}}";

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeResponse>(json));
    }
    [Fact]
    public void Numeric_enum_values_are_rejected()
    {
        const string json = """
            {"kind":999,"hasValue":false}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterValue>(json));
    }

    [Fact]
    public void Scalar_request_payload_is_rejected()
    {
        const string json = """
            {
              "protocolVersion":"1",
              "schemaVersion":"1",
              "requestId":"request-1",
              "operation":"status.get",
              "payload":42
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Response_result_matches_success_state()
    {
        const string successWithoutResult = """
            {"requestId":"request-1","success":true,"warnings":[],"durationMs":1}
            """;
        const string failureWithResult = """
            {"requestId":"request-1","success":false,"result":{},"warnings":[],"error":{"code":"TEST","message":"failed"},"durationMs":1}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeResponse>(successWithoutResult));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeResponse>(failureWithResult));
    }

    [Fact]
    public void Optional_transport_metadata_is_bounded_even_when_operation_does_not_require_it()
    {
        string sessionId = new('s', ContractLimits.MaximumIdentifierLength + 1);
        string json = $$"""
            {
              "protocolVersion":"1",
              "schemaVersion":"1",
              "requestId":"request-1",
              "sessionId":"{{sessionId}}",
              "operation":"status.get",
              "payload":{}
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Optional_response_revision_is_bounded()
    {
        var response = new BridgeResponse
        {
            RequestId = "request-1",
            Success = true,
            Result = JsonDocument.Parse("{}").RootElement.Clone(),
            Warnings = [],
            DocumentRevision = new string('r', ContractLimits.MaximumRevisionLength + 1),
            DurationMs = 1
        };

        Assert.Throws<JsonException>(() => ContractJson.Serialize(response));
    }

    [Fact]
    public void Standalone_error_rejects_excessive_details()
    {
        IReadOnlyDictionary<string, string> details = Enumerable.Range(0, ContractLimits.MaximumErrorDetails + 1)
            .ToDictionary(index => $"key-{index}", _ => "value");
        var error = new BridgeError("TEST_ERROR", "Test error", details);

        Assert.Throws<JsonException>(() => ContractJson.Serialize(error));
    }

    [Fact]
    public void Oversized_request_identifier_is_rejected_before_contract_use()
    {
        string requestId = new('x', ContractLimits.MaximumIdentifierLength + 1);
        string json = $$"""
            {
              "protocolVersion":"1",
              "schemaVersion":"1",
              "requestId":"{{requestId}}",
              "operation":"status.get",
              "payload":{}
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Oversized_complete_contract_is_rejected_before_parsing()
    {
        string json = "{" + new string('x', ContractLimits.MaximumContractBytes) + "}";

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Approval_requires_non_default_utc_timestamp()
    {
        const string json = """
            {
              "planId":"plan-1",
              "planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "expectedRevision":"revision-1",
              "idempotencyKey":"idempotency-1",
              "approval":{
                "approvedBy":"user-1",
                "approvedAtUtc":"0001-01-01T00:00:00+00:00",
                "source":"test"
              }
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ApplyChangePlanRequest>(json));
    }

    [Fact]
    public void Failed_response_requires_error_details()
    {
        const string json = """
            {
              "requestId":"request-1",
              "success":false,
              "warnings":[],
              "durationMs":1
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeResponse>(json));
    }

    [Fact]
    public void Successful_response_rejects_error_details()
    {
        const string json = """
            {
              "requestId":"request-1",
              "success":true,
              "warnings":[],
              "error":{"code":"TRANSACTION_FAILED","message":"unexpected"},
              "durationMs":1
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeResponse>(json));
    }

    [Fact]
    public void Response_missing_warning_member_is_rejected()
    {
        const string json = """
            {
              "requestId":"request-1",
              "success":true,
              "durationMs":1
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeResponse>(json));
    }

    [Fact]
    public void Unknown_operation_is_rejected()
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

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Incompatible_protocol_version_is_rejected()
    {
        const string json = """
            {
              "protocolVersion":"2",
              "schemaVersion":"1",
              "requestId":"request-1",
              "operation":"status.get",
              "payload":{}
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Explicit_null_required_string_is_rejected()
    {
        const string json = """
            {
              "planId":null,
              "planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "expectedRevision":"revision-1",
              "idempotencyKey":"idempotency-1",
              "approved":true
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ApplyChangePlanRequest>(json));
    }

    [Fact]
    public void Missing_operations_are_rejected_before_hashing()
    {
        const string json = """
            {
              "planId":"plan-1",
              "sessionId":"session-1",
              "documentKey":"document-1",
              "expectedRevision":"revision-1",
              "createdAtUtc":"2026-09-21T12:00:00Z",
              "expiresAtUtc":"2026-09-21T12:05:00Z",
              "operations":null,
              "warnings":[],
              "planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(json));
    }

    [Fact]
    public void Missing_approval_metadata_is_rejected()
    {
        const string json = """
            {
              "planId":"plan-1",
              "planHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "expectedRevision":"revision-1",
              "idempotencyKey":"idempotency-1"
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ApplyChangePlanRequest>(json));
    }
}
