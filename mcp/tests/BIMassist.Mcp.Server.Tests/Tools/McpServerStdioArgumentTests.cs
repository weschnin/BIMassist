using System.Diagnostics;
using System.Text.Json;
using BIMassist.Mcp.Server.Tools;

namespace BIMassist.Mcp.Server.Tests.Tools;

public sealed class McpServerStdioArgumentTests
{
    [Fact]
    public async Task Rejects_unknown_arguments_before_attempting_bridge_connection()
    {
        string serverAssembly = Path.Combine(AppContext.BaseDirectory, "BIMassist.Mcp.Server.dll");
        Assert.True(File.Exists(serverAssembly), $"MCP server assembly not found: {serverAssembly}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(serverAssembly);
        Assert.True(process.Start(), "Could not start the MCP STDIO server.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { },
                    clientInfo = new { name = "server-argument-tests", version = "1" }
                }
            }, timeout.Token);
            using JsonDocument initialized = await ReadResponseAsync(process, 1, timeout.Token);
            Assert.True(initialized.RootElement.TryGetProperty("result", out _));

            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" }, timeout.Token);
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, timeout.Token);
            using JsonDocument toolList = await ReadResponseAsync(process, 2, timeout.Token);
            JsonElement tools = toolList.RootElement.GetProperty("result").GetProperty("tools");
            Assert.Equal(11, tools.GetArrayLength());
            Assert.Contains(
                tools.EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == "revit_list_elements");
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                string schema = tool.GetProperty("inputSchema").GetRawText();
                Assert.DoesNotContain("requestContext", schema, StringComparison.Ordinal);
            }

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "revit_get_document_context",
                    arguments = new
                    {
                        revitProcessId = int.MaxValue,
                        sessionId = "not-a-real-session",
                        unexpected = true
                    }
                }
            }, timeout.Token);
            using JsonDocument unknownRoot = await ReadResponseAsync(process, 3, timeout.Token);
            AssertRejectedBeforeBridge(unknownRoot.RootElement.GetProperty("result"));

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    name = "revit_list_families",
                    arguments = new
                    {
                        revitProcessId = int.MaxValue,
                        sessionId = "not-a-real-session",
                        documentKey = "not-a-real-document",
                        request = new
                        {
                            page = new { pageSize = 1, unexpected = true },
                            includeInPlace = false
                        }
                    }
                }
            }, timeout.Token);
            using JsonDocument unknownNested = await ReadResponseAsync(process, 4, timeout.Token);
            AssertRejectedBeforeBridge(unknownNested.RootElement.GetProperty("result"));

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new
                {
                    name = "revit_get_document_context",
                    arguments = new
                    {
                        revitProcessId = int.MaxValue,
                        sessionId = "not-a-real-session"
                    }
                }
            }, timeout.Token);
            using JsonDocument unavailableBridge = await ReadResponseAsync(process, 5, timeout.Token);
            JsonElement bridgeResult = unavailableBridge.RootElement.GetProperty("result");
            Assert.False(bridgeResult.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(), bridgeResult.GetRawText());
            Assert.Contains("BRIDGE_UNAVAILABLE", bridgeResult.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }

        Assert.Equal(0, process.ExitCode);
    }

    private static void AssertRejectedBeforeBridge(JsonElement result)
    {
        Assert.True(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        string resultText = result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        Assert.DoesNotContain("BRIDGE_UNAVAILABLE", resultText, StringComparison.Ordinal);
    }

    private static async Task SendAsync(Process process, object message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, int expectedId, CancellationToken cancellationToken)
    {
        string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(line), "MCP server closed STDOUT before replying.");
        JsonDocument response = JsonDocument.Parse(line!);
        Assert.Equal(expectedId, response.RootElement.GetProperty("id").GetInt32());
        return response;
    }
}
