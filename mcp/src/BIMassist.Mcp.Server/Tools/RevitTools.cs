using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Server.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BIMassist.Mcp.Server.Tools;

[McpServerToolType]
public static class RevitTools
{
    private static readonly string[] ReadArgumentKeys = ["revitProcessId", "sessionId", "documentKey", "request"];

    [McpServerTool(Name = "revit_get_status", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Find running Revit 2026 processes and return bridge status, session IDs, open documents, and revisions. If multiple Revit processes are open, query one by revitProcessId.")]
    public static async Task<string> GetStatus(
        IBridgeClient bridgeClient,
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Optional Revit process ID. Omit to query all running Revit processes.")] int? revitProcessId = null,
        CancellationToken cancellationToken = default)
    {
        ToolArgumentValidator.Validate(requestContext, ["revitProcessId"], []);
        int[] processIds = revitProcessId.HasValue
            ? [revitProcessId.Value]
            : Process.GetProcessesByName("Revit").Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            }).Order().ToArray();

        if (processIds.Length == 0)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = new { code = "NO_REVIT_PROCESS", message = "No Revit process is currently running." }
            });
        }

        var results = new List<object>(processIds.Length);
        foreach (int processId in processIds)
        {
            string response = await BridgeToolInvoker.InvokeAsync(
                bridgeClient,
                processId,
                BridgeOperations.GetStatus,
                new EmptyPayload(),
                sessionId: null,
                documentKey: null,
                cancellationToken).ConfigureAwait(false);
            using JsonDocument responseDocument = JsonDocument.Parse(response);
            results.Add(new { revitProcessId = processId, response = responseDocument.RootElement.Clone() });
        }

        return JsonSerializer.Serialize(new { processes = results });
    }

    [McpServerTool(Name = "revit_get_document_context", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Return the current document context for a Revit bridge session.")]
    public static Task<string> GetDocumentContext(
        IBridgeClient bridgeClient,
        [Description("Revit process ID returned by revit_get_status.")] int revitProcessId,
        [Description("Session ID returned by revit_get_status.")] string sessionId,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default)
    {
        ToolArgumentValidator.Validate(requestContext, ["revitProcessId", "sessionId"], ["revitProcessId", "sessionId"]);
        return BridgeToolInvoker.InvokeAsync(
            bridgeClient,
            revitProcessId,
            BridgeOperations.GetDocumentContext,
            new EmptyPayload(),
            sessionId,
            documentKey: null,
            cancellationToken);
    }

    [McpServerTool(Name = "revit_list_families", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List loaded Revit families using deterministic, revision-bound pagination.")]
    public static Task<string> ListFamilies(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        ListFamiliesRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.ListFamilies, request, requestContext, cancellationToken);

    [McpServerTool(Name = "revit_get_family_metadata", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read family metadata and paginated family types by stable family identity.")]
    public static Task<string> GetFamilyMetadata(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        GetFamilyMetadataRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.GetFamilyMetadata, request, requestContext, cancellationToken);

    [McpServerTool(Name = "revit_list_parameters", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List parameters for a stable target identity with filters and revision-bound pagination.")]
    public static Task<string> ListParameters(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        ListParametersRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.ListParameters, request, requestContext, cancellationToken);

    [McpServerTool(Name = "revit_get_parameter_metadata", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read metadata for a parameter identified by stable parameter identity on a specific target.")]
    public static Task<string> GetParameterMetadata(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        GetParameterMetadataRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.GetParameterMetadata, request, requestContext, cancellationToken);

    [McpServerTool(Name = "revit_list_shared_definitions", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List shared parameter definitions from the configured Revit shared-parameter file.")]
    public static Task<string> ListSharedDefinitions(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        ListSharedDefinitionsRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.ListSharedDefinitions, request, requestContext, cancellationToken);

    [McpServerTool(Name = "revit_list_project_bindings", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("List project parameter bindings and their actual categories and binding kinds.")]
    public static Task<string> ListProjectBindings(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        ListProjectBindingsRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.ListProjectBindings, request, requestContext, cancellationToken);

    [McpServerTool(Name = "revit_get_parameter_values", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Read typed parameter values, preserving storage type, internal numeric values, units, and empty-value state.")]
    public static Task<string> GetParameterValues(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        GetParameterValuesRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.GetParameterValues, request, requestContext, cancellationToken);

    [McpServerTool(Name = "revit_export_metadata_snapshot", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Export a bounded metadata snapshot with immutable pages and authenticated continuation cursors.")]
    public static Task<string> ExportMetadataSnapshot(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        ExportMetadataSnapshotRequest request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken = default) =>
        InvokeReadAsync(bridgeClient, revitProcessId, sessionId, documentKey, BridgeOperations.ExportMetadataSnapshot, request, requestContext, cancellationToken);

    private static Task<string> InvokeReadAsync(
        IBridgeClient bridgeClient,
        int revitProcessId,
        string sessionId,
        string documentKey,
        string operation,
        object request,
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken)
    {
        ToolArgumentValidator.Validate(requestContext, ReadArgumentKeys, ReadArgumentKeys);
        return BridgeToolInvoker.InvokeAsync(
            bridgeClient,
            revitProcessId,
            operation,
            request,
            sessionId,
            documentKey,
            cancellationToken);
    }
}
