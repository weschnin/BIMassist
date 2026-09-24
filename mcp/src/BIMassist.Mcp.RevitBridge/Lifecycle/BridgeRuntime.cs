namespace BIMassist.Mcp.RevitBridge.Lifecycle;

public interface IBridgeRuntimeComponent
{
    Task StartAsync(CancellationToken lifetimeToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public enum BridgeRuntimeState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}

public sealed class BridgeRuntime : IAsyncDisposable
{
    private readonly IReadOnlyList<IBridgeRuntimeComponent> _components;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _startedCount;
    private bool _disposed;

    public BridgeRuntime(IReadOnlyList<IBridgeRuntimeComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Any(static component => component is null))
        {
            throw new ArgumentException("Runtime components cannot contain null values.", nameof(components));
        }

        _components = components;
    }

    public BridgeRuntimeState State { get; private set; } = BridgeRuntimeState.Created;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != BridgeRuntimeState.Created)
            {
                throw new InvalidOperationException($"Bridge runtime cannot start from state {State}.");
            }

            State = BridgeRuntimeState.Starting;
            try
            {
                for (int index = 0; index < _components.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _components[index].StartAsync(_lifetime.Token).ConfigureAwait(false);
                    _startedCount++;
                }

                State = BridgeRuntimeState.Running;
            }
            catch
            {
                State = BridgeRuntimeState.Faulted;
                _lifetime.Cancel();
                await StopStartedComponentsAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == BridgeRuntimeState.Stopped)
            {
                return;
            }

            if (State == BridgeRuntimeState.Created)
            {
                _lifetime.Cancel();
                State = BridgeRuntimeState.Stopped;
                return;
            }

            State = BridgeRuntimeState.Stopping;
            _lifetime.Cancel();
            await StopStartedComponentsAsync(cancellationToken).ConfigureAwait(false);
            State = BridgeRuntimeState.Stopped;
        }
        catch
        {
            State = BridgeRuntimeState.Faulted;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (State != BridgeRuntimeState.Stopped)
        {
            await StopAsync().ConfigureAwait(false);
        }

        _disposed = true;
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private async Task StopStartedComponentsAsync(CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        for (int index = _startedCount - 1; index >= 0; index--)
        {
            try
            {
                await _components[index].StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures ??= [];
                failures.Add(exception);
            }
        }

        _startedCount = 0;
        if (failures is not null)
        {
            throw new AggregateException("One or more bridge components failed to stop.", failures);
        }
    }
}
