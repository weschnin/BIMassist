using System.Collections.Concurrent;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Dispatch;

public interface IExternalEventSignal
{
    void Raise();
}

public sealed class BridgeRequestQueue
{
    private readonly ConcurrentQueue<QueuedBridgeRequest> _queue = new();
    private readonly IExternalEventSignal _signal;
    private int _active;
    private int _shuttingDown;
    private int _signalOutstanding;

    public BridgeRequestQueue(IExternalEventSignal signal)
    {
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    public QueuedBridgeRequest Enqueue(BridgeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        if (Volatile.Read(ref _shuttingDown) != 0)
        {
            throw new InvalidOperationException("The bridge request queue is shutting down.");
        }

        var queued = new QueuedBridgeRequest(request, cancellationToken);
        _queue.Enqueue(queued);
        if (Volatile.Read(ref _shuttingDown) != 0)
        {
            BeginShutdown();
            throw new InvalidOperationException("The bridge request queue is shutting down.");
        }

        SignalIfNeeded();
        return queued;
    }

    public bool ExecuteNext(Func<BridgeRequest, BridgeResponse> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        if (Volatile.Read(ref _shuttingDown) != 0 ||
            Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            return false;
        }

        Interlocked.Exchange(ref _signalOutstanding, 0);
        try
        {
            while (_queue.TryDequeue(out QueuedBridgeRequest? queued))
            {
                if (!queued.TryStart())
                {
                    queued.Dispose();
                    continue;
                }

                try
                {
                    BridgeResponse response = processor(queued.Request);
                    ContractValidator.Validate(response);
                    queued.Complete(response);
                }
                catch (Exception exception)
                {
                    queued.Fail(exception);
                }
                finally
                {
                    queued.Dispose();
                }

                return true;
            }

            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
            if (!_queue.IsEmpty && Volatile.Read(ref _shuttingDown) == 0)
            {
                SignalIfNeeded();
            }
        }
    }

    public void BeginShutdown()
    {
        Interlocked.Exchange(ref _shuttingDown, 1);
        Interlocked.Exchange(ref _signalOutstanding, 0);
        while (_queue.TryDequeue(out QueuedBridgeRequest? queued))
        {
            queued.TryCancelBeforeStart();
            queued.Dispose();
        }
    }

    private void SignalIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _signalOutstanding, 1, 0) == 0)
        {
            _signal.Raise();
        }
    }
}
