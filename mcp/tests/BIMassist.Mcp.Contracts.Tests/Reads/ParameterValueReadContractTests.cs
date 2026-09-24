using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class ParameterValueReadContractTests
{
    [Fact]
    public void Parameter_value_read_payload_and_page_roundtrip_preserve_external_and_internal_double()
    {
        var request = new GetParameterValuesRequest
        {
            Page = new PageRequest { PageSize = 25 },
            Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "element-uid-1" },
            Parameters =
            [
                new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.BuiltIn,
                    BuiltInId = -1001301,
                    StableId = "builtin:DOOR_WIDTH",
                    Name = "Width"
                }
            ]
        };
        var page = new PageResult<ParameterValueEntry>
        {
            Items =
            [
                new ParameterValueEntry
                {
                    Target = request.Target,
                    Parameter = request.Parameters[0],
                    Value = new ParameterValue
                    {
                        Kind = ParameterValueKind.Double,
                        HasValue = true,
                        IsReadOnly = false,
                        DoubleValue = 1.0,
                        InternalDoubleValue = 3.280839895,
                        DisplayValue = "1.00 m",
                        SpecTypeId = "autodesk.spec.aec:length-2.0.0",
                        InputUnitTypeId = "autodesk.unit.unit:meters-1.0.1"
                    }
                }
            ],
            TotalCount = 1
        };

        GetParameterValuesRequest restoredRequest = ContractJson.Deserialize<GetParameterValuesRequest>(ContractJson.Serialize(request));
        PageResult<ParameterValueEntry> restoredPage = ContractJson.Deserialize<PageResult<ParameterValueEntry>>(ContractJson.Serialize(page));

        Assert.Single(restoredRequest.Parameters!);
        Assert.Equal(1.0, restoredPage.Items[0].Value.DoubleValue);
        Assert.Equal(3.280839895, restoredPage.Items[0].Value.InternalDoubleValue);
    }

    [Fact]
    public void Empty_parameter_value_retains_its_storage_discriminator_without_payload()
    {
        var value = new ParameterValue
        {
            Kind = ParameterValueKind.String,
            HasValue = false,
            IsReadOnly = false
        };

        ContractValidator.Validate(value);
    }

    [Fact]
    public void Parameter_value_get_operation_rejects_empty_selector_and_unknown_payload_member()
    {
        BridgeRequest emptySelector = CreateEnvelope("{\"page\":{\"pageSize\":25},\"target\":{\"kind\":\"element\",\"uniqueId\":\"element-uid-1\"},\"parameters\":[]}");
        BridgeRequest unknownMember = CreateEnvelope("{\"page\":{\"pageSize\":25},\"target\":{\"kind\":\"element\",\"uniqueId\":\"element-uid-1\"},\"unexpected\":true}");

        Assert.Throws<JsonException>(() => ContractValidator.Validate(emptySelector));
        Assert.Throws<JsonException>(() => ContractValidator.Validate(unknownMember));
    }

    private static BridgeRequest CreateEnvelope(string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-parameter-value-contract",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = BridgeOperations.GetParameterValues,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
