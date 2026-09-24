using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class SharedDefinitionReadContractTests
{
    [Fact]
    public void Shared_definition_payload_and_page_roundtrip_through_closed_contract()
    {
        Guid guid = Guid.Parse("9f51461a-bda5-4a03-8e7d-fb52e6e7d50c");
        var request = new ListSharedDefinitionsRequest
        {
            Page = new PageRequest { PageSize = 25 },
            GroupName = "Architecture",
            SharedGuid = guid
        };
        var page = new PageResult<SharedDefinitionDescriptor>
        {
            Items =
            [
                new SharedDefinitionDescriptor
                {
                    SharedGuid = guid,
                    Name = "Fire Rating",
                    DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
                    GroupName = "Architecture",
                    Description = "Door fire rating",
                    IsVisible = true,
                    IsUserModifiable = true
                }
            ],
            TotalCount = 1
        };

        ListSharedDefinitionsRequest restoredRequest = ContractJson.Deserialize<ListSharedDefinitionsRequest>(ContractJson.Serialize(request));
        PageResult<SharedDefinitionDescriptor> restoredPage = ContractJson.Deserialize<PageResult<SharedDefinitionDescriptor>>(ContractJson.Serialize(page));

        Assert.Equal(guid, restoredRequest.SharedGuid);
        Assert.Single(restoredPage.Items);
        Assert.Equal("Architecture", restoredPage.Items[0].GroupName);
    }

    [Fact]
    public void Shared_definition_operation_validates_exact_payload_contract()
    {
        BridgeRequest valid = CreateEnvelope("{\"page\":{\"pageSize\":25},\"nameContains\":\"Fire\"}");
        BridgeRequest missingPage = CreateEnvelope("{\"nameContains\":\"Fire\"}");
        BridgeRequest unknownMember = CreateEnvelope("{\"page\":{\"pageSize\":25},\"unexpected\":true}");

        ContractValidator.Validate(valid);
        Assert.Throws<JsonException>(() => ContractValidator.Validate(missingPage));
        Assert.Throws<JsonException>(() => ContractValidator.Validate(unknownMember));
    }

    private static BridgeRequest CreateEnvelope(string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-shared-definition-contract",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = BridgeOperations.ListSharedDefinitions,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
