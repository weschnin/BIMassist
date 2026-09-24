using Autodesk.Revit.UI;

namespace BIMassist.Mcp.RevitBridge.Dispatch;

public sealed class RevitExternalEventRaiser : IExternalEventRaiser, IDisposable
{
    private readonly ExternalEvent _externalEvent;
    private int _disposed;

    public RevitExternalEventRaiser(ExternalEvent externalEvent)
    {
        _externalEvent = externalEvent ?? throw new ArgumentNullException(nameof(externalEvent));
    }

    public void Raise()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _externalEvent.Raise();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _externalEvent.Dispose();
        }
    }
}
