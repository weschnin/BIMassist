using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Dispatch;

public enum BridgeRequestExecutionState
{
    Queued,
    Running,
    Completed,
    CancelledBeforeStart,
    OutcomeUnknown
}

public sealed class QueuedBridgeRequest : IDisposable
{
    private readonly TaskCompletionSource<BridgeResponse> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _cancellationRegistration;
    private int _state = (int)BridgeRequestExecutionState.Queued;
    private int _disposed;

    internal QueuedBridgeRequest(BridgeRequest request, CancellationToken cancellationToken)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(
                static state => ((QueuedBridgeRequest)state!).TryCancelBeforeStart(),
                this);
        }
    }

    public BridgeRequest Request { get; }

    public Task<BridgeResponse> Completion => _completion.Task;

    public BridgeRequestExecutionState State => (BridgeRequestExecutionState)Volatile.Read(ref _state);

    internal bool TryStart()
    {
        bool started = Interlocked.CompareExchange(
            ref _state,
            (int)BridgeRequestExecutionState.Running,
            (int)BridgeRequestExecutionState.Queued) == (int)BridgeRequestExecutionState.Queued;
        if (started)
        {
            _cancellationRegistration.Dispose();
        }

        return started;
    }

    internal void Complete(BridgeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (State == BridgeRequestExecutionState.Running)
        {
            Interlocked.Exchange(ref _state, (int)BridgeRequestExecutionState.Completed);
        }

        _completion.TrySetResult(response);
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (State == BridgeRequestExecutionState.Running)
        {
            Interlocked.Exchange(ref _state, (int)BridgeRequestExecutionState.Completed);
        }

        _completion.TrySetException(exception);
    }

    public bool MarkOutcomeUnknown() => Interlocked.CompareExchange(
        ref _state,
        (int)BridgeRequestExecutionState.OutcomeUnknown,
        (int)BridgeRequestExecutionState.Running) == (int)BridgeRequestExecutionState.Running;

    internal bool TryCancelBeforeStart()
    {
        bool cancelled = Interlocked.CompareExchange(
            ref _state,
            (int)BridgeRequestExecutionState.CancelledBeforeStart,
            (int)BridgeRequestExecutionState.Queued) == (int)BridgeRequestExecutionState.Queued;
        if (cancelled)
        {
            _completion.TrySetCanceled(new CancellationToken(canceled: true));
        }

        return cancelled;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cancellationRegistration.Dispose();
        }
    }
}
