namespace BIMassist.Mcp.RevitBridge.Dispatch;

public interface IExternalEventRaiser
{
    void Raise();
}

public sealed class DeferredExternalEventSignal : IExternalEventSignal
{
    private IExternalEventRaiser? _raiser;
    private int _shuttingDown;

    public void Bind(IExternalEventRaiser raiser)
    {
        ArgumentNullException.ThrowIfNull(raiser);
        if (Interlocked.CompareExchange(ref _raiser, raiser, null) is not null)
        {
            throw new InvalidOperationException("The bridge ExternalEvent signal is already bound.");
        }
    }

    public void Raise()
    {
        if (Volatile.Read(ref _shuttingDown) != 0)
        {
            throw new InvalidOperationException("The bridge ExternalEvent signal is shutting down.");
        }

        IExternalEventRaiser? raiser = Volatile.Read(ref _raiser);
        if (raiser is null)
        {
            throw new InvalidOperationException("The bridge ExternalEvent signal has not been bound.");
        }

        raiser.Raise();
    }

    public void BeginShutdown() => Interlocked.Exchange(ref _shuttingDown, 1);
}
