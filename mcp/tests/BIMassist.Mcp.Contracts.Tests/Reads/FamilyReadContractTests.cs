using System.Text.Json;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class FamilyReadContractTests
{
    [Fact]
    public void Family_list_payload_and_page_roundtrip_through_closed_contract()
    {
        var request = new ListFamiliesRequest
        {
            Page = new PageRequest { PageSize = 25 },
            NameContains = "Door",
            IncludeInPlace = false
        };
        var page = new PageResult<FamilySummary>
        {
            Items =
            [
                new FamilySummary
                {
                    UniqueId = "family-uid-1",
                    ElementId = 42,
                    Name = "Door - Single",
                    CategoryId = "autodesk.revit.category.family:doors",
                    CategoryName = "Doors",
                    IsInPlace = false,
                    IsEditable = true,
                    IsShared = false,
                    TypeCount = 2
                }
            ],
            TotalCount = 1
        };

        string requestJson = ContractJson.Serialize(request);
        string pageJson = ContractJson.Serialize(page);

        Assert.Equal(request, ContractJson.Deserialize<ListFamiliesRequest>(requestJson));
        PageResult<FamilySummary> restored = ContractJson.Deserialize<PageResult<FamilySummary>>(pageJson);
        Assert.Single(restored.Items);
        Assert.Equal("family-uid-1", restored.Items[0].UniqueId);
    }

    [Fact]
    public void Family_metadata_rejects_duplicate_type_identities()
    {
        var metadata = new FamilyMetadata
        {
            Family = new FamilySummary
            {
                UniqueId = "family-uid-1",
                ElementId = 42,
                Name = "Door - Single",
                IsInPlace = false,
                IsEditable = true,
                IsShared = false,
                TypeCount = 2
            },
            Types = new PageResult<FamilyTypeSummary>
            {
                Items =
                [
                    new FamilyTypeSummary { UniqueId = "type-uid-1", ElementId = 43, Name = "900 x 2100" },
                    new FamilyTypeSummary { UniqueId = "type-uid-1", ElementId = 44, Name = "1000 x 2100" }
                ],
                TotalCount = 2
            }
        };

        Assert.Throws<JsonException>(() => ContractValidator.Validate(metadata));
    }

    [Fact]
    public void Family_metadata_pages_types_without_limiting_the_family_total()
    {
        var metadata = new FamilyMetadata
        {
            Family = new FamilySummary
            {
                UniqueId = "family-uid-1",
                ElementId = 42,
                Name = "Large family",
                IsInPlace = false,
                IsEditable = true,
                IsShared = false,
                TypeCount = 1_500
            },
            Types = new PageResult<FamilyTypeSummary>
            {
                Items = [new FamilyTypeSummary { UniqueId = "type-uid-1", ElementId = 43, Name = "Type 1" }],
                NextCursor = "next-page",
                TotalCount = 1_500
            }
        };

        ContractValidator.Validate(metadata);
    }

    [Fact]
    public void Family_operations_validate_their_exact_payload_contract()
    {
        BridgeRequest valid = CreateEnvelope(
            BridgeOperations.ListFamilies,
            "{\"page\":{\"pageSize\":25},\"includeInPlace\":false}");
        BridgeRequest missingPage = CreateEnvelope(
            BridgeOperations.ListFamilies,
            "{\"includeInPlace\":false}");
        BridgeRequest unknownMember = CreateEnvelope(
            BridgeOperations.GetFamilyMetadata,
            "{\"familyUniqueId\":\"family-uid-1\",\"page\":{\"pageSize\":25},\"unexpected\":true}");

        ContractValidator.Validate(valid);
        Assert.Throws<JsonException>(() => ContractValidator.Validate(missingPage));
        Assert.Throws<JsonException>(() => ContractValidator.Validate(unknownMember));
    }

    private static BridgeRequest CreateEnvelope(string operation, string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-family-contract",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = operation,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
