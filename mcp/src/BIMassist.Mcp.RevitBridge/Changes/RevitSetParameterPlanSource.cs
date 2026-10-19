using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Reads;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>
/// Revit API adapter. Call Read only from the bridge's existing serialized ExternalEvent callback;
/// this class does not schedule a callback, start a transaction, or write to a document.
/// </summary>
internal sealed class RevitSetParameterPlanSource : ISetParameterPlanSource
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;
    private readonly IRevitParameterReadSource _parameters;

    internal RevitSetParameterPlanSource(UIApplication application, RevitSessionRegistry sessions,
        DocumentRevisionService revisions, RevitParameterReadSource parameterSource)
        : this(application, sessions, revisions, (IRevitParameterReadSource)parameterSource)
    {
    }

    internal RevitSetParameterPlanSource(UIApplication application, RevitSessionRegistry sessions,
        DocumentRevisionService revisions, IRevitParameterReadSource parameters)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public ResolvedSetParameterPlan Read(BridgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);
        if (request.Operation != BridgeOperations.PlanSetParameterValues)
            throw new ChangePlanFailure(BridgeErrorCodes.OperationNotSupported);
        PlanSetParameterValueRequest payload = ContractJson.Deserialize<PlanSetParameterValueRequest>(request.Payload.GetRawText());
        if (!_sessions.TryGetSnapshot(request.SessionId!, out _))
            throw new ChangePlanFailure(BridgeErrorCodes.SessionNotFound);

        string sessionId = request.SessionId!;
        string documentKey = request.DocumentKey!;
        Document document = ExactDocumentResolver.Resolve(
            _application.Application.Documents.Cast<Document>(), documentKey,
            candidate => RevitContextSnapshotProvider.CreateDocumentKey(candidate, sessionId));
        if (document.IsFamilyDocument || document.IsReadOnly)
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotWritable);
        if (!string.Equals(_revisions.GetCurrent(documentKey), request.ExpectedRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        return ReadSelected(document, sessionId, documentKey, request.ExpectedRevision!, payload.Target, payload.Parameter);
    }

    // Called from the serialized bridge callback; resolve the exact document and title
    // together so the builder preflights the same model used for all observations.
    internal IReadOnlyList<ResolvedSetParameterPlan> ReadBatch(
        PlanSetStringParametersBatchRequest payload, string sessionId, string documentKey, string expectedRevision)
        => ReadBatch(payload, sessionId, documentKey, expectedRevision, out _);

    private IReadOnlyList<ResolvedSetParameterPlan> ReadBatch(
        PlanSetStringParametersBatchRequest payload, string sessionId, string documentKey,
        string expectedRevision, out string documentTitle)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ContractValidator.Validate(payload);
        if (!string.Equals(payload.DocumentKey, documentKey, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotFound);
        if (!_sessions.TryGetSnapshot(sessionId, out _))
            throw new ChangePlanFailure(BridgeErrorCodes.SessionNotFound);
        Document document = ExactDocumentResolver.Resolve(
            _application.Application.Documents.Cast<Document>(), documentKey,
            candidate => RevitContextSnapshotProvider.CreateDocumentKey(candidate, sessionId));
        if (document.IsFamilyDocument || document.IsReadOnly)
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotWritable);
        documentTitle = document.Title;
        return BatchSetParameterPlanReader.Read(payload, documentKey, expectedRevision,
            item => ReadSelected(document, sessionId, documentKey, expectedRevision,
                item.Target, item.Parameter, includeCanonicalOwner: true),
            () => _revisions.GetCurrent(documentKey));
    }

    // PLAN only; the batch registry remains isolated from Apply.
    internal ChangePlan BuildBatchPlan(PlanSetStringParametersBatchRequest payload,
        string sessionId, string documentKey, string expectedRevision, DateTimeOffset now)
    {
        IReadOnlyList<ResolvedSetParameterPlan> observations =
            ReadBatch(payload, sessionId, documentKey, expectedRevision, out string documentTitle);
        return BatchSetParameterPlanBuilder.Build(payload, sessionId, documentKey,
            expectedRevision, observations, now, documentTitle);
    }

    private ResolvedSetParameterPlan ReadSelected(Document document, string sessionId,
        string documentKey, string expectedRevision, ParameterTarget requested, ParameterIdentity parameter,
        bool includeCanonicalOwner = false)
    {
        ParameterTarget canonical;
        Element? owner = null;
        string? ownerUniqueId = null;
        if (requested.Kind == ParameterTargetKind.Document)
        {
            if (document.ProjectInformation is null)
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            canonical = new ParameterTarget { Kind = ParameterTargetKind.Document };
            if (includeCanonicalOwner) owner = document.ProjectInformation;
        }
        else if (requested.Kind is ParameterTargetKind.Element or ParameterTargetKind.ElementType)
        {
            Element element = StrictWriteIdentityResolver.Resolve(requested.UniqueId, requested.ElementId,
                uniqueId => document.GetElement(uniqueId),
                id => document.GetElement(new ElementId(id)),
                found => found.UniqueId, found => found.Id.Value);
            if ((requested.Kind == ParameterTargetKind.ElementType) != (element is ElementType))
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            canonical = new ParameterTarget
            {
                Kind = requested.Kind, UniqueId = element.UniqueId, ElementId = element.Id.Value
            };
            if (includeCanonicalOwner) owner = element;
        }
        else
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);

        ParameterReadSnapshot snapshot = _parameters.ReadParameters(sessionId, documentKey, canonical);
        if (!string.Equals(snapshot.DocumentKey, documentKey, StringComparison.Ordinal) ||
            snapshot.Target != canonical ||
            !string.Equals(snapshot.DocumentRevision, expectedRevision, StringComparison.Ordinal) ||
            !string.Equals(_revisions.GetCurrent(documentKey), snapshot.DocumentRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        ResolvedSetParameterPlan selected = SetParameterPlanSelection.Select(snapshot, parameter);
        if (!includeCanonicalOwner) return selected;
        ownerUniqueId = owner!.UniqueId;
        return selected with
        {
            CanonicalOwnerUniqueId = ownerUniqueId,
            CanonicalParameterId = ResolveParameterId(document, owner, documentKey, selected.Parameter)
        };
    }

    // Mirror the existing Apply selector to obtain the concrete Revit Parameter.Id.Value,
    // including negative built-in IDs. An identity DTO's kind/stableId alone cannot
    // distinguish SharedGuid and ParameterElement aliases of the same Parameter.
    private static long ResolveParameterId(Document document, Element owner, string documentKey,
        ParameterIdentity identity)
    {
        long? id = null;
        foreach (Parameter candidate in owner.Parameters)
        {
            bool matches = identity.Kind switch
            {
                ParameterIdentityKind.SharedGuid => candidate.IsShared && candidate.GUID == identity.SharedGuid,
                ParameterIdentityKind.BuiltIn => candidate.Id.Value == identity.BuiltInId,
                ParameterIdentityKind.ParameterElement => candidate.Id.Value == identity.DefinitionId &&
                    string.Equals(identity.OwnerContext, $"document:{documentKey}", StringComparison.Ordinal) &&
                    candidate.Definition is not null &&
                    document.GetElement(candidate.Id) is ParameterElement definition &&
                    string.Equals(identity.StableId, $"parameter-element:{definition.UniqueId}", StringComparison.Ordinal) &&
                    string.Equals(identity.DataTypeId, candidate.Definition.GetDataType().TypeId, StringComparison.Ordinal),
                _ => false
            };
            if (!matches) continue;
            if (id is not null) throw new ChangePlanFailure(BridgeErrorCodes.AmbiguousTarget);
            id = candidate.Id.Value;
        }
        return id ?? throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
    }
}
