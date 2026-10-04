using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Server.Transport;

namespace BIMassist.Mcp.Server.Tests.Tools;

public sealed class McpParameterSearchBridgeFlowTests
{
    [Fact]
    public async Task Public_search_forwards_exact_document_filter_and_page_to_bridge()
    {
        string serverAssembly = Path.Combine(AppContext.BaseDirectory, "BIMassist.Mcp.Server.dll");
        Assert.True(File.Exists(serverAssembly), $"MCP server assembly not found: {serverAssembly}");
        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        int fakeRevitProcessId = Environment.ProcessId + 12346;
        string endpoint = BridgeEndpointName.Create(userSid, fakeRevitProcessId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<BridgeRequest> capturedRequest = CaptureRequestAsync(endpoint, timeout.Token);
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
                    clientInfo = new { name = "parameter-search-flow-test", version = "1" }
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
                    name = "revit_search_document_parameters",
                    arguments = new
                    {
                        revitProcessId = fakeRevitProcessId,
                        sessionId = "session-search",
                        documentKey = "exact-document",
                        request = new
                        {
                            nameContains = "Marke",
                            categoryId = "revit-category:-2000011",
                            includeTypes = true,
                            page = new { pageSize = 7 }
                        }
                    }
                }
            }, timeout.Token);
            using JsonDocument searchResponse = await ReadResponseAsync(process, 2, timeout.Token);
            JsonElement toolResult = searchResponse.RootElement.GetProperty("result");
            Assert.False(toolResult.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean(), searchResponse.RootElement.GetRawText());
            string bridgeJson = toolResult.GetProperty("content")[0].GetProperty("text").GetString()!;
            using JsonDocument bridged = JsonDocument.Parse(bridgeJson);
            JsonElement resultPage = bridged.RootElement.GetProperty("result");
            Assert.Equal(1, resultPage.GetProperty("totalCount").GetInt32());
            JsonElement match = Assert.Single(resultPage.GetProperty("items").EnumerateArray());
            Assert.Equal("uid-42", match.GetProperty("target").GetProperty("uniqueId").GetString());
            Assert.Equal("built-in:-1", match.GetProperty("parameter").GetProperty("stableId").GetString());
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
                    process.Kill(entireProcessTree: true);
            }
        }
        Assert.Equal(0, process.ExitCode);
        BridgeRequest request = await capturedRequest;
        Assert.Equal(BridgeOperations.SearchDocumentParameters, request.Operation);
        Assert.Equal("session-search", request.SessionId);
        Assert.Equal("exact-document", request.DocumentKey);
        Assert.Null(request.ExpectedRevision);
        Assert.Null(request.IdempotencyKey);
        Assert.Equal("Marke", request.Payload.GetProperty("nameContains").GetString());
        Assert.Equal("revit-category:-2000011", request.Payload.GetProperty("categoryId").GetString());
        Assert.True(request.Payload.GetProperty("includeTypes").GetBoolean());
        Assert.Equal(7, request.Payload.GetProperty("page").GetProperty("pageSize").GetInt32());
    }

    private static async Task<BridgeRequest> CaptureRequestAsync(string endpoint, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(cancellationToken);
        BridgeRequest request = await BridgeFrameProtocol.ReadAsync<BridgeRequest>(pipe, cancellationToken);
        var page = new PageResult<DocumentParameterMatch>
        {
            Items = [new DocumentParameterMatch
            {
                Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "uid-42", ElementId = 42 },
                Parameter = new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.BuiltIn,
                    BuiltInId = -1,
                    StableId = "built-in:-1",
                    Name = "Marke"
                },
                StorageType = ParameterStorageType.String,
                IsReadOnly = false
            }],
            TotalCount = 1
        };
        await BridgeFrameProtocol.WriteAsync(pipe, new BridgeResponse
        {
            RequestId = request.RequestId,
            Success = true,
            Result = JsonSerializer.SerializeToElement(page, ContractJson.Options),
            Warnings = [],
            DurationMs = 1
        }, cancellationToken);
        return request;
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
