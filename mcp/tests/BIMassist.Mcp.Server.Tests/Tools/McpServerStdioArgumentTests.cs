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
        process.StartInfo.Environment["BIMASSIST_MCP_ENABLE_TEST_WRITES"] = "0";
        process.StartInfo.Environment["BIMASSIST_MCP_TEST_DOCUMENT_KEY"] = "not-for-this-run";
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
            Assert.Equal(12, tools.GetArrayLength());
            Assert.Contains(
                tools.EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == "revit_list_elements");
            JsonElement searchTool = tools.EnumerateArray().Single(tool =>
                tool.GetProperty("name").GetString() == "revit_search_document_parameters");
            Assert.Contains("nameContains", searchTool.GetProperty("inputSchema").GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                tools.EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == "revit_test_plan_set_parameter_string");
            Assert.DoesNotContain(
                tools.EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == "revit_test_apply_change_plan");
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

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 6,
                method = "tools/call",
                @params = new
                {
                    name = "revit_search_document_parameters",
                    arguments = new
                    {
                        revitProcessId = int.MaxValue,
                        sessionId = "not-a-real-session",
                        documentKey = "not-a-real-document",
                        request = new
                        {
                            nameContains = "Mark",
                            includeTypes = false,
                            page = new { pageSize = 1 },
                            unexpected = true
                        }
                    }
                }
            }, timeout.Token);
            using JsonDocument unknownSearch = await ReadResponseAsync(process, 6, timeout.Token);
            AssertRejectedBeforeBridge(unknownSearch.RootElement.GetProperty("result"));
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

    [Fact]
    public async Task Test_write_tools_require_explicit_opt_in_and_allowlisted_document()
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
        process.StartInfo.Environment["BIMASSIST_MCP_ENABLE_TEST_WRITES"] = "1";
        process.StartInfo.Environment["BIMASSIST_MCP_TEST_DOCUMENT_KEY"] = "test-doc-only";
        process.StartInfo.ArgumentList.Add(serverAssembly);
        Assert.True(process.Start(), "Could not start the MCP STDIO server.");
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();

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
                    clientInfo = new { name = "test-write-opt-in", version = "1" }
                }
            }, timeout.Token);
            using JsonDocument initialized = await ReadResponseAsync(process, 1, timeout.Token);
            Assert.True(initialized.RootElement.TryGetProperty("result", out _));
            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" }, timeout.Token);
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, timeout.Token);
            using JsonDocument toolList = await ReadResponseAsync(process, 2, timeout.Token);
            Assert.True(toolList.RootElement.TryGetProperty("result", out JsonElement toolListResult), toolList.RootElement.GetRawText());
            JsonElement tools = toolListResult.GetProperty("tools");
            string[] toolNames = tools.EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
                .ToArray();
            Assert.True(toolNames.Length == 14, $"Expected 14 tools, got {toolNames.Length}: {string.Join(", ", toolNames)}");
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                Assert.DoesNotContain("requestContext", tool.GetProperty("inputSchema").GetRawText(), StringComparison.Ordinal);
            }
            JsonElement planTool = tools.EnumerateArray().Single(tool =>
                tool.GetProperty("name").GetString() == "revit_test_plan_set_parameter_string");
            string planSchema = planTool.GetProperty("inputSchema").GetRawText();
            Assert.Contains("idempotencyKey", planSchema, StringComparison.Ordinal);
            JsonElement applyTool = tools.EnumerateArray().Single(tool =>
                tool.GetProperty("name").GetString() == "revit_test_apply_change_plan");
            string applySchema = applyTool.GetProperty("inputSchema").GetRawText();
            Assert.Contains("idempotencyKey", applySchema, StringComparison.Ordinal);
            Assert.Contains("approvedBy", applySchema, StringComparison.Ordinal);
            Assert.Contains("approvedAtUtc", applySchema, StringComparison.Ordinal);
            Assert.Contains("approvalSource", applySchema, StringComparison.Ordinal);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "revit_test_plan_set_parameter_string",
                    arguments = new
                    {
                        revitProcessId = int.MaxValue,
                        sessionId = "not-a-real-session",
                        documentKey = "other-document",
                        expectedRevision = "revision-1",
                        idempotencyKey = "disallowed-plan-key",
                        target = new { kind = "element", uniqueId = "uid", elementId = 1 },
                        parameter = new { kind = "sharedGuid", sharedGuid = "5d4b3cf0-8ecb-4821-9470-3da7dbb143a0", stableId = "guid:5d4b3cf0-8ecb-4821-9470-3da7dbb143a0", name = "Test" },
                        newValue = "test"
                    }
                }
            }, timeout.Token);
            using JsonDocument disallowedDocument = await ReadResponseAsync(process, 3, timeout.Token);
            AssertRejectedBeforeBridge(disallowedDocument.RootElement.GetProperty("result"));

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new
                {
                    name = "revit_test_plan_set_parameter_string",
                    arguments = new
                    {
                        revitProcessId = int.MaxValue,
                        sessionId = "not-a-real-session",
                        documentKey = "test-doc-only",
                        expectedRevision = "revision-1",
                        idempotencyKey = "allowed-plan-key",
                        target = new { kind = "Element", uniqueId = "uid", elementId = 1 },
                        parameter = new { kind = "SharedGuid", sharedGuid = "5d4b3cf0-8ecb-4821-9470-3da7dbb143a0", stableId = "guid:5d4b3cf0-8ecb-4821-9470-3da7dbb143a0", name = "Test" },
                        newValue = "test"
                    }
                }
            }, timeout.Token);
            using JsonDocument validPlanRequest = await ReadResponseAsync(process, 4, timeout.Token);
            JsonElement bridgeUnavailable = validPlanRequest.RootElement.GetProperty("result");
            Assert.False(
                bridgeUnavailable.TryGetProperty("isError", out JsonElement bridgeIsError) && bridgeIsError.GetBoolean(),
                bridgeUnavailable.GetRawText());
            Assert.Contains("BRIDGE_UNAVAILABLE", bridgeUnavailable.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new
                {
                    name = "revit_test_plan_set_parameter_string",
                    arguments = new
                    {
                        revitProcessId = int.MaxValue,
                        sessionId = "not-a-real-session",
                        documentKey = "test-doc-only",
                        expectedRevision = "revision-1",
                        idempotencyKey = "typed-value-plan-key",
                        target = new { kind = "Element", uniqueId = "uid", elementId = 1 },
                        parameter = new { kind = "SharedGuid", sharedGuid = "5d4b3cf0-8ecb-4821-9470-3da7dbb143a0", stableId = "guid:5d4b3cf0-8ecb-4821-9470-3da7dbb143a0", name = "Test" },
                        newValue = 42
                    }
                }
            }, timeout.Token);
            using JsonDocument invalidValue = await ReadResponseAsync(process, 5, timeout.Token);
            AssertRejectedBeforeBridge(invalidValue.RootElement.GetProperty("result"));
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
        string standardError = await standardErrorTask;
        Assert.Contains("allowlisted test document", standardError, StringComparison.Ordinal);
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
