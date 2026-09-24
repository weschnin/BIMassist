using BIMassist.Mcp.RevitBridge.Dispatch;

namespace BIMassist.Mcp.RevitBridge.Tests.Dispatch;

public sealed class DeferredExternalEventSignalTests
{
    [Fact]
    public void Signal_binds_exactly_once_and_forwards_raise()
    {
        var signal = new DeferredExternalEventSignal();
        var raiser = new RecordingRaiser();

        signal.Bind(raiser);
        signal.Raise();

        Assert.Equal(1, raiser.RaiseCount);
        Assert.Throws<InvalidOperationException>(() => signal.Bind(new RecordingRaiser()));
    }

    [Fact]
    public void Signal_rejects_raise_before_binding_and_after_shutdown()
    {
        var signal = new DeferredExternalEventSignal();
        Assert.Throws<InvalidOperationException>(() => signal.Raise());
        signal.Bind(new RecordingRaiser());

        signal.BeginShutdown();

        Assert.Throws<InvalidOperationException>(() => signal.Raise());
    }

    private sealed class RecordingRaiser : IExternalEventRaiser
    {
        public int RaiseCount { get; private set; }

        public void Raise() => RaiseCount++;
    }
}
