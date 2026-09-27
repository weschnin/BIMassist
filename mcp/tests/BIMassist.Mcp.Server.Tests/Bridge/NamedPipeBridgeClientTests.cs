using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Server.Bridge;
using BIMassist.Mcp.Server.Transport;

namespace BIMassist.Mcp.Server.Tests.Bridge;

public sealed class NamedPipeBridgeClientTests
{
    [Fact]
    public async Task Sends_one_request_and_returns_its_correlated_bridge_response()
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        int processId = Environment.ProcessId;
        string endpoint = BridgeEndpointName.Create(sid, processId);
        await using var server = new NamedPipeServerStream(
            endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Task<BridgeRequest> serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            BridgeRequest request = await BridgeFrameProtocol.ReadAsync<BridgeRequest>(server, CancellationToken.None);
            using JsonDocument result = JsonDocument.Parse("{\"accepted\":true}");
            await BridgeFrameProtocol.WriteAsync(server, new BridgeResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Result = result.RootElement.Clone(),
                Warnings = [],
                DurationMs = 1
            }, CancellationToken.None);
            return request;
        });

        var client = new NamedPipeBridgeClient();
        BridgeResponse response = await client.SendAsync(
            processId,
            BridgeOperations.GetStatus,
            new Dictionary<string, string>(),
            cancellationToken: CancellationToken.None);
        BridgeRequest sent = await serverTask;

        Assert.True(response.Success);
        Assert.Equal(sent.RequestId, response.RequestId);
        Assert.Equal(BridgeOperations.GetStatus, sent.Operation);
    }
}
