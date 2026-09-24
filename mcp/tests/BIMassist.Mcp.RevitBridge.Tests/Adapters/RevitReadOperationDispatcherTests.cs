using System.Security.Cryptography;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Adapters;

public sealed class RevitReadOperationDispatcherTests
{
    [Fact]
    public void Family_list_is_materialized_serialized_and_revision_bound()
    {
        var record = new FamilyReadRecord(
            new FamilySummary
            {
                UniqueId = "family-uid-1",
                ElementId = 42,
                Name = "Door - Single",
                IsInPlace = false,
                IsEditable = true,
                IsShared = false,
                TypeCount = 1
            },
            [new FamilyTypeSummary { UniqueId = "type-uid-1", ElementId = 43, Name = "900 x 2100" }]);
        var source = new FakeFamilySource(new FamilyReadSnapshot("document-1", "revision-3", [record]));
        byte[] key = SHA256.HashData("BIMassist dispatcher test"u8);
        var familyService = new FamilyReadService(new StablePaginator(new ReadCursorCodec(key)));
        var dispatcher = new RevitReadOperationDispatcher(source, familyService);
        BridgeRequest request = CreateRequest(
            BridgeOperations.ListFamilies,
            "{\"page\":{\"pageSize\":25},\"includeInPlace\":false}");

        ReadOperationResult result = dispatcher.Process(request);
        PageResult<FamilySummary> page = ContractJson.Deserialize<PageResult<FamilySummary>>(result.Result.GetRawText());

        Assert.Equal("revision-3", result.DocumentRevision);
        Assert.Equal("session-1", source.SessionId);
        Assert.Equal("document-1", source.DocumentKey);
        Assert.Equal("family-uid-1", Assert.Single(page.Items).UniqueId);
    }

    private static BridgeRequest CreateRequest(string operation, string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-read-dispatcher",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = operation,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };

    private sealed class FakeFamilySource(FamilyReadSnapshot snapshot) : IRevitFamilyReadSource
    {
        public string? SessionId { get; private set; }
        public string? DocumentKey { get; private set; }

        public FamilyReadSnapshot ReadFamilies(string sessionId, string documentKey)
        {
            SessionId = sessionId;
            DocumentKey = documentKey;
            return snapshot;
        }
    }
}
