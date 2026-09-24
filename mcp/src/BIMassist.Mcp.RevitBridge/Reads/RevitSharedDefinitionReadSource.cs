using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class RevitSharedDefinitionReadSource : IRevitSharedDefinitionReadSource
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    internal RevitSharedDefinitionReadSource(
        UIApplication application,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public SharedDefinitionReadSnapshot ReadSharedDefinitions(string sessionId, string documentKey)
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
        var definitions = new List<SharedDefinitionDescriptor>();
        DefinitionFile? file = _application.Application.OpenSharedParameterFile();
        if (file is not null)
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is not ExternalDefinition external)
                    {
                        continue;
                    }

                    definitions.Add(new SharedDefinitionDescriptor
                    {
                        SharedGuid = external.GUID,
                        Name = external.Name,
                        DataTypeId = external.GetDataType().TypeId,
                        GroupName = group.Name,
                        Description = string.IsNullOrWhiteSpace(external.Description) ? null : external.Description,
                        IsVisible = external.Visible,
                        IsUserModifiable = external.UserModifiable
                    });
                }
            }
        }

        return new SharedDefinitionReadSnapshot(
            candidate.DocumentKey,
            _revisions.GetCurrent(candidate.DocumentKey),
            definitions);
    }

    private sealed record DocumentCandidate(Document Document, string DocumentKey);
}
