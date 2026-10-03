using System.ComponentModel;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Server.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BIMassist.Mcp.Server.Tools;

internal static class TestWriteFeatureGate
{
    internal const string EnvironmentVariable = "BIMASSIST_MCP_ENABLE_TEST_WRITES";
    internal const string DocumentKeyEnvironmentVariable = "BIMASSIST_MCP_TEST_DOCUMENT_KEY";

    internal static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal) &&
        IsValidDocumentKey(Environment.GetEnvironmentVariable(DocumentKeyEnvironmentVariable));

    internal static void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Test-only Revit writes are disabled. Set BIMASSIST_MCP_ENABLE_TEST_WRITES=1 and BIMASSIST_MCP_TEST_DOCUMENT_KEY to an exact disposable test-document key.");
        }
    }

    internal static void EnsureDocumentAllowed(string documentKey)
    {
        EnsureEnabled();
        string allowedDocumentKey = Environment.GetEnvironmentVariable(DocumentKeyEnvironmentVariable)!;
        if (!string.Equals(documentKey, allowedDocumentKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Test-only writes are restricted to the allowlisted test document.");
        }
    }

    private static bool IsValidDocumentKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 1024 &&
        !value.Any(char.IsControl);
}

[McpServerToolType]
public static class RevitTestWriteTools
{
    private static readonly string[] PlanArgumentKeys =
    [
        "revitProcessId", "sessionId", "documentKey", "expectedRevision", "idempotencyKey", "target", "parameter", "newValue"
    ];

    private static readonly string[] ApplyArgumentKeys =
    [
        "revitProcessId", "sessionId", "documentKey", "planId", "planHash", "expectedRevision", "idempotencyKey",
        "approvedBy", "approvedAtUtc", "approvalSource"
    ];

    [McpServerTool(Name = "revit_test_plan_set_parameter_string", ReadOnly = true, Destructive = false, Idempotent = false)]
    [Description("TEST ONLY: create a non-mutating plan to set exactly one stable-identity Revit string parameter on the document named by BIMASSIST_MCP_TEST_DOCUMENT_KEY. Intended only for a disposable test RVT; Apply still requires the Revit-owned confirmation dialog.")]
    public static Task<string> PlanSetParameterString(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        string expectedRevision,
        string idempotencyKey,
        ParameterTarget target,
        ParameterIdentity parameter,
        string newValue,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default)
    {
        TestWriteFeatureGate.EnsureEnabled();
        ToolArgumentValidator.Validate(requestContext, PlanArgumentKeys, PlanArgumentKeys);
        TestWriteFeatureGate.EnsureDocumentAllowed(documentKey);

        var request = new PlanSetParameterValueRequest
        {
            Target = target,
            Parameter = parameter,
            After = new ParameterValue
            {
                Kind = ParameterValueKind.String,
                HasValue = true,
                IsReadOnly = false,
                StringValue = newValue
            }
        };
        ContractValidator.Validate(request);

        return BridgeToolInvoker.InvokeAsync(
            bridgeClient,
            revitProcessId,
            BridgeOperations.PlanSetParameterValues,
            request,
            sessionId,
            documentKey,
            cancellationToken,
            expectedRevision: expectedRevision,
            idempotencyKey: idempotencyKey);
    }

    [McpServerTool(Name = "revit_test_apply_change_plan", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("TEST ONLY: apply one previously returned change plan in the allowlisted disposable test RVT. approvedBy, approvedAtUtc, and approvalSource are client-supplied audit metadata only, not authorization; Revit must show and receive explicit confirmation for the exact plan. The Revit process also requires BIMASSIST_MCP_ENABLE_TEST_WRITES=1 and BIMASSIST_MCP_TEST_DOCUMENT_PATH set to the exact local disposable RVT. Never use on production models.")]
    public static Task<string> ApplyChangePlan(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        string planId,
        string planHash,
        string expectedRevision,
        string idempotencyKey,
        string approvedBy,
        DateTimeOffset approvedAtUtc,
        string approvalSource,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default)
    {
        TestWriteFeatureGate.EnsureEnabled();
        ToolArgumentValidator.Validate(requestContext, ApplyArgumentKeys, ApplyArgumentKeys);
        TestWriteFeatureGate.EnsureDocumentAllowed(documentKey);

        var request = new ApplyChangePlanRequest
        {
            PlanId = planId,
            PlanHash = planHash,
            ExpectedRevision = expectedRevision,
            IdempotencyKey = idempotencyKey,
            Approval = new ApprovalMetadata
            {
                ApprovedBy = approvedBy,
                ApprovedAtUtc = approvedAtUtc,
                Source = approvalSource
            }
        };
        ContractValidator.Validate(request);

        return BridgeToolInvoker.InvokeAsync(
            bridgeClient,
            revitProcessId,
            BridgeOperations.ApplyChangePlan,
            request,
            sessionId,
            documentKey,
            cancellationToken,
            expectedRevision: expectedRevision,
            idempotencyKey: idempotencyKey);
    }
}
