using System.IO.Pipes;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Dispatch;
using BIMassist.Mcp.RevitBridge.Transport;

namespace BIMassist.Mcp.RevitBridge.Tests.Transport;

public sealed class NamedPipeListenerTests
{
    [Fact]
    public async Task Listener_roundtrips_request_through_queue_without_processing_on_pipe_thread()
    {
        string endpoint = $"bimassist.test.{Guid.NewGuid():N}";
        BridgeRequestQueue? queue = null;
        var signal = new CallbackSignal(() => _ = Task.Run(() => queue!.ExecuteNext(request =>
        {
            return Success(request.RequestId);
        })));
        queue = new BridgeRequestQueue(signal);
        await using var listener = new NamedPipeListener(endpoint, queue);
        await listener.StartAsync(CancellationToken.None);
        await using var client = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        await LengthPrefixedJsonProtocol.WriteAsync(client, CreateRequest(), CancellationToken.None);
        BridgeResponse response = await LengthPrefixedJsonProtocol.ReadAsync<BridgeResponse>(client, CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal("request-1", response.RequestId);
        Assert.Equal(1, signal.RaiseCount);
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_returns_structured_internal_error_and_keeps_connection_alive()
    {
        string endpoint = $"bimassist.test.{Guid.NewGuid():N}";
        BridgeRequestQueue? queue = null;
        int calls = 0;
        var signal = new CallbackSignal(() => _ = Task.Run(() => queue!.ExecuteNext(request =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("sensitive implementation detail");
            }

            return Success(request.RequestId);
        })));
        queue = new BridgeRequestQueue(signal);
        await using var listener = new NamedPipeListener(endpoint, queue);
        await listener.StartAsync(CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        await LengthPrefixedJsonProtocol.WriteAsync(client, CreateRequest(), CancellationToken.None);
        BridgeResponse failed = await LengthPrefixedJsonProtocol.ReadAsync<BridgeResponse>(client, CancellationToken.None);
        await LengthPrefixedJsonProtocol.WriteAsync(
            client,
            CreateRequest() with { RequestId = "request-2" },
            CancellationToken.None);
        BridgeResponse succeeded = await LengthPrefixedJsonProtocol.ReadAsync<BridgeResponse>(client, CancellationToken.None);

        Assert.False(failed.Success);
        Assert.Equal(BridgeErrorCodes.BridgeInternalError, failed.Error?.Code);
        Assert.DoesNotContain("sensitive", failed.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(succeeded.Success);
        Assert.Equal("request-2", succeeded.RequestId);
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_stop_closes_idle_accept_and_rejects_future_connections()
    {
        string endpoint = $"bimassist.test.{Guid.NewGuid():N}";
        var queue = new BridgeRequestQueue(new CallbackSignal(() => { }));
        await using var listener = new NamedPipeListener(endpoint, queue);
        await listener.StartAsync(CancellationToken.None);

        await listener.StopAsync(CancellationToken.None);

        Assert.Equal(NamedPipeListenerState.Stopped, listener.State);
        await using var client = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(150));
    }

    private static BridgeRequest CreateRequest() => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-1",
        Operation = BridgeOperations.GetStatus,
        Payload = JsonDocument.Parse("{}").RootElement.Clone()
    };

    private static BridgeResponse Success(string requestId) => new()
    {
        RequestId = requestId,
        Success = true,
        Result = JsonDocument.Parse("{}").RootElement.Clone(),
        Warnings = [],
        DurationMs = 0
    };

    private sealed class CallbackSignal(Action callback) : IExternalEventSignal
    {
        public int RaiseCount { get; private set; }

        public void Raise()
        {
            RaiseCount++;
            callback();
        }
    }
}
