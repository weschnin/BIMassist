using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Dispatch;

namespace BIMassist.Mcp.RevitBridge.Tests.Dispatch;

public sealed class BridgeRequestQueueTests
{
    [Fact]
    public async Task Queue_executes_requests_in_fifo_order_and_coalesces_signal()
    {
        var signal = new RecordingSignal();
        var queue = new BridgeRequestQueue(signal);
        QueuedBridgeRequest first = queue.Enqueue(CreateRequest("request-1"));
        QueuedBridgeRequest second = queue.Enqueue(CreateRequest("request-2"));
        var executed = new List<string>();

        Assert.Equal(1, signal.RaiseCount);
        Assert.True(queue.ExecuteNext(request => Success(request.RequestId, executed)));
        Assert.True(queue.ExecuteNext(request => Success(request.RequestId, executed)));

        Assert.Equal(["request-1", "request-2"], executed);
        Assert.Equal("request-1", (await first.Completion).RequestId);
        Assert.Equal("request-2", (await second.Completion).RequestId);
        Assert.Equal(2, signal.RaiseCount);
        Assert.Equal(BridgeRequestExecutionState.Completed, first.State);
        Assert.Equal(BridgeRequestExecutionState.Completed, second.State);
    }

    [Fact]
    public async Task Cancellation_before_start_skips_the_request()
    {
        var signal = new RecordingSignal();
        var queue = new BridgeRequestQueue(signal);
        using var cancellation = new CancellationTokenSource();
        QueuedBridgeRequest queued = queue.Enqueue(CreateRequest("request-1"), cancellation.Token);
        cancellation.Cancel();

        Assert.False(queue.ExecuteNext(_ => throw new InvalidOperationException("Must not execute.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued.Completion);
        Assert.Equal(BridgeRequestExecutionState.CancelledBeforeStart, queued.State);
    }

    [Fact]
    public async Task Shutdown_rejects_pending_and_new_requests()
    {
        var queue = new BridgeRequestQueue(new RecordingSignal());
        QueuedBridgeRequest pending = queue.Enqueue(CreateRequest("request-1"));

        queue.BeginShutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.Completion);
        Assert.Equal(BridgeRequestExecutionState.CancelledBeforeStart, pending.State);
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(CreateRequest("request-2")));
    }

    [Fact]
    public async Task Queue_never_allows_two_active_processors()
    {
        var queue = new BridgeRequestQueue(new RecordingSignal());
        QueuedBridgeRequest first = queue.Enqueue(CreateRequest("request-1"));
        QueuedBridgeRequest second = queue.Enqueue(CreateRequest("request-2"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task<bool> running = Task.Run(() => queue.ExecuteNext(request =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return Success(request.RequestId, []);
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(queue.ExecuteNext(request => Success(request.RequestId, [])));
        release.Set();
        Assert.True(await running);
        Assert.True(queue.ExecuteNext(request => Success(request.RequestId, [])));
        await Task.WhenAll(first.Completion, second.Completion);
    }

    private static BridgeRequest CreateRequest(string requestId) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = requestId,
        Operation = BridgeOperations.GetStatus,
        Payload = JsonDocument.Parse("{}").RootElement.Clone()
    };

    private static BridgeResponse Success(string requestId, List<string> executed)
    {
        executed.Add(requestId);
        return new BridgeResponse
        {
            RequestId = requestId,
            Success = true,
            Result = JsonDocument.Parse("{}").RootElement.Clone(),
            Warnings = [],
            DurationMs = 0
        };
    }

    private sealed class RecordingSignal : IExternalEventSignal
    {
        public int RaiseCount { get; private set; }

        public void Raise() => RaiseCount++;
    }
}
