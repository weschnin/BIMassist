using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class MetadataSnapshotContractTests
{
    [Fact]
    public void Metadata_snapshot_payload_and_page_roundtrip_through_closed_contract()
    {
        var request = new ExportMetadataSnapshotRequest
        {
            Page = new PageRequest { PageSize = 25 },
            Selection = new MetadataSnapshotSelection
            {
                IncludeFamilies = true,
                IncludeSharedDefinitions = false,
                IncludeProjectBindings = false,
                ParameterTargets = [],
                IncludeParameterValues = false
            }
        };
        var result = new MetadataSnapshotPage
        {
            SnapshotId = "snapshot-1",
            DocumentKey = "document-1",
            DocumentRevision = "revision-1",
            CreatedAtUtc = DateTimeOffset.Parse("2026-09-24T06:00:00Z"),
            ExpiresAtUtc = DateTimeOffset.Parse("2026-09-24T06:05:00Z"),
            Page = new PageResult<MetadataSnapshotItem>
            {
                Items =
                [
                    new MetadataSnapshotItem
                    {
                        Kind = MetadataSnapshotItemKind.Family,
                        Family = new FamilySummary
                        {
                            UniqueId = "family-uid-1",
                            ElementId = 42,
                            Name = "Door - Single",
                            IsInPlace = false,
                            IsEditable = true,
                            IsShared = false,
                            TypeCount = 1
                        }
                    }
                ],
                TotalCount = 1
            }
        };

        ExportMetadataSnapshotRequest restoredRequest = ContractJson.Deserialize<ExportMetadataSnapshotRequest>(ContractJson.Serialize(request));
        MetadataSnapshotPage restoredResult = ContractJson.Deserialize<MetadataSnapshotPage>(ContractJson.Serialize(result));

        Assert.True(restoredRequest.Selection.IncludeFamilies);
        Assert.Single(restoredResult.Page.Items);
        Assert.Equal(MetadataSnapshotItemKind.Family, restoredResult.Page.Items[0].Kind);
    }

    [Fact]
    public void Snapshot_item_discriminator_rejects_multiple_payloads()
    {
        var item = new MetadataSnapshotItem
        {
            Kind = MetadataSnapshotItemKind.Family,
            Family = new FamilySummary
            {
                UniqueId = "family-uid-1",
                ElementId = 42,
                Name = "Door - Single",
                IsInPlace = false,
                IsEditable = true,
                IsShared = false,
                TypeCount = 0
            },
            SharedDefinition = new SharedDefinitionDescriptor
            {
                SharedGuid = Guid.Parse("9f51461a-bda5-4a03-8e7d-fb52e6e7d50c"),
                Name = "Fire Rating",
                DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
                GroupName = "Architecture",
                IsVisible = true,
                IsUserModifiable = true
            }
        };

        Assert.Throws<JsonException>(() => ContractValidator.Validate(item));
    }

    [Fact]
    public void Metadata_snapshot_operation_rejects_empty_selection()
    {
        const string payload = """
            {
              "page":{"pageSize":25},
              "selection":{
                "includeFamilies":false,
                "includeSharedDefinitions":false,
                "includeProjectBindings":false,
                "parameterTargets":[],
                "includeParameterValues":false
              }
            }
            """;
        var request = new BridgeRequest
        {
            ProtocolVersion = ProtocolVersions.ProtocolVersion,
            SchemaVersion = ProtocolVersions.SchemaVersion,
            RequestId = "request-snapshot-contract",
            SessionId = "session-1",
            DocumentKey = "document-1",
            Operation = BridgeOperations.ExportMetadataSnapshot,
            Payload = JsonDocument.Parse(payload).RootElement.Clone()
        };

        Assert.Throws<JsonException>(() => ContractValidator.Validate(request));
    }
}
