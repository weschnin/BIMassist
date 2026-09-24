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

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class RevitProjectBindingReadSource : IRevitProjectBindingReadSource
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    internal RevitProjectBindingReadSource(
        UIApplication application,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public ProjectBindingReadSnapshot ReadProjectBindings(string sessionId, string documentKey)
    {
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

        DocumentCandidate candidate = ExactDocumentResolver.Resolve(
            candidates,
            documentKey,
            item => item.DocumentKey);
        Document document = candidate.Document;
        IReadOnlyDictionary<Guid, ExternalDefinition> externalDefinitions = ReadExternalDefinitions();
        var descriptors = new List<ProjectBindingDescriptor>();
        DefinitionBindingMapIterator iterator = document.ParameterBindings.ForwardIterator();
        iterator.Reset();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition definition || iterator.Current is not ElementBinding binding)
            {
                continue;
            }

            bool isInstance = binding is InstanceBinding;
            ParameterBindingKind bindingKind = isInstance
                ? ParameterBindingKind.ProjectInstance
                : ParameterBindingKind.ProjectType;
            string dataTypeId = definition.GetDataType().TypeId;
            string? groupTypeId = NullIfEmpty(definition.GetGroupTypeId().TypeId);
            ParameterIdentity identity = CreateIdentity(
                document,
                definition,
                candidate.DocumentKey,
                dataTypeId,
                isInstance);
            ExternalDefinition? external = identity.SharedGuid is Guid sharedGuid &&
                                           externalDefinitions.TryGetValue(sharedGuid, out ExternalDefinition? found)
                ? found
                : null;
            string[] categoryIds = binding.Categories
                .Cast<Category>()
                .Select(category => CategoryId(category.Id))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            descriptors.Add(new ProjectBindingDescriptor
            {
                Identity = identity,
                Definition = new ParameterDefinitionMetadata(
                    definition.Name,
                    dataTypeId,
                    groupTypeId,
                    NullIfEmpty(external?.Description),
                    definition.Visible,
                    external?.UserModifiable ?? true),
                Binding = new ParameterBindingMetadata(
                    bindingKind,
                    categoryIds,
                    null,
                    external is null ? "project-document" : "project-shared-definition")
            });
        }

        return new ProjectBindingReadSnapshot(
            candidate.DocumentKey,
            _revisions.GetCurrent(candidate.DocumentKey),
            descriptors);
    }

    private static ParameterIdentity CreateIdentity(
        Document document,
        InternalDefinition definition,
        string documentKey,
        string dataTypeId,
        bool isInstance)
    {
        BuiltInParameter builtIn = definition.BuiltInParameter;
        if (builtIn != BuiltInParameter.INVALID)
        {
            long builtInId = (long)builtIn;
            return new ParameterIdentity
            {
                Kind = ParameterIdentityKind.BuiltIn,
                BuiltInId = builtInId,
                StableId = $"built-in:{builtInId.ToString(CultureInfo.InvariantCulture)}",
                Name = definition.Name,
                DataTypeId = dataTypeId,
                IsInstance = isInstance
            };
        }

        ElementId definitionId = definition.Id;
        if (document.GetElement(definitionId) is SharedParameterElement shared)
        {
            Guid guid = shared.GuidValue;
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

        if (document.GetElement(definitionId) is ParameterElement parameterElement && definitionId.Value > 0)
        {
            return new ParameterIdentity
            {
                Kind = ParameterIdentityKind.ParameterElement,
                StableId = $"parameter-element:{parameterElement.UniqueId}",
                Name = definition.Name,
                OwnerContext = documentKey,
                DefinitionId = definitionId.Value,
                DataTypeId = dataTypeId,
                IsInstance = isInstance
            };
        }

        throw new ReadCursorException(BridgeErrorCodes.BridgeInternalError);
    }

    private IReadOnlyDictionary<Guid, ExternalDefinition> ReadExternalDefinitions()
    {
        var definitions = new Dictionary<Guid, ExternalDefinition>();
        DefinitionFile? file = _application.Application.OpenSharedParameterFile();
        if (file is null)
        {
            return definitions;
        }

        foreach (DefinitionGroup group in file.Groups)
        {
            foreach (Definition definition in group.Definitions)
            {
                if (definition is ExternalDefinition external)
                {
                    definitions[external.GUID] = external;
                }
            }
        }

        return definitions;
    }

    private static string CategoryId(ElementId id) =>
        $"revit-category:{id.Value.ToString(CultureInfo.InvariantCulture)}";

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record DocumentCandidate(Document Document, string DocumentKey);
}
