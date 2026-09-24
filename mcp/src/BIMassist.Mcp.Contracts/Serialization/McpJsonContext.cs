using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Sessions;

namespace BIMassist.Mcp.Contracts.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(BridgeError))]
[JsonSerializable(typeof(BridgeWarning))]
[JsonSerializable(typeof(SessionDescriptor))]
[JsonSerializable(typeof(DocumentDescriptor))]
[JsonSerializable(typeof(ParameterIdentity))]
[JsonSerializable(typeof(ParameterMetadata))]
[JsonSerializable(typeof(ParameterDefinitionMetadata))]
[JsonSerializable(typeof(ParameterBindingMetadata))]
[JsonSerializable(typeof(ParameterUnitMetadata))]
[JsonSerializable(typeof(ParameterContextMetadata))]
[JsonSerializable(typeof(ParameterWorksharingMetadata))]
[JsonSerializable(typeof(ParameterValue))]
[JsonSerializable(typeof(ElementReferenceValue))]
[JsonSerializable(typeof(ParameterTarget))]
[JsonSerializable(typeof(ChangePlan))]
[JsonSerializable(typeof(ChangeOperation))]
[JsonSerializable(typeof(ParameterBindingState))]
[JsonSerializable(typeof(SharedParameterDefinitionState))]
[JsonSerializable(typeof(ChangePlanHasher.ChangePlanHashPayload))]
[JsonSerializable(typeof(ApplyChangePlanRequest))]
[JsonSerializable(typeof(ApprovalMetadata))]
[JsonSerializable(typeof(PageRequest))]
[JsonSerializable(typeof(PageResult<string>))]
[JsonSerializable(typeof(PageResult<DocumentDescriptor>))]
[JsonSerializable(typeof(PageResult<SessionDescriptor>))]
[JsonSerializable(typeof(PageResult<ParameterIdentity>))]
[JsonSerializable(typeof(PageResult<ParameterMetadata>))]
[JsonSerializable(typeof(PageResult<ParameterValue>))]
[JsonSerializable(typeof(ListFamiliesRequest))]
[JsonSerializable(typeof(GetFamilyMetadataRequest))]
[JsonSerializable(typeof(FamilySummary))]
[JsonSerializable(typeof(FamilyTypeSummary))]
[JsonSerializable(typeof(FamilyMetadata))]
[JsonSerializable(typeof(PageResult<FamilySummary>))]
[JsonSerializable(typeof(PageResult<FamilyTypeSummary>))]
[JsonSerializable(typeof(ListParametersRequest))]
[JsonSerializable(typeof(ParameterSummary))]
[JsonSerializable(typeof(GetParameterMetadataRequest))]
[JsonSerializable(typeof(PageResult<ParameterSummary>))]
[JsonSerializable(typeof(ListSharedDefinitionsRequest))]
[JsonSerializable(typeof(SharedDefinitionDescriptor))]
[JsonSerializable(typeof(PageResult<SharedDefinitionDescriptor>))]
[JsonSerializable(typeof(ListProjectBindingsRequest))]
[JsonSerializable(typeof(ProjectBindingDescriptor))]
[JsonSerializable(typeof(PageResult<ProjectBindingDescriptor>))]
[JsonSerializable(typeof(GetParameterValuesRequest))]
[JsonSerializable(typeof(ParameterValueEntry))]
[JsonSerializable(typeof(PageResult<ParameterValueEntry>))]
[JsonSerializable(typeof(ExportMetadataSnapshotRequest))]
[JsonSerializable(typeof(MetadataSnapshotSelection))]
[JsonSerializable(typeof(MetadataSnapshotItem))]
[JsonSerializable(typeof(FamilyTypeSnapshotItem))]
[JsonSerializable(typeof(MetadataSnapshotPage))]
[JsonSerializable(typeof(PageResult<MetadataSnapshotItem>))]
internal partial class McpJsonContext : JsonSerializerContext;
