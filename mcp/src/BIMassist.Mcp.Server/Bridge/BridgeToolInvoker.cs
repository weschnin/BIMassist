using System.IO;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Server.Bridge;

internal static class BridgeToolInvoker
{
    public static async Task<string> InvokeAsync(
        IBridgeClient client,
        int revitProcessId,
        string operation,
        object payload,
        string? sessionId,
        string? documentKey,
        CancellationToken cancellationToken)
    {
        try
        {
            BridgeResponse response = await client.SendAsync(
                revitProcessId,
                operation,
                payload,
                sessionId,
                documentKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return ContractJson.Serialize(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = new
                {
                    code = "BRIDGE_UNAVAILABLE",
                    message = "Could not connect to the selected Revit bridge. Confirm that the MCP add-on is loaded and Revit is still running."
                }
            });
        }
    }
}
