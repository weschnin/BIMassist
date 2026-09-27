using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Server.Bridge;

public interface IBridgeClient
{
    Task<BridgeResponse> SendAsync(
        int revitProcessId,
        string operation,
        object payload,
        string? sessionId = null,
        string? documentKey = null,
        string? expectedRevision = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}
