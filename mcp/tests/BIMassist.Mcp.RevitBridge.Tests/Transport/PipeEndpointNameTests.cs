using System.IO.Pipes;
using BIMassist.Mcp.RevitBridge.Transport;

namespace BIMassist.Mcp.RevitBridge.Tests.Transport;

public sealed class PipeEndpointNameTests
{
    [Fact]
    public void Endpoint_is_deterministic_and_bound_to_sid_process_and_protocol()
    {
        string endpoint = PipeEndpointName.Create("S-1-5-21-100-200-300-400", 1234, "1");

        Assert.Equal("bimassist.mcp.revit.s-1-5-21-100-200-300-400.1234.v1", endpoint);
    }

    [Theory]
    [InlineData("", 1234, "1")]
    [InlineData("user-name", 1234, "1")]
    [InlineData("S-1-5-21-100", 0, "1")]
    [InlineData("S-1-5-21-100", -1, "1")]
    [InlineData("S-1-5-21-100", 1234, "v1")]
    public void Endpoint_rejects_invalid_identity_or_version(string sid, int processId, string protocolVersion)
    {
        Assert.Throws<ArgumentException>(() => PipeEndpointName.Create(sid, processId, protocolVersion));
    }

    [Fact]
    public void Server_options_restrict_pipe_to_current_user_and_local_async_transport()
    {
        PipeServerConfiguration configuration = CurrentUserPipeSecurity.CreateConfiguration("safe.pipe");

        Assert.Equal("safe.pipe", configuration.Name);
        Assert.True(configuration.Options.HasFlag(PipeOptions.CurrentUserOnly));
        Assert.True(configuration.Options.HasFlag(PipeOptions.Asynchronous));
        Assert.Equal(PipeDirection.InOut, configuration.Direction);
        Assert.Equal(1, configuration.MaximumServerInstances);
        Assert.True(configuration.RejectRemoteClients);
    }

    [Fact]
    public async Task Server_rejects_hostname_connections_before_accepting_a_local_client()
    {
        string endpoint = $"bimassist.remote-test.{Guid.NewGuid():N}";
        await using NamedPipeServerStream server = CurrentUserPipeSecurity.CreateServer(endpoint);
        using var acceptCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task accepting = server.WaitForConnectionAsync(acceptCancellation.Token);
        await using var remoteStyleClient = new NamedPipeClientStream(
            Environment.MachineName,
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await Assert.ThrowsAnyAsync<Exception>(() => remoteStyleClient.ConnectAsync(250));
        Assert.False(server.IsConnected);

        await using var localClient = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await localClient.ConnectAsync(1000);
        await accepting;
        Assert.True(server.IsConnected);
    }
}
