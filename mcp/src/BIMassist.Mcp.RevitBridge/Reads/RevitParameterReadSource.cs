using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Sessions;
using ContractStorageType = BIMassist.Mcp.Contracts.Parameters.ParameterStorageType;
using RevitStorageType = Autodesk.Revit.DB.StorageType;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class RevitParameterReadSource : IRevitParameterReadSource
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    internal RevitParameterReadSource(
        UIApplication application,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public ParameterReadSnapshot ReadParameters(
        string sessionId,
        string documentKey,
        ParameterTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        SessionDescriptor session = _sessions.GetSnapshot();
        if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new ReadCursorException(BridgeErrorCodes.SessionNotFound);
        }

        var candidates = new List<DocumentCandidate>();
        foreach (Document openDocument in _application.Application.Documents)
        {
            candidates.Add(new DocumentCandidate(
                openDocument,
                RevitContextSnapshotProvider.CreateDocumentKey(openDocument, session.SessionId)));
        }

        DocumentCandidate candidate = ExactDocumentResolver.Resolve(candidates, documentKey, item => item.DocumentKey);
        Document document = candidate.Document;
        Element owner = ResolveTarget(document, target);
        IReadOnlyDictionary<long, ProjectBindingInfo> bindings = ReadProjectBindings(document);
        IReadOnlyDictionary<Guid, ExternalDefinition> sharedDefinitions = ReadSharedDefinitions(_application.Application);
        var records = new Dictionary<string, ParameterReadRecord>(StringComparer.Ordinal);

        foreach (Parameter parameter in owner.Parameters)
        {
            // Revit can expose stale, no-longer-bound parameters whose Definition is null.
            // They cannot be identified or read reliably and are not valid current parameters.
            if (parameter.Definition is null)
            {
                continue;
            }

            ParameterReadRecord record = Materialize(
                document,
                owner,
                parameter,
                target,
                documentKey,
                bindings,
                sharedDefinitions);
            if (!records.TryAdd(record.Summary.Identity.StableId, record))
            {
                throw new InvalidOperationException(
                    $"Duplicate parameter identity '{record.Summary.Identity.StableId}' on target '{target.Kind}'.");
            }
        }

        return new ParameterReadSnapshot(
            documentKey,
            _revisions.GetCurrent(documentKey),
            target,
            records.Values.ToArray());
    }

    private static Element ResolveTarget(Document document, ParameterTarget target)
    {
        if (target.Kind == ParameterTargetKind.Document)
        {
            return document.ProjectInformation;
        }

        Element? element = target.Kind switch
        {
            ParameterTargetKind.Family => ResolveByIdentity(document, target.UniqueId, target.ElementId),
            ParameterTargetKind.FamilyType => ResolveByIdentity(document, target.UniqueId, target.ElementId),
            _ => ResolveByIdentity(document, target.UniqueId, target.ElementId)
        };
        if (element is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.TargetNotFound);
        }

        bool matches = target.Kind switch
        {
            ParameterTargetKind.Element => element is not ElementType,
            ParameterTargetKind.ElementType => element is ElementType,
            ParameterTargetKind.Family => element is Family family &&
                string.Equals(family.Name, target.FamilyName, StringComparison.Ordinal),
            ParameterTargetKind.FamilyType => element is FamilySymbol symbol &&
                string.Equals(symbol.Name, target.TypeName, StringComparison.Ordinal) &&
                string.Equals(symbol.FamilyName, target.FamilyName, StringComparison.Ordinal),
            _ => false
        };
        if (!matches)
        {
            throw new ReadCursorException(BridgeErrorCodes.TargetNotFound);
        }

        return element;
    }

    private static Element? ResolveByIdentity(Document document, string? uniqueId, long? elementId)
    {
        Element? byUniqueId = uniqueId is null ? null : document.GetElement(uniqueId);
        Element? byElementId = elementId is null ? null : document.GetElement(new ElementId(elementId.Value));
        if (byUniqueId is not null && byElementId is not null && byUniqueId.Id != byElementId.Id)
        {
            throw new ReadCursorException(BridgeErrorCodes.TargetNotFound);
        }

        return byUniqueId ?? byElementId;
    }

    private static ParameterReadRecord Materialize(
        Document document,
        Element owner,
        Parameter parameter,
        ParameterTarget target,
        string documentKey,
        IReadOnlyDictionary<long, ProjectBindingInfo> bindings,
        IReadOnlyDictionary<Guid, ExternalDefinition> sharedDefinitions)
    {
        Definition definition = parameter.Definition
            ?? throw new ReadCursorException(BridgeErrorCodes.BridgeInternalError);
        ForgeTypeId dataType = definition.GetDataType();
        string? dataTypeId = string.IsNullOrWhiteSpace(dataType.TypeId) ? null : dataType.TypeId;
        long definitionId = parameter.Id.Value;
        bindings.TryGetValue(definitionId, out ProjectBindingInfo? projectBinding);
        ParameterIdentity identity = BuildIdentity(
            document,
            parameter,
            definition,
            target,
            documentKey,
            dataTypeId,
            projectBinding);
        ParameterBindingMetadata binding = BuildBinding(document, owner, target, identity, projectBinding);
        ParameterWorksharingMetadata worksharing = BuildWorksharing(document, owner, parameter.IsReadOnly);
        bool isFamilyParameter = target.Kind is ParameterTargetKind.Family or ParameterTargetKind.FamilyType &&
            identity.Kind == ParameterIdentityKind.FamilyDefinition;
        bool isTypeParameter = projectBinding?.Kind == ParameterBindingKind.ProjectType ||
            target.Kind is ParameterTargetKind.ElementType or ParameterTargetKind.FamilyType;
        string? blockedReason = GetBlockedReason(document, owner, target, parameter.IsReadOnly, worksharing);
        string? groupTypeId = definition.GetGroupTypeId()?.TypeId;
        if (string.IsNullOrWhiteSpace(groupTypeId))
        {
            groupTypeId = null;
        }

        ExternalDefinition? external = null;
        if (parameter.IsShared)
        {
            sharedDefinitions.TryGetValue(parameter.GUID, out external);
        }

        var metadata = new ParameterMetadata
        {
            Identity = identity,
            Definition = new ParameterDefinitionMetadata(
                definition.Name,
                dataTypeId,
                groupTypeId,
                external?.Description,
                definition is not InternalDefinition internalDefinition || internalDefinition.Visible,
                external?.UserModifiable ?? true),
            Binding = binding,
            StorageType = MapStorageType(parameter.StorageType),
            IsReadOnly = parameter.IsReadOnly,
            Formula = null,
            BlockedReason = blockedReason,
            Context = new ParameterContextMetadata
            {
                DocumentKey = documentKey,
                Target = target,
                IsTypeParameter = isTypeParameter,
                IsFamilyParameter = isFamilyParameter
            },
            Unit = BuildUnit(document, dataType),
            Worksharing = worksharing
        };
        var summary = new ParameterSummary
        {
            Identity = identity,
            StorageType = metadata.StorageType,
            BindingKind = binding.Kind,
            IsReadOnly = metadata.IsReadOnly
        };
        return new ParameterReadRecord(summary, metadata);
    }

    private static ParameterIdentity BuildIdentity(
        Document document,
        Parameter parameter,
        Definition definition,
        ParameterTarget target,
        string documentKey,
        string? dataTypeId,
        ProjectBindingInfo? projectBinding)
    {
        long definitionId = parameter.Id.Value;
        bool? isInstance = projectBinding?.Kind switch
        {
            ParameterBindingKind.ProjectInstance => true,
            ParameterBindingKind.ProjectType => false,
            _ => target.Kind is ParameterTargetKind.Element or ParameterTargetKind.Family
        };

        if (definitionId < 0)
        {
            return new ParameterIdentity
            {
                Kind = ParameterIdentityKind.BuiltIn,
                BuiltInId = definitionId,
                StableId = $"built-in:{definitionId.ToString(CultureInfo.InvariantCulture)}",
                Name = definition.Name,
                DataTypeId = dataTypeId,
                IsInstance = isInstance
            };
        }

        if (parameter.IsShared)
        {
            Guid guid = parameter.GUID;
            return new ParameterIdentity
            {
                Kind = ParameterIdentityKind.SharedGuid,
                SharedGuid = guid,
                StableId = $"shared:{guid:D}",
                Name = definition.Name,
                DataTypeId = dataTypeId,
                IsInstance = isInstance
            };
        }

        if (document.GetElement(parameter.Id) is ParameterElement parameterElement)
        {
            return new ParameterIdentity
            {
                Kind = ParameterIdentityKind.ParameterElement,
                DefinitionId = definitionId,
                OwnerContext = $"document:{documentKey}",
                StableId = $"parameter-element:{parameterElement.UniqueId}",
                Name = definition.Name,
                DataTypeId = dataTypeId,
                IsInstance = isInstance
            };
        }

        ParameterIdentityKind kind = target.Kind is ParameterTargetKind.Family or ParameterTargetKind.FamilyType
            ? ParameterIdentityKind.FamilyDefinition
            : ParameterIdentityKind.ParameterElement;
        string ownerContext = target.UniqueId is null
            ? $"document:{documentKey}"
            : $"family:{target.UniqueId}";
        return new ParameterIdentity
        {
            Kind = kind,
            DefinitionId = definitionId,
            OwnerContext = ownerContext,
            StableId = $"definition:{ownerContext}:{definitionId.ToString(CultureInfo.InvariantCulture)}",
            Name = definition.Name,
            DataTypeId = dataTypeId,
            IsInstance = isInstance
        };
    }

    private static ParameterBindingMetadata BuildBinding(
        Document document,
        Element owner,
        ParameterTarget target,
        ParameterIdentity identity,
        ProjectBindingInfo? projectBinding)
    {
        if (projectBinding is not null)
        {
            return new ParameterBindingMetadata(
                projectBinding.Kind,
                projectBinding.CategoryIds,
                null,
                "project-binding");
        }

        string? familyCategoryId = target.Kind is ParameterTargetKind.Family or ParameterTargetKind.FamilyType
            ? FormatCategoryId(owner.Category)
            : null;
        ParameterBindingKind kind = identity.Kind switch
        {
            ParameterIdentityKind.BuiltIn => ParameterBindingKind.None,
            ParameterIdentityKind.SharedGuid when target.Kind is ParameterTargetKind.Family => ParameterBindingKind.FamilyInstance,
            ParameterIdentityKind.SharedGuid when target.Kind is ParameterTargetKind.FamilyType => ParameterBindingKind.FamilyType,
            ParameterIdentityKind.SharedGuid => ParameterBindingKind.Shared,
            ParameterIdentityKind.FamilyDefinition when target.Kind == ParameterTargetKind.Family => ParameterBindingKind.FamilyInstance,
            ParameterIdentityKind.FamilyDefinition when target.Kind == ParameterTargetKind.FamilyType => ParameterBindingKind.FamilyType,
            _ => ParameterBindingKind.None
        };
        return new ParameterBindingMetadata(kind, [], familyCategoryId, "target-element");
    }

    private static ParameterWorksharingMetadata BuildWorksharing(
        Document document,
        Element owner,
        bool isReadOnly)
    {
        if (!document.IsWorkshared)
        {
            return new ParameterWorksharingMetadata
            {
                IsWorkshared = false,
                IsOwnedByCurrentUser = false,
                Owner = null,
                IsEditable = !document.IsReadOnly && !isReadOnly
            };
        }

        CheckoutStatus checkoutStatus = WorksharingUtils.GetCheckoutStatus(document, owner.Id);
        string? ownerName = WorksharingUtils.GetWorksharingTooltipInfo(document, owner.Id).Owner;
        bool ownedByCurrentUser = checkoutStatus == CheckoutStatus.OwnedByCurrentUser;
        bool editable = !document.IsReadOnly && !isReadOnly && checkoutStatus != CheckoutStatus.OwnedByOtherUser;
        return new ParameterWorksharingMetadata
        {
            IsWorkshared = true,
            IsOwnedByCurrentUser = ownedByCurrentUser,
            Owner = string.IsNullOrWhiteSpace(ownerName) ? null : ownerName,
            IsEditable = editable
        };
    }

    private static string? GetBlockedReason(
        Document document,
        Element owner,
        ParameterTarget target,
        bool parameterIsReadOnly,
        ParameterWorksharingMetadata worksharing)
    {
        if (document.IsReadOnly)
        {
            return "document_read_only";
        }

        if (target.Kind == ParameterTargetKind.Family && owner is Family { IsInPlace: true })
        {
            return "in_place_family_not_supported";
        }

        if (parameterIsReadOnly)
        {
            return "parameter_read_only";
        }

        if (worksharing.IsWorkshared && !worksharing.IsEditable)
        {
            return "element_owned_by_other_user";
        }

        return null;
    }

    private static ParameterUnitMetadata? BuildUnit(Document document, ForgeTypeId dataType)
    {
        if (!UnitUtils.IsMeasurableSpec(dataType))
        {
            return null;
        }

        ForgeTypeId unitType = document.GetUnits().GetFormatOptions(dataType).GetUnitTypeId();
        return new ParameterUnitMetadata
        {
            SpecTypeId = dataType.TypeId,
            UnitTypeId = string.IsNullOrWhiteSpace(unitType.TypeId) ? null : unitType.TypeId,
            Format = null
        };
    }

    private static IReadOnlyDictionary<long, ProjectBindingInfo> ReadProjectBindings(Document document)
    {
        var result = new Dictionary<long, ProjectBindingInfo>();
        DefinitionBindingMapIterator iterator = document.ParameterBindings.ForwardIterator();
        iterator.Reset();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition definition || iterator.Current is not ElementBinding binding)
            {
                continue;
            }

            ParameterBindingKind kind = binding is InstanceBinding
                ? ParameterBindingKind.ProjectInstance
                : ParameterBindingKind.ProjectType;
            string[] categories = binding.Categories
                .Cast<Category>()
                .Select(FormatCategoryId)
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            result[definition.Id.Value] = new ProjectBindingInfo(kind, categories);
        }

        return result;
    }

    private static IReadOnlyDictionary<Guid, ExternalDefinition> ReadSharedDefinitions(
        Autodesk.Revit.ApplicationServices.Application application)
    {
        var result = new Dictionary<Guid, ExternalDefinition>();
        if (string.IsNullOrWhiteSpace(application.SharedParametersFilename))
        {
            return result;
        }

        DefinitionFile? file = application.OpenSharedParameterFile();
        if (file is null)
        {
            return result;
        }

        foreach (DefinitionGroup group in file.Groups)
        {
            foreach (Definition definition in group.Definitions)
            {
                if (definition is ExternalDefinition external)
                {
                    result[external.GUID] = external;
                }
            }
        }

        return result;
    }

    private static ContractStorageType MapStorageType(RevitStorageType storageType) => storageType switch
    {
        RevitStorageType.String => ContractStorageType.String,
        RevitStorageType.Integer => ContractStorageType.Integer,
        RevitStorageType.Double => ContractStorageType.Double,
        RevitStorageType.ElementId => ContractStorageType.ElementId,
        _ => ContractStorageType.None
    };

    private static string? FormatCategoryId(Category? category) => category is null
        ? null
        : $"revit-category:{category.Id.Value.ToString(CultureInfo.InvariantCulture)}";

    private sealed record ProjectBindingInfo(
        ParameterBindingKind Kind,
        IReadOnlyList<string> CategoryIds);

    private sealed record DocumentCandidate(Document Document, string DocumentKey);
}
