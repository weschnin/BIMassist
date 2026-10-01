using System.Security.Cryptography;
using System.Text;
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
    private readonly ParameterValueReadService? _parameterValueService;
    private readonly MetadataSnapshotService? _metadataSnapshotService;
    private readonly Func<string, string>? _currentRevision;
    private readonly IRevitElementReadSource? _elements;
    private readonly ElementReadService? _elementService;

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService)
        : this(families, familyService, null, null, null, null, null, null, null)
    {
    }

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService,
        IRevitSharedDefinitionReadSource? sharedDefinitions,
        SharedDefinitionReadService? sharedDefinitionService)
        : this(families, familyService, sharedDefinitions, sharedDefinitionService, null, null, null, null, null)
    {
    }

    internal RevitReadOperationDispatcher(
        IRevitFamilyReadSource families,
        FamilyReadService familyService,
        IRevitElementReadSource elements,
        ElementReadService elementService)
        : this(
            families,
            familyService,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            elements,
            elementService)
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
        : this(
            families,
            familyService,
            sharedDefinitions,
            sharedDefinitionService,
            projectBindings,
            projectBindingService,
            parameters,
            parameterService,
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
        ParameterReadService? parameterService,
        ParameterValueReadService? parameterValueService,
        MetadataSnapshotService? metadataSnapshotService = null,
        Func<string, string>? currentRevision = null,
        IRevitElementReadSource? elements = null,
        ElementReadService? elementService = null)
    {
        _families = families ?? throw new ArgumentNullException(nameof(families));
        _familyService = familyService ?? throw new ArgumentNullException(nameof(familyService));
        _sharedDefinitions = sharedDefinitions;
        _sharedDefinitionService = sharedDefinitionService;
        _projectBindings = projectBindings;
        _projectBindingService = projectBindingService;
        _parameters = parameters;
        _parameterService = parameterService;
        _parameterValueService = parameterValueService;
        _metadataSnapshotService = metadataSnapshotService;
        _currentRevision = currentRevision;
        _elements = elements;
        _elementService = elementService;
    }

    public ReadOperationResult Process(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        return request.Operation switch
        {
            BridgeOperations.ListFamilies => ListFamilies(request),
            BridgeOperations.ListElements => ListElements(request),
            BridgeOperations.GetFamilyMetadata => GetFamilyMetadata(request),
            BridgeOperations.ListSharedDefinitions => ListSharedDefinitions(request),
            BridgeOperations.ListProjectBindings => ListProjectBindings(request),
            BridgeOperations.ListParameters => ListParameters(request),
            BridgeOperations.GetParameterMetadata => GetParameterMetadata(request),
            BridgeOperations.GetParameterValues => GetParameterValues(request),
            BridgeOperations.ExportMetadataSnapshot => ExportMetadataSnapshot(request),
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

    private ReadOperationResult ListElements(BridgeRequest request)
    {
        if (_elements is null || _elementService is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }

        ListElementsRequest payload = ContractJson.Deserialize<ListElementsRequest>(request.Payload.GetRawText());
        ElementReadSnapshot snapshot =
            _elements.ReadElements(request.SessionId!, request.DocumentKey!, payload);
        PageResult<ElementSummary> result = _elementService.ListElements(
            snapshot.Elements,
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

    private ReadOperationResult GetParameterValues(BridgeRequest request)
    {
        if (_parameters is null || _parameterValueService is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }

        GetParameterValuesRequest payload =
            ContractJson.Deserialize<GetParameterValuesRequest>(request.Payload.GetRawText());
        ParameterReadSnapshot snapshot =
            _parameters.ReadParameters(request.SessionId!, request.DocumentKey!, payload.Target);
        PageResult<ParameterValueEntry> result = _parameterValueService.GetValues(
            snapshot.Parameters,
            payload,
            snapshot.DocumentKey,
            snapshot.DocumentRevision);
        return Serialize(result, snapshot.DocumentRevision);
    }

    private ReadOperationResult ExportMetadataSnapshot(BridgeRequest request)
    {
        if (_metadataSnapshotService is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }

        ExportMetadataSnapshotRequest payload =
            ContractJson.Deserialize<ExportMetadataSnapshotRequest>(request.Payload.GetRawText());
        if (payload.Page.Cursor is not null)
        {
            MetadataSnapshotPage cached = _metadataSnapshotService.Continue(payload, request.DocumentKey!);
            return Serialize(cached, cached.DocumentRevision);
        }

        MetadataSnapshotCollector collector = _metadataSnapshotService.CreateCollector();
        string? revision = null;
        string documentKey = request.DocumentKey!;
        void BindSnapshot(string actualDocumentKey, string actualRevision)
        {
            if (!string.Equals(actualDocumentKey, documentKey, StringComparison.Ordinal))
            {
                throw new ReadCursorException(BridgeErrorCodes.InvalidRequest);
            }
            if (revision is not null && !string.Equals(revision, actualRevision, StringComparison.Ordinal))
            {
                throw new ReadCursorException(BridgeErrorCodes.DocumentChanged);
            }

            revision = actualRevision;
        }

        if (payload.Selection.IncludeFamilies)
        {
            FamilyReadSnapshot snapshot = _families.ReadFamilies(request.SessionId!, documentKey);
            BindSnapshot(snapshot.DocumentKey, snapshot.DocumentRevision);
            foreach (FamilyReadRecord family in snapshot.Families)
            {
                collector.Add(
                    $"family:{HashIdentity(family.Family.UniqueId)}",
                    new MetadataSnapshotItem
                    {
                        Kind = MetadataSnapshotItemKind.Family,
                        Family = family.Family with { TypeCount = family.Types.Count }
                    });
                foreach (FamilyTypeSummary type in family.Types)
                {
                    collector.Add(
                        $"familyType:{HashIdentity(family.Family.UniqueId, type.UniqueId)}",
                        new MetadataSnapshotItem
                        {
                            Kind = MetadataSnapshotItemKind.FamilyType,
                            FamilyType = new FamilyTypeSnapshotItem
                            {
                                FamilyUniqueId = family.Family.UniqueId,
                                Type = type
                            }
                        });
                }
            }
        }

        if (payload.Selection.IncludeSharedDefinitions)
        {
            if (_sharedDefinitions is null)
            {
                throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
            }

            SharedDefinitionReadSnapshot snapshot =
                _sharedDefinitions.ReadSharedDefinitions(request.SessionId!, documentKey);
            BindSnapshot(snapshot.DocumentKey, snapshot.DocumentRevision);
            foreach (SharedDefinitionDescriptor definition in snapshot.Definitions)
            {
                collector.Add(
                    $"shared:{definition.SharedGuid:D}",
                    new MetadataSnapshotItem
                    {
                        Kind = MetadataSnapshotItemKind.SharedDefinition,
                        SharedDefinition = definition
                    });
            }
        }

        if (payload.Selection.IncludeProjectBindings)
        {
            if (_projectBindings is null)
            {
                throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
            }

            ProjectBindingReadSnapshot snapshot =
                _projectBindings.ReadProjectBindings(request.SessionId!, documentKey);
            BindSnapshot(snapshot.DocumentKey, snapshot.DocumentRevision);
            foreach (ProjectBindingDescriptor binding in snapshot.Bindings)
            {
                collector.Add(
                    $"binding:{HashIdentity(binding.Identity.StableId)}",
                    new MetadataSnapshotItem
                    {
                        Kind = MetadataSnapshotItemKind.ProjectBinding,
                        ProjectBinding = binding
                    });
            }
        }

        foreach (ParameterTarget target in payload.Selection.ParameterTargets)
        {
            if (_parameters is null)
            {
                throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
            }

            ParameterReadSnapshot snapshot =
                _parameters.ReadParameters(request.SessionId!, documentKey, target);
            BindSnapshot(snapshot.DocumentKey, snapshot.DocumentRevision);
            string targetIdentity = TargetIdentity(target);
            foreach (ParameterReadRecord parameter in snapshot.Parameters)
            {
                collector.Add(
                    $"parameterMetadata:{HashIdentity(targetIdentity, parameter.Summary.Identity.StableId)}",
                    new MetadataSnapshotItem
                    {
                        Kind = MetadataSnapshotItemKind.ParameterMetadata,
                        ParameterMetadata = parameter.Metadata
                    });
                if (payload.Selection.IncludeParameterValues && parameter.Value is not null)
                {
                    collector.Add(
                        $"parameterValue:{HashIdentity(targetIdentity, parameter.Summary.Identity.StableId)}",
                        new MetadataSnapshotItem
                        {
                            Kind = MetadataSnapshotItemKind.ParameterValue,
                            ParameterValue = new ParameterValueEntry
                            {
                                Target = target,
                                Parameter = parameter.Summary.Identity,
                                Value = parameter.Value
                            }
                        });
                }
            }
        }

        if (revision is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.InvalidRequest);
        }
        if (_currentRevision is not null &&
            !string.Equals(_currentRevision(documentKey), revision, StringComparison.Ordinal))
        {
            throw new ReadCursorException(BridgeErrorCodes.DocumentChanged);
        }

        MetadataSnapshotPage result = _metadataSnapshotService.Create(
            payload,
            documentKey,
            revision,
            collector);
        return Serialize(result, revision);
    }

    private static string TargetIdentity(ParameterTarget target)
    {
        string uniqueId = target.UniqueId ?? string.Empty;
        string elementId = target.ElementId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(int)target.Kind}:{uniqueId.Length}:{uniqueId}:{elementId}");
    }

    private static string HashIdentity(params string[] parts)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (string part in parts)
            {
                writer.Write(part);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
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
