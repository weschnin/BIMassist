using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Sessions;
using ContractStorageType = BIMassist.Mcp.Contracts.Parameters.ParameterStorageType;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record DocumentParameterSearchSnapshot(
    string DocumentKey,
    string DocumentRevision,
    IReadOnlyList<DocumentParameterMatch> Matches);

internal interface IRevitDocumentParameterSearchSource
{
    DocumentParameterSearchSnapshot ReadMatches(string sessionId, string documentKey,
        SearchDocumentParametersRequest request, CancellationToken cancellationToken = default);
    DocumentParameterSearchSnapshot ReadMatches(string sessionId, string documentKey,
        SearchDocumentParametersRequest request, DocumentParameterScanBudget budget, CancellationToken cancellationToken = default) =>
        ReadMatches(sessionId, documentKey, request, cancellationToken);
}

internal sealed class RevitDocumentParameterSearchSource : IRevitDocumentParameterSearchSource
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    internal RevitDocumentParameterSearchSource(UIApplication application, RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public DocumentParameterSearchSnapshot ReadMatches(string sessionId, string documentKey,
        SearchDocumentParametersRequest request, CancellationToken cancellationToken = default) =>
        ReadMatches(sessionId, documentKey, request, new DocumentParameterScanBudget(cancellationToken), cancellationToken);

    public DocumentParameterSearchSnapshot ReadMatches(string sessionId, string documentKey,
        SearchDocumentParametersRequest request, DocumentParameterScanBudget budget,
        CancellationToken cancellationToken = default)
    {
        budget.CheckTime();
        ContractValidator.Validate(request);
        budget.CheckTime();
        SessionDescriptor session = _sessions.GetSnapshot();
        budget.CheckTime();
        if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
            throw new ReadCursorException(BridgeErrorCodes.SessionNotFound);

        var open = new List<DocumentCandidate>();
        foreach (Document document in _application.Application.Documents)
        {
            budget.CheckTime();
            open.Add(new DocumentCandidate(document,
                RevitContextSnapshotProvider.CreateDocumentKey(document, session.SessionId)));
            budget.CheckTime();
        }
        budget.CheckTime();
        DocumentCandidate selected = ExactDocumentResolver.Resolve(open, documentKey, candidate =>
        {
            budget.CheckTime();
            return candidate.Key;
        });
        budget.CheckTime();
        var collector = new FilteredElementCollector(selected.Document);
        if (!request.IncludeTypes) collector = collector.WhereElementIsNotElementType();
        if (request.CategoryId is not null)
        {
            const string prefix = "revit-category:";
            if (!request.CategoryId.StartsWith(prefix, StringComparison.Ordinal) ||
                !long.TryParse(request.CategoryId.AsSpan(prefix.Length), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out long categoryId))
                throw new ReadCursorException(BridgeErrorCodes.InvalidRequest);
            collector = collector.WherePasses(new ElementCategoryFilter(new ElementId(categoryId)));
        }

        var matches = new List<DocumentParameterMatch>();
        foreach (Element element in collector)
        {
            budget.CountCandidate();
            if (request.CategoryId is not null &&
                !string.Equals(request.CategoryId,
                    element.Category is { } category
                        ? $"revit-category:{category.Id.Value.ToString(CultureInfo.InvariantCulture)}"
                        : null, StringComparison.Ordinal)) continue;
            foreach (Parameter parameter in element.Parameters)
            {
                budget.CountInspection();
                Definition? definition = parameter.Definition;
                // Stale entries without a Definition have no searchable name.
                if (definition is null || !definition.Name.Contains(request.NameContains,
                        StringComparison.OrdinalIgnoreCase)) continue;
                // Inspect the parameter name before deciding whether its owner is representable.
                ParameterTarget target = RequireSearchTarget(element.Id.Value, element.UniqueId,
                    element is ElementType ? ParameterTargetKind.ElementType : ParameterTargetKind.Element);
                ParameterIdentity? identity = RevitParameterReadSource.BuildSearchIdentity(
                    selected.Document, parameter, definition, target, selected.Key);
                // A matching but unsupported identity cannot be omitted from totalCount.
                ParameterIdentity supportedIdentity = DocumentParameterSearchService.RequireSearchIdentity(identity);
                budget.CountMatch();
                matches.Add(new DocumentParameterMatch
                {
                    Target = target,
                    Parameter = supportedIdentity,
                    StorageType = parameter.StorageType switch
                    {
                        StorageType.String => ContractStorageType.String,
                        StorageType.Integer => ContractStorageType.Integer,
                        StorageType.Double => ContractStorageType.Double,
                        StorageType.ElementId => ContractStorageType.ElementId,
                        _ => ContractStorageType.None
                    },
                    IsReadOnly = parameter.IsReadOnly
                });
            }
        }
        budget.CheckTime();
        string revision = _revisions.GetCurrent(selected.Key);
        budget.CheckTime();
        return new DocumentParameterSearchSnapshot(selected.Key, revision, matches);
    }

    internal static ParameterTarget RequireSearchTarget(long elementId, string? uniqueId, ParameterTargetKind kind)
    {
        if (elementId < 0 || string.IsNullOrWhiteSpace(uniqueId))
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        var target = new ParameterTarget { Kind = kind, UniqueId = uniqueId, ElementId = elementId };
        try
        {
            ContractValidator.Validate(elementId == 0 ? target with { ElementId = null } : target);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }
        return target;
    }

    private sealed record DocumentCandidate(Document Document, string Key);
}
