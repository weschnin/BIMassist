using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class RevitFamilyReadSource : IRevitFamilyReadSource
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    internal RevitFamilyReadSource(
        UIApplication application,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public FamilyReadSnapshot ReadFamilies(string sessionId, string documentKey)
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
        Document selectedDocument = candidate.Document;
        var families = new List<FamilyReadRecord>();
        foreach (Family family in new FilteredElementCollector(selectedDocument)
                     .OfClass(typeof(Family))
                     .Cast<Family>())
        {
            var types = new List<FamilyTypeSummary>();
            foreach (ElementId symbolId in family.GetFamilySymbolIds())
            {
                if (selectedDocument.GetElement(symbolId) is not FamilySymbol symbol)
                {
                    continue;
                }

                types.Add(new FamilyTypeSummary
                {
                    UniqueId = symbol.UniqueId,
                    ElementId = symbol.Id.Value,
                    Name = symbol.Name
                });
            }

            Category? category = family.FamilyCategory;
            families.Add(new FamilyReadRecord(
                new FamilySummary
                {
                    UniqueId = family.UniqueId,
                    ElementId = family.Id.Value,
                    Name = family.Name,
                    CategoryId = category is null
                        ? null
                        : $"revit-category:{category.Id.Value.ToString(CultureInfo.InvariantCulture)}",
                    CategoryName = category?.Name,
                    IsInPlace = family.IsInPlace,
                    IsEditable = family.IsEditable,
                    IsShared = IsShared(family),
                    TypeCount = types.Count
                },
                types));
        }

        return new FamilyReadSnapshot(
            candidate.DocumentKey,
            _revisions.GetCurrent(candidate.DocumentKey),
            families);
    }

    private static bool IsShared(Family family)
    {
        Parameter? parameter = family.get_Parameter(BuiltInParameter.FAMILY_SHARED);
        return parameter is not null &&
               parameter.StorageType == StorageType.Integer &&
               parameter.AsInteger() != 0;
    }

    private sealed record DocumentCandidate(Document Document, string DocumentKey);
}
