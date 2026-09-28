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

        ParameterTarget requested = payload.Target;
        ParameterTarget canonical;
        if (requested.Kind == ParameterTargetKind.Document)
        {
            if (document.ProjectInformation is null)
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            canonical = new ParameterTarget { Kind = ParameterTargetKind.Document };
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
        }
        else
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);

        ParameterReadSnapshot snapshot = _parameters.ReadParameters(sessionId, documentKey, canonical);
        if (!string.Equals(snapshot.DocumentKey, documentKey, StringComparison.Ordinal) ||
            snapshot.Target != canonical ||
            !string.Equals(snapshot.DocumentRevision, request.ExpectedRevision, StringComparison.Ordinal) ||
            !string.Equals(_revisions.GetCurrent(documentKey), snapshot.DocumentRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        return SetParameterPlanSelection.Select(snapshot, payload.Parameter);
    }
}
