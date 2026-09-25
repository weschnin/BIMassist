using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Adapters;

internal sealed class RevitReadOperationDispatcher : IReadOperationDispatcher
{
    private readonly IRevitFamilyReadSource _families;
    private readonly FamilyReadService _familyService;
    private readonly IRevitSharedDefinitionReadSource? _sharedDefinitions;
    private readonly SharedDefinitionReadService? _sharedDefinitionService;
    private readonly IRevitProjectBindingReadSource? _projectBindings;
    private readonly ProjectBindingReadService? _projectBindingService;
    private readonly IRevitParameterReadSource? _parameters;
    private readonly ParameterReadService? _parameterService;

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService)
        : this(families, familyService, null, null, null, null, null, null)
    {
    }

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService,
        IRevitSharedDefinitionReadSource? sharedDefinitions,
        SharedDefinitionReadService? sharedDefinitionService)
        : this(families, familyService, sharedDefinitions, sharedDefinitionService, null, null, null, null)
    {
    }

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService,
        IRevitSharedDefinitionReadSource? sharedDefinitions,
        SharedDefinitionReadService? sharedDefinitionService,
        IRevitProjectBindingReadSource? projectBindings,
        ProjectBindingReadService? projectBindingService)
        : this(
            families,
            familyService,
            sharedDefinitions,
            sharedDefinitionService,
            projectBindings,
            projectBindingService,
            null,
            null)
    {
    }

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService,
        IRevitSharedDefinitionReadSource? sharedDefinitions,
        SharedDefinitionReadService? sharedDefinitionService,
        IRevitProjectBindingReadSource? projectBindings,
        ProjectBindingReadService? projectBindingService,
        IRevitParameterReadSource? parameters,
        ParameterReadService? parameterService)
    {
        _families = families ?? throw new ArgumentNullException(nameof(families));
        _familyService = familyService ?? throw new ArgumentNullException(nameof(familyService));
        _sharedDefinitions = sharedDefinitions;
        _sharedDefinitionService = sharedDefinitionService;
        _projectBindings = projectBindings;
        _projectBindingService = projectBindingService;
        _parameters = parameters;
        _parameterService = parameterService;
    }

    public ReadOperationResult Process(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        return request.Operation switch
        {
            BridgeOperations.ListFamilies => ListFamilies(request),
            BridgeOperations.GetFamilyMetadata => GetFamilyMetadata(request),
            BridgeOperations.ListSharedDefinitions => ListSharedDefinitions(request),
            BridgeOperations.ListProjectBindings => ListProjectBindings(request),
            BridgeOperations.ListParameters => ListParameters(request),
            BridgeOperations.GetParameterMetadata => GetParameterMetadata(request),
            _ => throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported)
        };
    }

    private ReadOperationResult ListFamilies(BridgeRequest request)
    {
        ListFamiliesRequest payload = ContractJson.Deserialize<ListFamiliesRequest>(request.Payload.GetRawText());
        FamilyReadSnapshot snapshot = ReadSnapshot(request);
        PageResult<FamilySummary> result = _familyService.ListFamilies(
            snapshot.Families,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private ReadOperationResult GetFamilyMetadata(BridgeRequest request)
    {
        GetFamilyMetadataRequest payload = ContractJson.Deserialize<GetFamilyMetadataRequest>(request.Payload.GetRawText());
        FamilyReadSnapshot snapshot = ReadSnapshot(request);
        FamilyMetadata result = _familyService.GetFamilyMetadata(
            snapshot.Families,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private ReadOperationResult ListSharedDefinitions(BridgeRequest request)
    {
        if (_sharedDefinitions is null || _sharedDefinitionService is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }

        ListSharedDefinitionsRequest payload =
            ContractJson.Deserialize<ListSharedDefinitionsRequest>(request.Payload.GetRawText());
        SharedDefinitionReadSnapshot snapshot =
            _sharedDefinitions.ReadSharedDefinitions(request.SessionId!, request.DocumentKey!);
        PageResult<SharedDefinitionDescriptor> result = _sharedDefinitionService.ListDefinitions(
            snapshot.Definitions,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private ReadOperationResult ListProjectBindings(BridgeRequest request)
    {
        if (_projectBindings is null || _projectBindingService is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }

        ListProjectBindingsRequest payload =
            ContractJson.Deserialize<ListProjectBindingsRequest>(request.Payload.GetRawText());
        ProjectBindingReadSnapshot snapshot =
            _projectBindings.ReadProjectBindings(request.SessionId!, request.DocumentKey!);
        PageResult<ProjectBindingDescriptor> result = _projectBindingService.ListBindings(
            snapshot.Bindings,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private ReadOperationResult ListParameters(BridgeRequest request)
    {
        if (_parameters is null || _parameterService is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }

        ListParametersRequest payload =
            ContractJson.Deserialize<ListParametersRequest>(request.Payload.GetRawText());
        ParameterReadSnapshot snapshot =
            _parameters.ReadParameters(request.SessionId!, request.DocumentKey!, payload.Target);
        PageResult<ParameterSummary> result = _parameterService.ListParameters(
            snapshot.Parameters,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private ReadOperationResult GetParameterMetadata(BridgeRequest request)
    {
        if (_parameters is null || _parameterService is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }

        GetParameterMetadataRequest payload =
            ContractJson.Deserialize<GetParameterMetadataRequest>(request.Payload.GetRawText());
        ParameterReadSnapshot snapshot =
            _parameters.ReadParameters(request.SessionId!, request.DocumentKey!, payload.Target);
        ParameterMetadata result = _parameterService.GetParameterMetadata(snapshot.Parameters, payload);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private FamilyReadSnapshot ReadSnapshot(BridgeRequest request) =>
        _families.ReadFamilies(request.SessionId!, request.DocumentKey!);

    private static ReadOperationResult Serialize<T>(T result, string revision)
    {
        ContractValidator.Validate(result);
        JsonElement json = JsonSerializer.SerializeToElement(result, ContractJson.Options);
        return new ReadOperationResult(json, revision);
    }
}
