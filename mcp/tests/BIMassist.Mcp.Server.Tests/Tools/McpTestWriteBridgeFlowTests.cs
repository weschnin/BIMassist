using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Server.Transport;

namespace BIMassist.Mcp.Server.Tests.Tools;

public sealed class McpTestWriteBridgeFlowTests
{
    [Fact]
    public async Task Plan_and_apply_forward_parameter_value_and_safety_metadata_to_bridge()
    {
        string serverAssembly = Path.Combine(AppContext.BaseDirectory, "BIMassist.Mcp.Server.dll");
        Assert.True(File.Exists(serverAssembly), $"MCP server assembly not found: {serverAssembly}");

        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        int fakeRevitProcessId = Environment.ProcessId + 12345;
        string endpoint = BridgeEndpointName.Create(userSid, fakeRevitProcessId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<BridgeRequest[]> capturedRequests = CaptureRequestsAsync(endpoint, 2, timeout.Token);

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
                    clientInfo = new { name = "parameter-write-flow-test", version = "1" }
                }
            }, timeout.Token);
            using JsonDocument initialized = await ReadResponseAsync(process, 1, timeout.Token);
            Assert.True(initialized.RootElement.TryGetProperty("result", out _));
            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" }, timeout.Token);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "revit_test_apply_change_plan",
                    arguments = new
                    {
                        revitProcessId = fakeRevitProcessId,
                        sessionId = "session-1",
                        documentKey = "other-document",
                        planId = "plan-1",
                        planHash = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
                        expectedRevision = "revision-7",
                        idempotencyKey = "apply-key-rejected",
                        approvedBy = "test-user",
                        approvedAtUtc = "2026-10-03T12:00:00Z",
                        approvalSource = "test metadata"
                    }
                }
            }, timeout.Token);
            using (JsonDocument wrongDocument = await ReadResponseAsync(process, 2, timeout.Token))
            {
                JsonElement result = wrongDocument.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean(), result.GetRawText());
            }

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "revit_test_apply_change_plan",
                    arguments = new
                    {
                        revitProcessId = fakeRevitProcessId,
                        sessionId = "session-1",
                        documentKey = "test-doc-only",
                        planId = "plan-1",
                        planHash = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
                        expectedRevision = "revision-7",
                        idempotencyKey = "apply-key-rejected-raw",
                        approvedBy = "test-user",
                        approvedAtUtc = "2026-10-03T12:00:00Z",
                        approvalSource = "test metadata",
                        unexpected = true
                    }
                }
            }, timeout.Token);
            using (JsonDocument unexpectedArgument = await ReadResponseAsync(process, 3, timeout.Token))
            {
                JsonElement result = unexpectedArgument.RootElement.GetProperty("result");
                Assert.True(result.GetProperty("isError").GetBoolean(), result.GetRawText());
            }

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
                        revitProcessId = fakeRevitProcessId,
                        sessionId = "session-1",
                        documentKey = "test-doc-only",
                        expectedRevision = "revision-7",
                        idempotencyKey = "plan-key-1",
                        target = new { kind = "Element", uniqueId = "element-uid-1", elementId = 42 },
                        parameter = new
                        {
                            kind = "ParameterElement",
                            definitionId = 84,
                            ownerContext = "document:test-doc-only",
                            dataTypeId = "autodesk.spec.aec:string.text-2.0.0",
                            stableId = "parameter-element:project-parameter-uid",
                            name = "Project Comments"
                        },
                        newValue = "Reviewed by MCP"
                    }
                }
            }, timeout.Token);
            using JsonDocument planResponse = await ReadResponseAsync(process, 4, timeout.Token);
            AssertMcpToolSucceeded(planResponse.RootElement);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new
                {
                    name = "revit_test_apply_change_plan",
                    arguments = new
                    {
                        revitProcessId = fakeRevitProcessId,
                        sessionId = "session-1",
                        documentKey = "test-doc-only",
                        planId = "plan-1",
                        planHash = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
                        expectedRevision = "revision-7",
                        idempotencyKey = "apply-key-1",
                        approvedBy = "test-user",
                        approvedAtUtc = "2026-10-03T12:00:00Z",
                        approvalSource = "Revit confirmation test metadata"
                    }
                }
            }, timeout.Token);
            using JsonDocument applyResponse = await ReadResponseAsync(process, 5, timeout.Token);
            AssertMcpToolSucceeded(applyResponse.RootElement);
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

        BridgeRequest[] requests = await capturedRequests;
        Assert.Equal(2, requests.Length);

        BridgeRequest plan = requests[0];
        Assert.Equal(BridgeOperations.PlanSetParameterValues, plan.Operation);
        Assert.Equal("session-1", plan.SessionId);
        Assert.Equal("test-doc-only", plan.DocumentKey);
        Assert.Equal("revision-7", plan.ExpectedRevision);
        Assert.Equal("plan-key-1", plan.IdempotencyKey);
        Assert.Equal("element", plan.Payload.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal("element-uid-1", plan.Payload.GetProperty("target").GetProperty("uniqueId").GetString());
        Assert.Equal(42, plan.Payload.GetProperty("target").GetProperty("elementId").GetInt64());
        Assert.Equal("parameterElement", plan.Payload.GetProperty("parameter").GetProperty("kind").GetString());
        Assert.Equal(84, plan.Payload.GetProperty("parameter").GetProperty("definitionId").GetInt64());
        Assert.Equal("document:test-doc-only", plan.Payload.GetProperty("parameter").GetProperty("ownerContext").GetString());
        Assert.Equal("autodesk.spec.aec:string.text-2.0.0", plan.Payload.GetProperty("parameter").GetProperty("dataTypeId").GetString());
        Assert.Equal("parameter-element:project-parameter-uid", plan.Payload.GetProperty("parameter").GetProperty("stableId").GetString());
        Assert.Equal("Project Comments", plan.Payload.GetProperty("parameter").GetProperty("name").GetString());
        Assert.Equal("string", plan.Payload.GetProperty("after").GetProperty("kind").GetString());
        Assert.Equal("Reviewed by MCP", plan.Payload.GetProperty("after").GetProperty("stringValue").GetString());

        BridgeRequest apply = requests[1];
        Assert.Equal(BridgeOperations.ApplyChangePlan, apply.Operation);
        Assert.Equal("session-1", apply.SessionId);
        Assert.Equal("test-doc-only", apply.DocumentKey);
        Assert.Equal("revision-7", apply.ExpectedRevision);
        Assert.Equal("apply-key-1", apply.IdempotencyKey);
        Assert.Equal("plan-1", apply.Payload.GetProperty("planId").GetString());
        Assert.Equal("aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899", apply.Payload.GetProperty("planHash").GetString());
        Assert.Equal("test-user", apply.Payload.GetProperty("approval").GetProperty("approvedBy").GetString());
        Assert.Equal("2026-10-03T12:00:00+00:00", apply.Payload.GetProperty("approval").GetProperty("approvedAtUtc").GetString());
        Assert.Equal("Revit confirmation test metadata", apply.Payload.GetProperty("approval").GetProperty("source").GetString());
    }

    private static void AssertMcpToolSucceeded(JsonElement response)
    {
        JsonElement result = response.GetProperty("result");
        Assert.False(
            result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(),
            response.GetRawText());
    }

    private static async Task<BridgeRequest[]> CaptureRequestsAsync(string endpoint, int count, CancellationToken cancellationToken)
    {
        var requests = new List<BridgeRequest>(count);
        for (int index = 0; index < count; index++)
        {
            await using var pipe = new NamedPipeServerStream(
                endpoint,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken);
            BridgeRequest request = await BridgeFrameProtocol.ReadAsync<BridgeRequest>(pipe, cancellationToken);
            requests.Add(request);

            using JsonDocument result = JsonDocument.Parse("{\"accepted\":true}");
            await BridgeFrameProtocol.WriteAsync(pipe, new BridgeResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Result = result.RootElement.Clone(),
                Warnings = [],
                DurationMs = 1
            }, cancellationToken);
        }

        return requests.ToArray();
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
