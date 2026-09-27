using System.IO.Pipes;
using System.Security.Principal;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Server.Transport;

namespace BIMassist.Mcp.Server.Bridge;

public sealed class NamedPipeBridgeClient : IBridgeClient
{
    private const int ConnectTimeoutMilliseconds = 3_000;
    private readonly string _userSid;

    public NamedPipeBridgeClient()
    {
        _userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }

    public async Task<BridgeResponse> SendAsync(
        int revitProcessId,
        string operation,
        object payload,
        string? sessionId = null,
        string? documentKey = null,
        string? expectedRevision = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        string endpoint = BridgeEndpointName.Create(_userSid, revitProcessId);
        BridgeRequest request = BridgeRequestFactory.Create(
            operation,
            payload,
            sessionId: sessionId,
            documentKey: documentKey,
            expectedRevision: expectedRevision,
            idempotencyKey: idempotencyKey);

        await using var pipe = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        await BridgeFrameProtocol.WriteAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        BridgeResponse response = await BridgeFrameProtocol
            .ReadAsync<BridgeResponse>(pipe, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The bridge response request ID does not match the request.");
        }

        return response;
    }
}
