using BIMassist.Mcp.RevitBridge.Lifecycle;

namespace BIMassist.Mcp.RevitBridge.Dispatch;

public sealed class BridgeQueueRuntimeComponent : IBridgeRuntimeComponent
{
    private readonly BridgeRequestQueue _queue;
    private readonly DeferredExternalEventSignal _signal;

    public BridgeQueueRuntimeComponent(BridgeRequestQueue queue, DeferredExternalEventSignal signal)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    public Task StartAsync(CancellationToken lifetimeToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _signal.BeginShutdown();
        _queue.BeginShutdown();
        return Task.CompletedTask;
    }
}
