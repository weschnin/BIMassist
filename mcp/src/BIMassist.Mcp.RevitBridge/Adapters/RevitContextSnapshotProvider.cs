using System.Globalization;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Documents;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Adapters;

public sealed class RevitContextSnapshotProvider : IRevitContextSnapshotProvider
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    public RevitContextSnapshotProvider(
        UIApplication application,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public SessionDescriptor GetSessionSnapshot()
    {
        var documents = new List<DocumentDescriptor>();
        foreach (Document document in _application.Application.Documents)
        {
            documents.Add(CreateDescriptor(document, _sessions.GetSnapshot().SessionId, _revisions));
        }

        _sessions.ReplaceDocuments(documents);
        return _sessions.GetSnapshot();
    }

    public DocumentDescriptor? GetActiveDocument()
    {
        Document? document = _application.ActiveUIDocument?.Document;
        if (document is null)
        {
            GetSessionSnapshot();
            return null;
        }

        SessionDescriptor session = GetSessionSnapshot();
        string key = CreateDocumentKey(document, session.SessionId);
        return session.Documents.SingleOrDefault(candidate =>
            string.Equals(candidate.DocumentKey, key, StringComparison.Ordinal));
    }

    public static string CreateDocumentKey(Document document, string sessionId) =>
        DocumentKeyFactory.Create(sessionId, GetStableIdentity(document));

    private static DocumentDescriptor CreateDescriptor(
        Document document,
        string sessionId,
        DocumentRevisionService revisions)
    {
        string key = CreateDocumentKey(document, sessionId);
        bool isCloud = document.IsModelInCloud;
        string? path = GetDisplayPath(document);
        DocumentPathStatus pathStatus = isCloud
            ? DocumentPathStatus.Cloud
            : document.IsDetached
                ? DocumentPathStatus.Detached
                : string.IsNullOrWhiteSpace(document.PathName)
                    ? DocumentPathStatus.Unsaved
                    : DocumentPathStatus.Saved;

        return new DocumentDescriptor
        {
            DocumentKey = key,
            Title = document.Title,
            PathStatus = pathStatus,
            Path = path,
            IsFamilyDocument = document.IsFamilyDocument,
            IsWorkshared = document.IsWorkshared,
            IsReadOnly = document.IsReadOnly,
            ActiveViewUniqueId = document.ActiveView?.UniqueId,
            Revision = revisions.GetCurrent(key)
        };
    }

    private static string GetStableIdentity(Document document)
    {
        if (document.IsModelInCloud)
        {
            return $"cloud:{ModelPathUtils.ConvertModelPathToUserVisiblePath(document.GetCloudModelPath())}";
        }

        if (document.IsWorkshared)
        {
            try
            {
                ModelPath centralPath = document.GetWorksharingCentralModelPath();
                string central = ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                if (!string.IsNullOrWhiteSpace(central))
                {
                    return $"central:{central}";
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(document.PathName))
        {
            return $"path:{document.PathName}";
        }

        return $"unsaved:{RuntimeHelpers.GetHashCode(document).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? GetDisplayPath(Document document)
    {
        if (document.IsModelInCloud)
        {
            return ModelPathUtils.ConvertModelPathToUserVisiblePath(document.GetCloudModelPath());
        }

        return string.IsNullOrWhiteSpace(document.PathName) ? null : document.PathName;
    }
}
