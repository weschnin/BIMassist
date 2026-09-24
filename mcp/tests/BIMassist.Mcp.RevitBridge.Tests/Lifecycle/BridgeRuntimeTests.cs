using BIMassist.Mcp.RevitBridge.Lifecycle;

namespace BIMassist.Mcp.RevitBridge.Tests.Lifecycle;

public sealed class BridgeRuntimeTests
{
    [Fact]
    public async Task Runtime_starts_in_order_and_stops_in_reverse_order()
    {
        var calls = new List<string>();
        var first = new RecordingComponent("first", calls);
        var second = new RecordingComponent("second", calls);
        await using var runtime = new BridgeRuntime([first, second]);

        await runtime.StartAsync();
        await runtime.StopAsync();

        Assert.Equal(
            ["start:first", "start:second", "stop:second", "stop:first"],
            calls);
        Assert.Equal(BridgeRuntimeState.Stopped, runtime.State);
    }

    [Fact]
    public async Task Runtime_cancels_lifetime_before_components_stop()
    {
        var component = new CancellationObservingComponent();
        await using var runtime = new BridgeRuntime([component]);

        await runtime.StartAsync();
        await runtime.StopAsync();

        Assert.True(component.LifetimeWasCancelledAtStop);
    }

    [Fact]
    public async Task Runtime_rejects_restart_after_shutdown()
    {
        await using var runtime = new BridgeRuntime([]);
        await runtime.StartAsync();
        await runtime.StopAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync());
    }

    private sealed class RecordingComponent(string name, List<string> calls) : IBridgeRuntimeComponent
    {
        public Task StartAsync(CancellationToken lifetimeToken)
        {
            calls.Add($"start:{name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            calls.Add($"stop:{name}");
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationObservingComponent : IBridgeRuntimeComponent
    {
        private CancellationToken _lifetimeToken;

        public bool LifetimeWasCancelledAtStop { get; private set; }

        public Task StartAsync(CancellationToken lifetimeToken)
        {
            _lifetimeToken = lifetimeToken;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            LifetimeWasCancelledAtStop = _lifetimeToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }
}
