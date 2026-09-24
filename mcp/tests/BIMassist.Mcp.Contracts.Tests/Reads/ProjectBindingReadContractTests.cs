using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class ProjectBindingReadContractTests
{
    [Fact]
    public void Project_binding_payload_and_page_roundtrip_through_closed_contract()
    {
        var request = new ListProjectBindingsRequest
        {
            Page = new PageRequest { PageSize = 25 },
            BindingKind = ParameterBindingKind.ProjectInstance,
            CategoryId = "builtin-category:OST_Doors",
            IsShared = true
        };
        var page = new PageResult<ProjectBindingDescriptor>
        {
            Items =
            [
                new ProjectBindingDescriptor
                {
                    Identity = new ParameterIdentity
                    {
                        Kind = ParameterIdentityKind.SharedGuid,
                        SharedGuid = Guid.Parse("9f51461a-bda5-4a03-8e7d-fb52e6e7d50c"),
                        StableId = "shared:9f51461a-bda5-4a03-8e7d-fb52e6e7d50c",
                        Name = "Fire Rating"
                    },
                    Definition = new ParameterDefinitionMetadata(
                        "Fire Rating",
                        "autodesk.spec.aec:string.text-2.0.0",
                        "autodesk.parameter.group:identityData-1.0.0",
                        null,
                        true,
                        true),
                    Binding = new ParameterBindingMetadata(
                        ParameterBindingKind.ProjectInstance,
                        ["builtin-category:OST_Doors"],
                        null,
                        "project")
                }
            ],
            TotalCount = 1
        };

        ListProjectBindingsRequest restoredRequest = ContractJson.Deserialize<ListProjectBindingsRequest>(ContractJson.Serialize(request));
        PageResult<ProjectBindingDescriptor> restoredPage = ContractJson.Deserialize<PageResult<ProjectBindingDescriptor>>(ContractJson.Serialize(page));

        Assert.True(restoredRequest.IsShared);
        Assert.Single(restoredPage.Items);
        Assert.Equal(ParameterBindingKind.ProjectInstance, restoredPage.Items[0].Binding.Kind);
    }

    [Fact]
    public void Project_binding_operation_rejects_non_project_binding_filter_and_unknown_payload_member()
    {
        BridgeRequest invalidKind = CreateEnvelope("{\"page\":{\"pageSize\":25},\"bindingKind\":\"familyType\"}");
        BridgeRequest unknownMember = CreateEnvelope("{\"page\":{\"pageSize\":25},\"unexpected\":true}");

        Assert.Throws<JsonException>(() => ContractValidator.Validate(invalidKind));
        Assert.Throws<JsonException>(() => ContractValidator.Validate(unknownMember));
    }

    private static BridgeRequest CreateEnvelope(string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-project-binding-contract",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = BridgeOperations.ListProjectBindings,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
