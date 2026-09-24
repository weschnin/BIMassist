using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class ParameterReadContractTests
{
    [Fact]
    public void Parameter_list_payload_and_page_roundtrip_through_closed_contract()
    {
        var request = new ListParametersRequest
        {
            Page = new PageRequest { PageSize = 50 },
            Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "element-uid-1" },
            NameContains = "Width",
            StorageType = ParameterStorageType.Double,
            IsReadOnly = false
        };
        var page = new PageResult<ParameterSummary>
        {
            Items =
            [
                new ParameterSummary
                {
                    Identity = new ParameterIdentity
                    {
                        Kind = ParameterIdentityKind.BuiltIn,
                        BuiltInId = -1001301,
                        StableId = "builtin:DOOR_WIDTH",
                        Name = "Width"
                    },
                    StorageType = ParameterStorageType.Double,
                    BindingKind = ParameterBindingKind.ProjectType,
                    IsReadOnly = false
                }
            ],
            TotalCount = 1
        };

        ListParametersRequest restoredRequest = ContractJson.Deserialize<ListParametersRequest>(ContractJson.Serialize(request));
        PageResult<ParameterSummary> restoredPage = ContractJson.Deserialize<PageResult<ParameterSummary>>(ContractJson.Serialize(page));

        Assert.Equal(ParameterTargetKind.Element, restoredRequest.Target.Kind);
        Assert.Single(restoredPage.Items);
        Assert.Equal("builtin:DOOR_WIDTH", restoredPage.Items[0].Identity.StableId);
    }

    [Fact]
    public void Parameter_read_operations_validate_their_exact_payload_contract()
    {
        BridgeRequest valid = CreateEnvelope(
            BridgeOperations.ListParameters,
            "{\"page\":{\"pageSize\":25},\"target\":{\"kind\":\"element\",\"uniqueId\":\"element-uid-1\"}}");
        BridgeRequest missingTarget = CreateEnvelope(
            BridgeOperations.ListParameters,
            "{\"page\":{\"pageSize\":25}}");
        BridgeRequest unknownMember = CreateEnvelope(
            BridgeOperations.GetParameterMetadata,
            "{\"target\":{\"kind\":\"element\",\"uniqueId\":\"element-uid-1\"},\"parameter\":{\"kind\":\"builtIn\",\"stableId\":\"builtin:DOOR_WIDTH\",\"name\":\"Width\"},\"unexpected\":true}");

        ContractValidator.Validate(valid);
        Assert.Throws<JsonException>(() => ContractValidator.Validate(missingTarget));
        Assert.Throws<JsonException>(() => ContractValidator.Validate(unknownMember));
    }

    private static BridgeRequest CreateEnvelope(string operation, string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-parameter-contract",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = operation,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
