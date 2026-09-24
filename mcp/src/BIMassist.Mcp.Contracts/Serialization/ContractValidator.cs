using System.Text;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Sessions;

namespace BIMassist.Mcp.Contracts.Serialization;

public static class ContractValidator
{
    public static void Validate<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (value)
        {
            case BridgeRequest request:
                Validate(request);
                break;
            case BridgeResponse response:
                Validate(response);
                break;
            case ApplyChangePlanRequest apply:
                Validate(apply);
                break;
            case ApprovalMetadata approval:
                ValidateApproval(approval);
                break;
            case BridgeError error:
                Validate(error);
                break;
            case BridgeWarning warning:
                Validate(warning);
                break;
            case ChangePlan plan:
                Validate(plan);
                break;
            case ParameterValue parameterValue:
                Validate(parameterValue);
                break;
            case ElementReferenceValue referenceValue:
                Validate(referenceValue);
                break;
            case ParameterIdentity identity:
                Validate(identity);
                break;
            case ParameterTarget target:
                Validate(target);
                break;
            case ChangeOperation operation:
                Validate(operation);
                break;
            case ParameterBindingState bindingState:
                ValidateBinding(bindingState, allowNone: true);
                break;
            case SharedParameterDefinitionState definitionState:
                ValidateDefinition(definitionState);
                break;
            case PageRequest pageRequest:
                Validate(pageRequest);
                break;
            case IPageResult pageResult:
                Validate(pageResult);
                break;
            case ListFamiliesRequest listFamilies:
                Validate(listFamilies);
                break;
            case GetFamilyMetadataRequest getFamily:
                Validate(getFamily);
                break;
            case FamilySummary family:
                Validate(family);
                break;
            case FamilyTypeSummary familyType:
                Validate(familyType);
                break;
            case FamilyMetadata familyMetadata:
                Validate(familyMetadata);
                break;
            case ListParametersRequest listParameters:
                Validate(listParameters);
                break;
            case ParameterSummary parameterSummary:
                Validate(parameterSummary);
                break;
            case GetParameterMetadataRequest getParameterMetadata:
                Validate(getParameterMetadata);
                break;
            case ListSharedDefinitionsRequest listSharedDefinitions:
                Validate(listSharedDefinitions);
                break;
            case SharedDefinitionDescriptor sharedDefinition:
                Validate(sharedDefinition);
                break;
            case ListProjectBindingsRequest listProjectBindings:
                Validate(listProjectBindings);
                break;
            case ProjectBindingDescriptor projectBinding:
                Validate(projectBinding);
                break;
            case GetParameterValuesRequest getParameterValues:
                Validate(getParameterValues);
                break;
            case ParameterValueEntry parameterValueEntry:
                Validate(parameterValueEntry);
                break;
            case ExportMetadataSnapshotRequest exportSnapshot:
                Validate(exportSnapshot);
                break;
            case MetadataSnapshotSelection snapshotSelection:
                Validate(snapshotSelection);
                break;
            case MetadataSnapshotItem snapshotItem:
                Validate(snapshotItem);
                break;
            case FamilyTypeSnapshotItem familyTypeSnapshot:
                Validate(familyTypeSnapshot);
                break;
            case MetadataSnapshotPage snapshotPage:
                Validate(snapshotPage);
                break;
            case SessionDescriptor session:
                Validate(session);
                break;
            case DocumentDescriptor document:
                Validate(document);
                break;
            case ParameterMetadata metadata:
                Validate(metadata);
                break;
            case ParameterDefinitionMetadata definitionMetadata:
                Validate(definitionMetadata);
                break;
            case ParameterBindingMetadata bindingMetadata:
                Validate(bindingMetadata);
                break;
            case ParameterUnitMetadata unitMetadata:
                Validate(unitMetadata);
                break;
            case ParameterContextMetadata contextMetadata:
                Validate(contextMetadata);
                break;
            case ParameterWorksharingMetadata worksharingMetadata:
                Validate(worksharingMetadata);
                break;
        }
    }

    private static void Validate(ListFamiliesRequest request)
    {
        if (request.Page is null)
        {
            throw new JsonException("Family list requests require pagination.");
        }

        Validate(request.Page);
        ValidateOptionalText(request.NameContains, nameof(request.NameContains), ContractLimits.MaximumTextLength);
    }

    private static void Validate(GetFamilyMetadataRequest request)
    {
        RequireText(request.FamilyUniqueId, nameof(request.FamilyUniqueId), ContractLimits.MaximumIdentifierLength);
        if (request.Page is null)
        {
            throw new JsonException("Family metadata requests require type pagination.");
        }

        Validate(request.Page);
    }

    private static void Validate(FamilySummary family)
    {
        RequireText(family.UniqueId, nameof(family.UniqueId), ContractLimits.MaximumIdentifierLength);
        RequireText(family.Name, nameof(family.Name));
        ValidateOptionalText(family.CategoryId, nameof(family.CategoryId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(family.CategoryName, nameof(family.CategoryName), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        if (family.ElementId <= 0 || family.TypeCount < 0)
        {
            throw new JsonException("Family IDs must be positive and type counts cannot be negative.");
        }
    }

    private static void Validate(FamilyTypeSummary familyType)
    {
        RequireText(familyType.UniqueId, nameof(familyType.UniqueId), ContractLimits.MaximumIdentifierLength);
        RequireText(familyType.Name, nameof(familyType.Name));
        if (familyType.ElementId <= 0)
        {
            throw new JsonException("Family type element IDs must be positive.");
        }
    }

    private static void Validate(FamilyMetadata metadata)
    {
        if (metadata.Family is null || metadata.Types is null)
        {
            throw new JsonException("Family metadata requires a family and a type array.");
        }

        Validate(metadata.Family);
        Validate(metadata.Types);
        if (metadata.Types.TotalCount != metadata.Family.TypeCount)
        {
            throw new JsonException("Family metadata total type count must match the family summary.");
        }

        if (metadata.Types.Items.Select(type => type.UniqueId).Distinct(StringComparer.Ordinal).Count() != metadata.Types.Items.Count ||
            metadata.Types.Items.Select(type => type.ElementId).Distinct().Count() != metadata.Types.Items.Count)
        {
            throw new JsonException("Family metadata type identities must be unique within a page.");
        }
    }

    private static void Validate(ListParametersRequest request)
    {
        if (request.Page is null || request.Target is null)
        {
            throw new JsonException("Parameter list requests require pagination and a target.");
        }

        Validate(request.Page);
        Validate(request.Target);
        ValidateOptionalText(request.NameContains, nameof(request.NameContains), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        if (request.StorageType is { } storageType)
        {
            ValidateDefinedEnum(storageType, nameof(request.StorageType));
        }
        if (request.BindingKind is { } bindingKind)
        {
            ValidateDefinedEnum(bindingKind, nameof(request.BindingKind));
        }
    }

    private static void Validate(ParameterSummary summary)
    {
        if (summary.Identity is null)
        {
            throw new JsonException("Parameter summaries require an identity.");
        }

        Validate(summary.Identity);
        ValidateDefinedEnum(summary.StorageType, nameof(summary.StorageType));
        ValidateDefinedEnum(summary.BindingKind, nameof(summary.BindingKind));
        ValidateOptionalText(summary.BlockedReason, nameof(summary.BlockedReason), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        if (!summary.IsReadOnly && summary.BlockedReason is not null)
        {
            throw new JsonException("Writable parameter summaries cannot contain a blocking reason.");
        }
    }

    private static void Validate(GetParameterMetadataRequest request)
    {
        if (request.Target is null || request.Parameter is null)
        {
            throw new JsonException("Parameter metadata requests require a target and parameter identity.");
        }

        Validate(request.Target);
        Validate(request.Parameter);
    }

    private static void Validate(ListSharedDefinitionsRequest request)
    {
        if (request.Page is null)
        {
            throw new JsonException("Shared-definition list requests require pagination.");
        }

        Validate(request.Page);
        ValidateOptionalText(request.NameContains, nameof(request.NameContains), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        ValidateOptionalText(request.GroupName, nameof(request.GroupName), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        if (request.SharedGuid == Guid.Empty)
        {
            throw new JsonException("Shared-definition GUID filters must be non-empty.");
        }
    }

    private static void Validate(SharedDefinitionDescriptor definition)
    {
        if (definition.SharedGuid == Guid.Empty)
        {
            throw new JsonException("Shared definitions require a non-empty GUID.");
        }

        RequireText(definition.Name, nameof(definition.Name));
        RequireText(definition.DataTypeId, nameof(definition.DataTypeId), ContractLimits.MaximumIdentifierLength);
        RequireText(definition.GroupName, nameof(definition.GroupName));
        ValidateOptionalText(definition.Description, nameof(definition.Description), ContractLimits.MaximumTextLength);
    }

    private static void Validate(ListProjectBindingsRequest request)
    {
        if (request.Page is null)
        {
            throw new JsonException("Project-binding list requests require pagination.");
        }

        Validate(request.Page);
        ValidateOptionalText(request.NameContains, nameof(request.NameContains), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        ValidateOptionalText(request.CategoryId, nameof(request.CategoryId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        if (request.BindingKind is { } bindingKind &&
            bindingKind is not ParameterBindingKind.ProjectInstance and not ParameterBindingKind.ProjectType)
        {
            throw new JsonException("Project-binding filters must use a project instance/type kind.");
        }
    }

    private static void Validate(ProjectBindingDescriptor descriptor)
    {
        if (descriptor.Identity is null || descriptor.Definition is null || descriptor.Binding is null)
        {
            throw new JsonException("Project-binding descriptors require identity, definition, and binding metadata.");
        }

        Validate(descriptor.Identity);
        Validate(descriptor.Definition);
        Validate(descriptor.Binding);
        if (descriptor.Binding.Kind is not ParameterBindingKind.ProjectInstance and not ParameterBindingKind.ProjectType)
        {
            throw new JsonException("Project-binding descriptors require a project instance/type binding.");
        }
        if (!descriptor.Binding.CategoryIds.SequenceEqual(descriptor.Binding.CategoryIds.OrderBy(value => value, StringComparer.Ordinal)))
        {
            throw new JsonException("Project-binding categories must be in ordinal order.");
        }
    }

    private static void Validate(GetParameterValuesRequest request)
    {
        if (request.Page is null || request.Target is null)
        {
            throw new JsonException("Parameter-value requests require pagination and a target.");
        }

        Validate(request.Page);
        Validate(request.Target);
        if (request.Parameters is null)
        {
            return;
        }
        if (request.Parameters.Count == 0 || request.Parameters.Count > ContractLimits.MaximumParameterSelectors ||
            request.Parameters.Any(parameter => parameter is null))
        {
            throw new JsonException("Parameter selectors must be omitted or contain a bounded non-empty list.");
        }

        foreach (ParameterIdentity parameter in request.Parameters)
        {
            Validate(parameter);
        }
        if (request.Parameters.Select(parameter => parameter.StableId).Distinct(StringComparer.Ordinal).Count() != request.Parameters.Count)
        {
            throw new JsonException("Parameter selectors must have unique stable identities.");
        }
    }

    private static void Validate(ParameterValueEntry entry)
    {
        if (entry.Target is null || entry.Parameter is null || entry.Value is null)
        {
            throw new JsonException("Parameter-value entries require target, parameter, and value data.");
        }

        Validate(entry.Target);
        Validate(entry.Parameter);
        Validate(entry.Value);
    }

    private static void Validate(ExportMetadataSnapshotRequest request)
    {
        if (request.Page is null || request.Selection is null)
        {
            throw new JsonException("Metadata-snapshot requests require pagination and a selection.");
        }

        Validate(request.Page);
        Validate(request.Selection);
    }

    private static void Validate(MetadataSnapshotSelection selection)
    {
        if (selection.ParameterTargets is null ||
            selection.ParameterTargets.Count > ContractLimits.MaximumParameterSelectors ||
            selection.ParameterTargets.Any(target => target is null))
        {
            throw new JsonException("Metadata-snapshot parameter targets must be a bounded non-null array.");
        }

        foreach (ParameterTarget target in selection.ParameterTargets)
        {
            Validate(target);
        }

        if (!selection.IncludeFamilies && !selection.IncludeSharedDefinitions &&
            !selection.IncludeProjectBindings && selection.ParameterTargets.Count == 0)
        {
            throw new JsonException("Metadata snapshots require at least one selected data source.");
        }
        if (selection.IncludeParameterValues && selection.ParameterTargets.Count == 0)
        {
            throw new JsonException("Metadata snapshot values require at least one parameter target.");
        }
    }

    private static void Validate(MetadataSnapshotItem item)
    {
        ValidateDefinedEnum(item.Kind, nameof(item.Kind));
        int payloadCount = (item.Family is null ? 0 : 1) +
                           (item.FamilyType is null ? 0 : 1) +
                           (item.ParameterMetadata is null ? 0 : 1) +
                           (item.SharedDefinition is null ? 0 : 1) +
                           (item.ProjectBinding is null ? 0 : 1) +
                           (item.ParameterValue is null ? 0 : 1);
        if (payloadCount != 1)
        {
            throw new JsonException("Metadata snapshot items require exactly one payload.");
        }

        bool valid = item.Kind switch
        {
            MetadataSnapshotItemKind.Family when item.Family is not null => ValidateAndReturn(item.Family),
            MetadataSnapshotItemKind.FamilyType when item.FamilyType is not null => ValidateAndReturn(item.FamilyType),
            MetadataSnapshotItemKind.ParameterMetadata when item.ParameterMetadata is not null => ValidateAndReturn(item.ParameterMetadata),
            MetadataSnapshotItemKind.SharedDefinition when item.SharedDefinition is not null => ValidateAndReturn(item.SharedDefinition),
            MetadataSnapshotItemKind.ProjectBinding when item.ProjectBinding is not null => ValidateAndReturn(item.ProjectBinding),
            MetadataSnapshotItemKind.ParameterValue when item.ParameterValue is not null => ValidateAndReturn(item.ParameterValue),
            _ => false
        };
        if (!valid)
        {
            throw new JsonException("Metadata snapshot item payload does not match its discriminator.");
        }
    }

    private static bool ValidateAndReturn<T>(T value)
    {
        Validate(value);
        return true;
    }

    private static void Validate(FamilyTypeSnapshotItem item)
    {
        RequireText(item.FamilyUniqueId, nameof(item.FamilyUniqueId), ContractLimits.MaximumIdentifierLength);
        if (item.Type is null)
        {
            throw new JsonException("Family-type snapshot items require type metadata.");
        }
        Validate(item.Type);
    }

    private static void Validate(MetadataSnapshotPage snapshot)
    {
        RequireText(snapshot.SnapshotId, nameof(snapshot.SnapshotId), ContractLimits.MaximumIdentifierLength);
        RequireText(snapshot.DocumentKey, nameof(snapshot.DocumentKey), ContractLimits.MaximumDocumentKeyLength);
        RequireText(snapshot.DocumentRevision, nameof(snapshot.DocumentRevision), ContractLimits.MaximumRevisionLength);
        if (snapshot.CreatedAtUtc == default || snapshot.CreatedAtUtc.Offset != TimeSpan.Zero ||
            snapshot.ExpiresAtUtc.Offset != TimeSpan.Zero || snapshot.ExpiresAtUtc <= snapshot.CreatedAtUtc)
        {
            throw new JsonException("Metadata snapshots require valid UTC creation and expiration times.");
        }
        if (snapshot.Page is null)
        {
            throw new JsonException("Metadata snapshots require a page result.");
        }
        Validate(snapshot.Page);
    }

    private static void Validate(ParameterMetadata metadata)
    {
        if (metadata.Identity is null || metadata.Definition is null || metadata.Binding is null ||
            metadata.Context is null || metadata.Worksharing is null)
        {
            throw new JsonException("Parameter metadata requires identity, definition, binding, context, and worksharing data.");
        }

        Validate(metadata.Identity);
        ValidateDefinedEnum(metadata.StorageType, nameof(metadata.StorageType));
        Validate(metadata.Definition);
        Validate(metadata.Binding);
        Validate(metadata.Context);
        if (metadata.Unit is not null)
        {
            Validate(metadata.Unit);
        }
        Validate(metadata.Worksharing);
        ValidateOptionalText(metadata.Formula, nameof(metadata.Formula), ContractLimits.MaximumValueTextLength, requireNonWhitespace: true);
        ValidateOptionalText(metadata.BlockedReason, nameof(metadata.BlockedReason), ContractLimits.MaximumTextLength, requireNonWhitespace: true);

        if (metadata.Formula is not null &&
            (!metadata.IsReadOnly || metadata.BlockedReason is null))
        {
            throw new JsonException("Formula-controlled parameter metadata must be read-only and blocked.");
        }
    }

    private static void Validate(ParameterDefinitionMetadata definition)
    {
        RequireText(definition.Name, nameof(definition.Name));
        RequireText(definition.DataTypeId, nameof(definition.DataTypeId), ContractLimits.MaximumIdentifierLength);
        ValidateOptionalText(definition.GroupTypeId, nameof(definition.GroupTypeId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(definition.Description, nameof(definition.Description), ContractLimits.MaximumTextLength);
    }

    private static void Validate(ParameterBindingMetadata binding)
    {
        ValidateDefinedEnum(binding.Kind, nameof(binding.Kind));
        RequireText(binding.Origin, nameof(binding.Origin), ContractLimits.MaximumIdentifierLength);
        ValidateOptionalText(binding.FamilyCategoryId, nameof(binding.FamilyCategoryId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        if (binding.CategoryIds is null ||
            binding.CategoryIds.Count > ContractLimits.MaximumBindingCategories ||
            binding.CategoryIds.Any(categoryId =>
                string.IsNullOrWhiteSpace(categoryId) ||
                categoryId.Length > ContractLimits.MaximumIdentifierLength) ||
            binding.CategoryIds.Distinct(StringComparer.Ordinal).Count() != binding.CategoryIds.Count)
        {
            throw new JsonException("Parameter binding category identifiers must be unique, bounded non-empty strings.");
        }

        if (binding.Kind is ParameterBindingKind.ProjectInstance or ParameterBindingKind.ProjectType &&
            binding.CategoryIds.Count == 0)
        {
            throw new JsonException("Project parameter bindings require at least one category.");
        }

        if (binding.Kind == ParameterBindingKind.None && binding.CategoryIds.Count != 0)
        {
            throw new JsonException("Unbound parameters cannot contain categories.");
        }

        if (binding.Kind is ParameterBindingKind.FamilyInstance or ParameterBindingKind.FamilyType)
        {
            RequireText(binding.FamilyCategoryId, nameof(binding.FamilyCategoryId), ContractLimits.MaximumIdentifierLength);
        }
    }

    private static void Validate(ParameterUnitMetadata unit)
    {
        RequireText(unit.SpecTypeId, nameof(unit.SpecTypeId), ContractLimits.MaximumIdentifierLength);
        if (unit.UnitTypeId is not null)
        {
            RequireText(unit.UnitTypeId, nameof(unit.UnitTypeId), ContractLimits.MaximumIdentifierLength);
        }
        if (unit.Format is not null)
        {
            RequireText(unit.Format, nameof(unit.Format));
        }
    }

    private static void Validate(ParameterContextMetadata context)
    {
        RequireText(context.DocumentKey, nameof(context.DocumentKey), ContractLimits.MaximumDocumentKeyLength);
        if (context.Target is null)
        {
            throw new JsonException("Parameter contexts require an explicit target.");
        }
        Validate(context.Target);
        if (context.IsFamilyParameter &&
            context.Target.Kind is not ParameterTargetKind.Family and not ParameterTargetKind.FamilyType)
        {
            throw new JsonException("Family-parameter contexts require a family or family-type target.");
        }
    }

    private static void Validate(ParameterWorksharingMetadata worksharing)
    {
        ValidateOptionalText(worksharing.Owner, nameof(worksharing.Owner), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        if (worksharing.IsOwnedByCurrentUser && !worksharing.IsWorkshared)
        {
            throw new JsonException("Only workshared parameters can be owned by the current user.");
        }

        if (worksharing.IsWorkshared && !worksharing.IsEditable)
        {
            RequireText(worksharing.Owner, nameof(worksharing.Owner));
        }
    }

    private static void Validate(SessionDescriptor session)
    {
        RequireText(session.SessionId, nameof(session.SessionId), ContractLimits.MaximumIdentifierLength);
        RequireText(session.BridgeVersion, nameof(session.BridgeVersion), ContractLimits.MaximumIdentifierLength);
        if (session.RevitProcessId <= 0 || session.RevitMajor <= 0)
        {
            throw new JsonException("Session process and Revit version must be positive.");
        }

        if (session.StartedAtUtc == default || session.StartedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new JsonException("Session start time must be a non-default UTC timestamp.");
        }

        if (!ProtocolVersions.IsCompatible(session.ProtocolVersion, session.SchemaVersion))
        {
            throw new JsonException(BridgeErrorCodes.ProtocolVersionMismatch);
        }

        if (session.Documents is null)
        {
            throw new JsonException("Session documents must be an array.");
        }

        if (session.Documents.Count > ContractLimits.MaximumSessionDocuments)
        {
            throw new JsonException("Session documents exceed the configured limit.");
        }

        foreach (DocumentDescriptor document in session.Documents)
        {
            if (document is null)
            {
                throw new JsonException("Session documents cannot contain null entries.");
            }

            Validate(document);
        }
    }

    private static void Validate(DocumentDescriptor document)
    {
        RequireText(document.DocumentKey, nameof(document.DocumentKey), ContractLimits.MaximumDocumentKeyLength);
        RequireText(document.Title, nameof(document.Title));
        RequireText(document.Revision, nameof(document.Revision), ContractLimits.MaximumRevisionLength);
        ValidateOptionalText(document.Path, nameof(document.Path), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        ValidateOptionalText(
            document.ActiveViewUniqueId,
            nameof(document.ActiveViewUniqueId),
            ContractLimits.MaximumIdentifierLength,
            requireNonWhitespace: true);
        ValidateDefinedEnum(document.PathStatus, nameof(document.PathStatus));
    }

    private static void Validate(PageRequest request)
    {
        if (request.PageSize is < 1 or > ContractLimits.MaximumPageSize)
        {
            throw new JsonException($"Page size must be between 1 and {ContractLimits.MaximumPageSize}.");
        }

        ValidateOptionalText(request.Cursor, nameof(request.Cursor), ContractLimits.MaximumCursorLength, requireNonWhitespace: true);
    }

    private static void Validate(IPageResult result)
    {
        if (result.ItemCount < 0)
        {
            throw new JsonException("Page items must be an array.");
        }

        if (result.ItemCount > ContractLimits.MaximumPageSize)
        {
            throw new JsonException("Page result exceeds the configured item limit.");
        }

        if (result.TotalCount < 0)
        {
            throw new JsonException("Total count cannot be negative.");
        }

        ValidateOptionalText(
            result.NextCursorForValidation,
            "NextCursor",
            ContractLimits.MaximumCursorLength,
            requireNonWhitespace: true);

        foreach (object? item in result.ItemsForValidation)
        {
            if (item is null)
            {
                throw new JsonException("Page items cannot contain null entries.");
            }

            if (item is string text)
            {
                RequireText(text, "Page item");
            }
            else
            {
                Validate(item);
            }
        }
    }

    private static void Validate(BridgeRequest request)
    {
        RequireText(request.ProtocolVersion, nameof(request.ProtocolVersion), ContractLimits.MaximumIdentifierLength);
        RequireText(request.SchemaVersion, nameof(request.SchemaVersion), ContractLimits.MaximumIdentifierLength);
        RequireText(request.RequestId, nameof(request.RequestId), ContractLimits.MaximumIdentifierLength);
        RequireText(request.Operation, nameof(request.Operation), ContractLimits.MaximumIdentifierLength);
        ValidateOptionalText(request.SessionId, nameof(request.SessionId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(request.DocumentKey, nameof(request.DocumentKey), ContractLimits.MaximumDocumentKeyLength, requireNonWhitespace: true);
        ValidateOptionalText(request.ExpectedRevision, nameof(request.ExpectedRevision), ContractLimits.MaximumRevisionLength, requireNonWhitespace: true);
        ValidateOptionalText(request.IdempotencyKey, nameof(request.IdempotencyKey), ContractLimits.MaximumRevisionLength, requireNonWhitespace: true);

        if (!ProtocolVersions.IsCompatible(request.ProtocolVersion, request.SchemaVersion))
        {
            throw new JsonException(BridgeErrorCodes.ProtocolVersionMismatch);
        }

        if (!BridgeOperations.All.Contains(request.Operation))
        {
            throw new JsonException($"Unknown bridge operation '{request.Operation}'.");
        }

        if (request.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Request payload must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(request.Payload.GetRawText()) > ContractLimits.MaximumPayloadBytes)
        {
            throw new JsonException("Request payload exceeds the configured limit.");
        }

        if (BridgeOperations.RequiresSession.Contains(request.Operation))
        {
            RequireText(request.SessionId, nameof(request.SessionId), ContractLimits.MaximumIdentifierLength);
        }

        if (BridgeOperations.RequiresDocument.Contains(request.Operation))
        {
            RequireText(request.DocumentKey, nameof(request.DocumentKey), ContractLimits.MaximumDocumentKeyLength);
        }

        if (BridgeOperations.Changes.Contains(request.Operation))
        {
            RequireText(request.ExpectedRevision, nameof(request.ExpectedRevision), ContractLimits.MaximumRevisionLength);
            RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey), ContractLimits.MaximumRevisionLength);
        }

        switch (request.Operation)
        {
            case BridgeOperations.ListFamilies:
                ValidatePayload<ListFamiliesRequest>(request.Payload);
                break;
            case BridgeOperations.GetFamilyMetadata:
                ValidatePayload<GetFamilyMetadataRequest>(request.Payload);
                break;
            case BridgeOperations.ListParameters:
                ValidatePayload<ListParametersRequest>(request.Payload);
                break;
            case BridgeOperations.GetParameterMetadata:
                ValidatePayload<GetParameterMetadataRequest>(request.Payload);
                break;
            case BridgeOperations.ListSharedDefinitions:
                ValidatePayload<ListSharedDefinitionsRequest>(request.Payload);
                break;
            case BridgeOperations.ListProjectBindings:
                ValidatePayload<ListProjectBindingsRequest>(request.Payload);
                break;
            case BridgeOperations.GetParameterValues:
                ValidatePayload<GetParameterValuesRequest>(request.Payload);
                break;
            case BridgeOperations.ExportMetadataSnapshot:
                ValidatePayload<ExportMetadataSnapshotRequest>(request.Payload);
                break;
            case BridgeOperations.ApplyChangePlan:
                ApplyChangePlanRequest apply = DeserializePayload<ApplyChangePlanRequest>(request.Payload);
                Validate(apply);

                if (!string.Equals(request.ExpectedRevision, apply.ExpectedRevision, StringComparison.Ordinal) ||
                    !string.Equals(request.IdempotencyKey, apply.IdempotencyKey, StringComparison.Ordinal))
                {
                    throw new JsonException("Apply transport metadata must match its payload.");
                }
                break;
        }
    }

    private static void ValidatePayload<T>(JsonElement payload)
    {
        T value = DeserializePayload<T>(payload);
        Validate(value);
    }

    private static T DeserializePayload<T>(JsonElement payload) =>
        JsonSerializer.Deserialize<T>(payload.GetRawText(), ContractJson.Options)
        ?? throw new JsonException($"{typeof(T).Name} payload is required.");

    private static void Validate(BridgeResponse response)
    {
        RequireText(response.RequestId, nameof(response.RequestId), ContractLimits.MaximumIdentifierLength);
        ValidateOptionalText(
            response.DocumentRevision,
            nameof(response.DocumentRevision),
            ContractLimits.MaximumRevisionLength,
            requireNonWhitespace: true);
        if (response.Warnings is null)
        {
            throw new JsonException("Warnings must be an array.");
        }

        if (response.Warnings.Count > ContractLimits.MaximumWarnings)
        {
            throw new JsonException("Response warnings exceed the configured limit.");
        }

        if (response.DurationMs < 0)
        {
            throw new JsonException("Response duration cannot be negative.");
        }

        foreach (BridgeWarning warning in response.Warnings)
        {
            if (warning is null)
            {
                throw new JsonException("Warnings cannot contain null entries.");
            }

            Validate(warning);
        }

        bool hasResult = response.Result is { } result &&
                         result.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        if (response.Success && !hasResult)
        {
            throw new JsonException("Successful responses require a non-null result.");
        }

        if (!response.Success && hasResult)
        {
            throw new JsonException("Failed responses cannot contain a result.");
        }

        if (response.Success && response.Error is not null)
        {
            throw new JsonException("Successful responses cannot contain an error.");
        }

        if (!response.Success && response.Error is null)
        {
            throw new JsonException("Failed responses require error details.");
        }

        if (response.Error is not null)
        {
            Validate(response.Error);
        }
    }

    private static void Validate(BridgeWarning warning)
    {
        RequireText(warning.Code, nameof(warning.Code), ContractLimits.MaximumIdentifierLength);
        RequireText(warning.Message, nameof(warning.Message));
    }

    private static void Validate(BridgeError error)
    {
        RequireText(error.Code, nameof(error.Code), ContractLimits.MaximumIdentifierLength);
        RequireText(error.Message, nameof(error.Message));
        if (error.Details is null)
        {
            return;
        }

        if (error.Details.Count > ContractLimits.MaximumErrorDetails ||
            error.Details.Any(detail =>
                string.IsNullOrWhiteSpace(detail.Key) ||
                detail.Key.Length > ContractLimits.MaximumIdentifierLength ||
                detail.Value is null ||
                detail.Value.Length > ContractLimits.MaximumTextLength))
        {
            throw new JsonException("Error details contain invalid entries or exceed the configured limit.");
        }
    }

    private static void Validate(ApplyChangePlanRequest request)
    {
        RequireText(request.PlanId, nameof(request.PlanId), ContractLimits.MaximumIdentifierLength);
        RequireHash(request.PlanHash, nameof(request.PlanHash));
        RequireText(request.ExpectedRevision, nameof(request.ExpectedRevision), ContractLimits.MaximumRevisionLength);
        RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey), ContractLimits.MaximumRevisionLength);

        if (request.Approval is null)
        {
            throw new JsonException("Explicit approval metadata is required.");
        }

        ValidateApproval(request.Approval);
    }

    private static void ValidateApproval(ApprovalMetadata approval)
    {
        RequireText(approval.ApprovedBy, nameof(approval.ApprovedBy), ContractLimits.MaximumRevisionLength);
        RequireText(approval.Source, nameof(approval.Source), ContractLimits.MaximumIdentifierLength);
        if (approval.ApprovedAtUtc == default || approval.ApprovedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new JsonException("Approval time must be a non-default UTC timestamp.");
        }
    }

    private static void Validate(ChangePlan plan)
    {
        RequireText(plan.PlanId, nameof(plan.PlanId), ContractLimits.MaximumIdentifierLength);
        RequireText(plan.SessionId, nameof(plan.SessionId), ContractLimits.MaximumIdentifierLength);
        RequireText(plan.DocumentKey, nameof(plan.DocumentKey), ContractLimits.MaximumDocumentKeyLength);
        RequireText(plan.ExpectedRevision, nameof(plan.ExpectedRevision), ContractLimits.MaximumRevisionLength);

        if (plan.Operations is null || plan.Operations.Count == 0)
        {
            throw new JsonException("A change plan requires at least one operation.");
        }

        if (plan.Operations.Count > ContractLimits.MaximumOperationsPerPlan)
        {
            throw new JsonException("The change plan exceeds the operation limit.");
        }

        if (plan.Operations.Any(operation => operation is null) ||
            plan.Operations.Select(operation => operation!.OperationId)
                .Distinct(StringComparer.Ordinal).Count() != plan.Operations.Count)
        {
            throw new JsonException("Change-plan operation identifiers must be unique and non-null.");
        }

        if (plan.Warnings is null)
        {
            throw new JsonException("Warnings must be an array.");
        }

        if (plan.Warnings.Count > ContractLimits.MaximumWarnings)
        {
            throw new JsonException("Plan warnings exceed the configured limit.");
        }

        foreach (BridgeWarning warning in plan.Warnings)
        {
            if (warning is null)
            {
                throw new JsonException("Plan warnings cannot contain null entries.");
            }
            Validate(warning);
        }

        RequireHash(plan.PlanHash, nameof(plan.PlanHash));
        if (!string.Equals(plan.PlanHash, ChangePlanHasher.ComputeHash(plan), StringComparison.Ordinal))
        {
            throw new JsonException(BridgeErrorCodes.PlanHashMismatch);
        }

        if (plan.ExpiresAtUtc <= plan.CreatedAtUtc)
        {
            throw new JsonException("The plan expiration must be later than its creation time.");
        }

        foreach (ChangeOperation operation in plan.Operations)
        {
            if (operation is null)
            {
                throw new JsonException("Operations cannot contain null entries.");
            }

            Validate(operation);
        }
    }

    private static void Validate(ChangeOperation operation)
    {
        ValidateDefinedEnum(operation.Kind, nameof(operation.Kind));
        RequireText(operation.OperationId, nameof(operation.OperationId), ContractLimits.MaximumIdentifierLength);
        if (operation.Options is null)
        {
            throw new JsonException("Operation options must be an object.");
        }

        if (operation.Options.Count > ContractLimits.MaximumOperationOptions ||
            operation.Options.Any(option =>
                string.IsNullOrWhiteSpace(option.Key) ||
                option.Key.Length > ContractLimits.MaximumIdentifierLength ||
                option.Value is null ||
                option.Value.Length > ContractLimits.MaximumTextLength))
        {
            throw new JsonException("Operation options contain invalid entries or exceed the configured limit.");
        }

        if (operation.Target is null || operation.Parameter is null)
        {
            throw new JsonException("Every operation requires a target and parameter identity.");
        }

        Validate(operation.Target);
        Validate(operation.Parameter);

        switch (operation.Kind)
        {
            case ChangeOperationKind.SetParameterValue:
                if (operation.Before is null || operation.After is null)
                {
                    throw new JsonException("Set-parameter operations require before and after values.");
                }
                if (operation.BeforeBinding is not null || operation.AfterBinding is not null ||
                    operation.BeforeDefinition is not null || operation.AfterDefinition is not null)
                {
                    throw new JsonException("Set-parameter operations cannot contain binding or definition states.");
                }
                Validate(operation.Before);
                Validate(operation.After);
                if (operation.After.IsReadOnly)
                {
                    throw new JsonException("Set-parameter operations cannot target a read-only after value.");
                }
                if (operation.Before.HasValue && operation.Before.Kind != operation.After.Kind)
                {
                    throw new JsonException("Set-parameter before and after value kinds must match.");
                }
                break;
            case ChangeOperationKind.AddSharedParameterToFamily:
                if (operation.AfterDefinition is null)
                {
                    throw new JsonException("Add-shared-parameter operations require an after-definition state.");
                }
                if (operation.Target.Kind != ParameterTargetKind.Family ||
                    operation.Parameter.Kind != ParameterIdentityKind.SharedGuid ||
                    operation.Before is not null || operation.After is not null ||
                    operation.BeforeBinding is not null || operation.AfterBinding is not null)
                {
                    throw new JsonException("Add-shared-parameter operations contain incompatible state.");
                }
                ValidateDefinition(operation.AfterDefinition);
                if (operation.BeforeDefinition is not null)
                {
                    ValidateDefinition(operation.BeforeDefinition);
                }
                if (operation.Parameter.SharedGuid != operation.AfterDefinition.SharedGuid)
                {
                    throw new JsonException("Shared parameter identity and definition GUIDs must match.");
                }
                break;
            case ChangeOperationKind.BindSharedParameter:
                if (operation.AfterBinding is null)
                {
                    throw new JsonException("Bind-shared-parameter operations require an after-binding state.");
                }
                if (operation.Target.Kind != ParameterTargetKind.Document ||
                    operation.Parameter.Kind != ParameterIdentityKind.SharedGuid ||
                    operation.Before is not null || operation.After is not null ||
                    operation.BeforeDefinition is not null || operation.AfterDefinition is not null)
                {
                    throw new JsonException("Bind-shared-parameter operations contain incompatible state.");
                }
                ValidateBinding(operation.AfterBinding);
                if (operation.BeforeBinding is not null)
                {
                    ValidateBinding(operation.BeforeBinding, allowNone: true);
                }
                break;
        }
    }

    private static void ValidateBinding(ParameterBindingState binding, bool allowNone = false)
    {
        ValidateDefinedEnum(binding.Kind, nameof(binding.Kind));
        if ((!allowNone && binding.Kind is not ParameterBindingKind.ProjectInstance and not ParameterBindingKind.ProjectType) ||
            (allowNone && binding.Kind is not ParameterBindingKind.None and not ParameterBindingKind.ProjectInstance and not ParameterBindingKind.ProjectType))
        {
            throw new JsonException("Project bindings require a project instance/type binding kind.");
        }

        if (binding.CategoryIds is null ||
            binding.CategoryIds.Count > ContractLimits.MaximumBindingCategories ||
            (binding.Kind != ParameterBindingKind.None && binding.CategoryIds.Count == 0) ||
            (binding.Kind == ParameterBindingKind.None && binding.CategoryIds.Count != 0) ||
            binding.CategoryIds.Any(categoryId =>
                string.IsNullOrWhiteSpace(categoryId) ||
                categoryId.Length > ContractLimits.MaximumIdentifierLength) ||
            binding.CategoryIds.Distinct(StringComparer.Ordinal).Count() != binding.CategoryIds.Count)
        {
            throw new JsonException("Project binding categories are invalid, duplicated, or exceed the configured limit.");
        }

        if (binding.GroupTypeId is not null)
        {
            RequireText(binding.GroupTypeId, nameof(binding.GroupTypeId), ContractLimits.MaximumIdentifierLength);
        }
    }

    private static void ValidateDefinition(SharedParameterDefinitionState definition)
    {
        if (definition.SharedGuid == Guid.Empty)
        {
            throw new JsonException("Shared parameter definitions require a non-empty GUID.");
        }
        RequireText(definition.Name, nameof(definition.Name));
        RequireText(definition.DataTypeId, nameof(definition.DataTypeId), ContractLimits.MaximumIdentifierLength);
        RequireText(definition.GroupName, nameof(definition.GroupName));
    }

    private static void Validate(ParameterIdentity identity)
    {
        ValidateDefinedEnum(identity.Kind, nameof(identity.Kind));
        RequireText(identity.StableId, nameof(identity.StableId), ContractLimits.MaximumIdentifierLength);
        RequireText(identity.Name, nameof(identity.Name));
        ValidateOptionalText(identity.OwnerContext, nameof(identity.OwnerContext), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(identity.DataTypeId, nameof(identity.DataTypeId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);

        switch (identity.Kind)
        {
            case ParameterIdentityKind.SharedGuid:
                if (identity.SharedGuid is null || identity.SharedGuid == Guid.Empty ||
                    identity.BuiltInId is not null || identity.DefinitionId is not null)
                {
                    throw new JsonException("A shared parameter identity requires only a non-empty GUID identity.");
                }
                break;
            case ParameterIdentityKind.BuiltIn:
                if (identity.SharedGuid is not null || identity.BuiltInId is null or 0 || identity.DefinitionId is not null)
                {
                    throw new JsonException("Built-in parameter identities require an explicit API ID and cannot contain other identity fields.");
                }
                break;
            case ParameterIdentityKind.ParameterElement:
            case ParameterIdentityKind.FamilyDefinition:
                if (identity.SharedGuid is not null || identity.BuiltInId is not null || identity.DefinitionId is null or <= 0 ||
                    identity.OwnerContext is null || identity.DataTypeId is null)
                {
                    throw new JsonException("Fallback parameter identities require positive definition, owner, and data-type characteristics only.");
                }
                break;
        }
    }

    private static void Validate(ParameterTarget target)
    {
        ValidateDefinedEnum(target.Kind, nameof(target.Kind));
        ValidateOptionalText(target.UniqueId, nameof(target.UniqueId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(target.FamilyName, nameof(target.FamilyName), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        ValidateOptionalText(target.TypeName, nameof(target.TypeName), ContractLimits.MaximumTextLength, requireNonWhitespace: true);

        switch (target.Kind)
        {
            case ParameterTargetKind.Document
                when target.UniqueId is not null || target.ElementId is not null ||
                     target.FamilyName is not null || target.TypeName is not null:
                throw new JsonException("Document targets cannot contain element or family identifiers.");
            case ParameterTargetKind.Element or ParameterTargetKind.ElementType
                when (target.ElementId is not null && target.ElementId <= 0) ||
                     (target.UniqueId is null && target.ElementId is null) ||
                     target.FamilyName is not null || target.TypeName is not null:
                throw new JsonException("Element targets require a stable element identifier and cannot contain family identifiers.");
            case ParameterTargetKind.Family
                when target.UniqueId is null || target.ElementId is null or <= 0 ||
                     target.TypeName is not null:
                throw new JsonException("Family targets require stable family unique and element IDs; names are display-only.");
            case ParameterTargetKind.FamilyType
                when target.UniqueId is null || target.ElementId is null or <= 0:
                throw new JsonException("Family-type targets require stable symbol unique and element IDs; names are display-only.");
        }
    }

    private static void Validate(ParameterValue value)
    {
        ValidateDefinedEnum(value.Kind, nameof(value.Kind));
        ValidateOptionalText(value.StringValue, nameof(value.StringValue), ContractLimits.MaximumValueTextLength);
        ValidateOptionalText(value.DisplayValue, nameof(value.DisplayValue), ContractLimits.MaximumValueTextLength);
        ValidateOptionalText(value.SpecTypeId, nameof(value.SpecTypeId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(value.InputUnitTypeId, nameof(value.InputUnitTypeId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(value.Formula, nameof(value.Formula), ContractLimits.MaximumValueTextLength, requireNonWhitespace: true);
        ValidateOptionalText(value.BlockingReason, nameof(value.BlockingReason), ContractLimits.MaximumTextLength, requireNonWhitespace: true);
        if (value.Reference is not null)
        {
            Validate(value.Reference);
        }

        if (value.Formula is not null &&
            (!value.IsReadOnly || value.BlockingReason is null))
        {
            throw new JsonException("Formula-controlled values must be read-only and contain a blocking reason.");
        }

        if ((value.DoubleValue is { } doubleValue && !double.IsFinite(doubleValue)) ||
            (value.InternalDoubleValue is { } internalDoubleValue && !double.IsFinite(internalDoubleValue)))
        {
            throw new JsonException("Double parameter values must be finite.");
        }

        bool hasAnyPayload = value.StringValue is not null || value.IntegerValue is not null ||
                             value.DoubleValue is not null || value.InternalDoubleValue is not null ||
                             value.BooleanValue is not null || value.Reference is not null;
        if (!value.HasValue)
        {
            if (hasAnyPayload)
            {
                throw new JsonException("Values with hasValue=false must retain only their discriminator and contain no payload.");
            }
            return;
        }

        bool valid = value.Kind switch
        {
            ParameterValueKind.String => value.StringValue is not null && PayloadCount(value) == 1,
            ParameterValueKind.Integer => value.IntegerValue is not null && PayloadCount(value) == 1,
            ParameterValueKind.Double => value.DoubleValue is not null &&
                                         value.InternalDoubleValue is not null &&
                                         !string.IsNullOrWhiteSpace(value.SpecTypeId) &&
                                         !string.IsNullOrWhiteSpace(value.InputUnitTypeId) &&
                                         PayloadCount(value) == 1,
            ParameterValueKind.Boolean => value.BooleanValue is not null && PayloadCount(value) == 1,
            ParameterValueKind.ElementReference => ValidReference(value, ParameterReferenceKind.Element),
            ParameterValueKind.MaterialReference => ValidReference(value, ParameterReferenceKind.Material),
            ParameterValueKind.TypeReference => ValidReference(value, ParameterReferenceKind.Type),
            _ => false
        };
        if (!valid)
        {
            throw new JsonException("Parameter value payload does not match its discriminator.");
        }
    }

    private static void Validate(ElementReferenceValue reference)
    {
        ValidateDefinedEnum(reference.ReferenceKind, nameof(reference.ReferenceKind));
        ValidateOptionalText(reference.UniqueId, nameof(reference.UniqueId), ContractLimits.MaximumIdentifierLength, requireNonWhitespace: true);
        ValidateOptionalText(reference.Name, nameof(reference.Name), ContractLimits.MaximumTextLength);
        if (reference.ElementId < 0 || (reference.ElementId == 0 && reference.UniqueId is null))
        {
            throw new JsonException("Element references require a unique ID or positive element ID.");
        }
    }

    private static bool ValidReference(ParameterValue value, ParameterReferenceKind expectedKind) =>
        value.Reference is { } reference &&
        reference.ReferenceKind == expectedKind &&
        PayloadCount(value) == 1;

    private static int PayloadCount(ParameterValue value) =>
        (value.StringValue is null ? 0 : 1) +
        (value.IntegerValue is null ? 0 : 1) +
        (value.DoubleValue is null && value.InternalDoubleValue is null ? 0 : 1) +
        (value.BooleanValue is null ? 0 : 1) +
        (value.Reference is null ? 0 : 1);

    private static void ValidateDefinedEnum<TEnum>(TEnum value, string propertyName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException($"Unknown {propertyName} value.");
        }
    }

    private static void ValidateOptionalText(
        string? value,
        string propertyName,
        int maximumLength,
        bool requireNonWhitespace = false)
    {
        if (value is null)
        {
            return;
        }

        if (requireNonWhitespace && string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"{propertyName} must be a non-empty string when present.");
        }

        if (value.Length > maximumLength)
        {
            throw new JsonException($"{propertyName} exceeds the maximum length of {maximumLength} characters.");
        }
    }

    private static void RequireText(
        string? value,
        string propertyName,
        int maximumLength = ContractLimits.MaximumTextLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"{propertyName} must be a non-empty string.");
        }

        if (value.Length > maximumLength)
        {
            throw new JsonException($"{propertyName} exceeds the maximum length of {maximumLength} characters.");
        }
    }

    private static void RequireHash(string? value, string propertyName)
    {
        RequireText(value, propertyName);
        if (value!.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new JsonException($"{propertyName} must be a lowercase SHA-256 value.");
        }
    }
}
