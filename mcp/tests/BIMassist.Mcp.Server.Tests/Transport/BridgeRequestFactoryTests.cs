using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Server.Transport;

namespace BIMassist.Mcp.Server.Tests.Transport;

public sealed class BridgeRequestFactoryTests
{
    [Fact]
    public void Serializes_registered_empty_payloads_as_empty_objects()
    {
        BridgeRequest request = BridgeRequestFactory.Create(
            BridgeOperations.GetStatus,
            new EmptyPayload(),
            requestId: "status-request");

        Assert.Equal(JsonValueKind.Object, request.Payload.ValueKind);
        Assert.Empty(request.Payload.EnumerateObject());
    }

    [Fact]
    public void Creates_a_versioned_request_and_materializes_the_payload()
    {
        var payload = new ListFamiliesRequest
        {
            Page = new PageRequest { PageSize = 25 },
            IncludeInPlace = false,
            NameContains = "Valve"
        };

        BridgeRequest request = BridgeRequestFactory.Create(
            BridgeOperations.ListFamilies,
            payload,
            requestId: "fixed-id",
            sessionId: "session",
            documentKey: "document",
            expectedRevision: "revision");

        Assert.Equal(ProtocolVersions.ProtocolVersion, request.ProtocolVersion);
        Assert.Equal(ProtocolVersions.SchemaVersion, request.SchemaVersion);
        Assert.Equal("fixed-id", request.RequestId);
        Assert.Equal("session", request.SessionId);
        Assert.Equal("document", request.DocumentKey);
        Assert.Equal("revision", request.ExpectedRevision);
        Assert.Equal(BridgeOperations.ListFamilies, request.Operation);
        Assert.Equal(25, request.Payload.GetProperty("page").GetProperty("pageSize").GetInt32());
        Assert.Equal("Valve", request.Payload.GetProperty("nameContains").GetString());
    }
}
