using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class RevitElementReadSource : IRevitElementReadSource
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    internal RevitElementReadSource(
        UIApplication application,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public ElementReadSnapshot ReadElements(
        string sessionId,
        string documentKey,
        ListElementsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);

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
        Document selectedDocument = candidate.Document;
        var collector = new FilteredElementCollector(selectedDocument);
        if (!request.IncludeTypes)
        {
            collector = collector.WhereElementIsNotElementType();
        }

        if (request.CategoryId is not null)
        {
            const string prefix = "revit-category:";
            if (!long.TryParse(
                    request.CategoryId.AsSpan(prefix.Length),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out long categoryId))
            {
                throw new ReadCursorException(BridgeErrorCodes.InvalidRequest);
            }

            collector = collector.WherePasses(new ElementCategoryFilter(new ElementId(categoryId)));
        }

        var elements = new List<ElementSummary>();
        foreach (Element element in collector)
        {
            string name = element.Name;
            if (request.NameContains is { } nameFilter &&
                !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Category? category = element.Category;
            bool isElementType = element is ElementType;
            string? typeUniqueId = null;
            string? typeName = null;
            if (!isElementType)
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId &&
                    selectedDocument.GetElement(typeId) is ElementType elementType)
                {
                    typeUniqueId = elementType.UniqueId;
                    typeName = elementType.Name;
                }
            }

            elements.Add(new ElementSummary
            {
                UniqueId = element.UniqueId,
                ElementId = element.Id.Value,
                Name = name,
                CategoryId = category is null
                    ? null
                    : $"revit-category:{category.Id.Value.ToString(CultureInfo.InvariantCulture)}",
                CategoryName = category?.Name,
                IsElementType = isElementType,
                TypeUniqueId = typeUniqueId,
                TypeName = typeName
            });
        }

        return new ElementReadSnapshot(
            candidate.DocumentKey,
            _revisions.GetCurrent(candidate.DocumentKey),
            elements);
    }

    private sealed record DocumentCandidate(Document Document, string DocumentKey);
}
