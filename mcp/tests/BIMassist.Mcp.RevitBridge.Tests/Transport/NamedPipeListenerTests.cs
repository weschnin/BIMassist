using System.IO.Pipes;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Dispatch;
using BIMassist.Mcp.RevitBridge.Transport;

namespace BIMassist.Mcp.RevitBridge.Tests.Transport;

public sealed class NamedPipeListenerTests
{
    [Fact]
    public async Task Expired_apply_wait_closes_only_its_client_and_listener_accepts_another()
    {
        string endpoint = $"bimassist.test.{Guid.NewGuid():N}";
        BridgeRequestQueue? queue = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new CallbackSignal(() => _ = Task.Run(() => queue!.ExecuteNextWithContext(item =>
        {
            if (item.Request.Operation == BridgeOperations.ApplyChangePlan)
            {
                entered.TrySetResult();
                item.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
            }
            return Success(item.Request.RequestId);
        })));
        queue = new BridgeRequestQueue(signal);
        await using var listener = new NamedPipeListener(endpoint, queue, TimeSpan.FromMilliseconds(150));
        await listener.StartAsync(CancellationToken.None);
        await using (var first = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await first.ConnectAsync(5000);
            await LengthPrefixedJsonProtocol.WriteAsync(first, CreateApplyRequest(), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                LengthPrefixedJsonProtocol.ReadAsync<BridgeResponse>(first, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await using var second = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        await second.ConnectAsync(5000);
        await LengthPrefixedJsonProtocol.WriteAsync(second, CreateRequest(), CancellationToken.None);
        BridgeResponse result = await LengthPrefixedJsonProtocol.ReadAsync<BridgeResponse>(second, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal(NamedPipeListenerState.Running, listener.State);
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disconnected_client_cancels_running_request_before_it_can_write()
    {
        string endpoint = $"bimassist.test.{Guid.NewGuid():N}";
        BridgeRequestQueue? queue = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new CallbackSignal(() => _ = Task.Run(() => queue!.ExecuteNextWithContext(item =>
        {
            entered.TrySetResult();
            if (item.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                abandoned.TrySetResult();
            return Success(item.Request.RequestId);
        })));
        queue = new BridgeRequestQueue(signal);
        await using var listener = new NamedPipeListener(endpoint, queue);
        await listener.StartAsync(CancellationToken.None);
        var client = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await LengthPrefixedJsonProtocol.WriteAsync(client, CreateRequest(), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await listener.StopAsync(CancellationToken.None);
    }

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

    private static BridgeRequest CreateApplyRequest() => CreateRequest() with
    {
        Operation = BridgeOperations.ApplyChangePlan,
        SessionId = "session-1", DocumentKey = "doc-1", ExpectedRevision = "session-1:0",
        IdempotencyKey = "apply-key-1",
        Payload = JsonSerializer.SerializeToElement(new ApplyChangePlanRequest
        {
            PlanId = "plan-1", PlanHash = new string('a', 64), ExpectedRevision = "session-1:0",
            IdempotencyKey = "apply-key-1", Approval = new ApprovalMetadata
            {
                ApprovedBy = "claim", Source = "client", ApprovedAtUtc = DateTimeOffset.UtcNow
            }
        }, ContractJson.Options)
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
