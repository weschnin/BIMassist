using BIMassist.Mcp.Server.Bridge;
using BIMassist.Mcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BIMassist.Mcp.Server;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<IBridgeClient, NamedPipeBridgeClient>();
        Type[] toolTypes = TestWriteFeatureGate.IsEnabled
            ? [typeof(RevitTools), typeof(RevitTestWriteTools)]
            : [typeof(RevitTools)];
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools((IEnumerable<Type>)toolTypes);

        await builder.Build().RunAsync().ConfigureAwait(false);
    }
}
