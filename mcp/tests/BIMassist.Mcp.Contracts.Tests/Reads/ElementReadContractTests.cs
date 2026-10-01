using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class ElementReadContractTests
{
    [Fact]
    public void Element_list_request_and_page_roundtrip_through_closed_contract()
    {
        var request = new ListElementsRequest
        {
            Page = new PageRequest { PageSize = 25 },
            NameContains = "door",
            CategoryId = "revit-category:-2000014",
            IncludeTypes = false
        };
        var page = new PageResult<ElementSummary>
        {
            Items =
            [
                new ElementSummary
                {
                    UniqueId = "element-uid-1",
                    ElementId = 42,
                    Name = "Door 1",
                    CategoryId = "revit-category:-2000014",
                    CategoryName = "Doors",
                    IsElementType = false,
                    TypeUniqueId = "type-uid-1",
                    TypeName = "Single"
                }
            ],
            TotalCount = 1
        };

        string requestJson = ContractJson.Serialize(request);
        string pageJson = ContractJson.Serialize(page);

        Assert.Equal(request, ContractJson.Deserialize<ListElementsRequest>(requestJson));
        PageResult<ElementSummary> restored = ContractJson.Deserialize<PageResult<ElementSummary>>(pageJson);
        ElementSummary element = Assert.Single(restored.Items);
        Assert.Equal("element-uid-1", element.UniqueId);
        Assert.Equal("type-uid-1", element.TypeUniqueId);
    }

    [Fact]
    public void Element_list_operation_requires_exact_payload_contract()
    {
        BridgeRequest valid = CreateEnvelope(
            BridgeOperations.ListElements,
            "{\"page\":{\"pageSize\":25},\"includeTypes\":false,\"nameContains\":\"Door\"}");
        BridgeRequest missingPage = CreateEnvelope(
            BridgeOperations.ListElements,
            "{\"includeTypes\":false,\"nameContains\":\"Door\"}");
        BridgeRequest unknownMember = CreateEnvelope(
            BridgeOperations.ListElements,
            "{\"page\":{\"pageSize\":25},\"includeTypes\":false,\"nameContains\":\"Door\",\"unexpected\":true}");

        ContractValidator.Validate(valid);
        Assert.Throws<JsonException>(() => ContractValidator.Validate(missingPage));
        Assert.Throws<JsonException>(() => ContractValidator.Validate(unknownMember));
    }

    [Fact]
    public void Element_summary_requires_complete_type_identity_and_positive_element_id()
    {
        var missingTypeName = new ElementSummary
        {
            UniqueId = "element-uid-1",
            ElementId = 42,
            Name = "Door 1",
            IsElementType = false,
            TypeUniqueId = "type-uid-1"
        };
        var nonPositiveId = new ElementSummary
        {
            UniqueId = "element-uid-2",
            ElementId = 0,
            Name = "Door 2",
            IsElementType = true
        };

        Assert.Throws<JsonException>(() => ContractValidator.Validate(missingTypeName));
        Assert.Throws<JsonException>(() => ContractValidator.Validate(nonPositiveId));
    }

    [Fact]
    public void Element_list_rejects_noncanonical_category_identity()
    {
        var request = new ListElementsRequest
        {
            Page = new PageRequest { PageSize = 1 },
            CategoryId = "revit-category:+7",
            IncludeTypes = false
        };

        Assert.Throws<JsonException>(() => ContractValidator.Validate(request));
    }

    [Fact]
    public void Element_list_requires_at_least_one_search_filter()
    {
        var request = new ListElementsRequest
        {
            Page = new PageRequest { PageSize = 1 },
            IncludeTypes = false
        };

        Assert.Throws<JsonException>(() => ContractValidator.Validate(request));
    }

    private static BridgeRequest CreateEnvelope(string operation, string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-element-contract",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = operation,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
