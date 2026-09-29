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

/// <summary>Invoked only by the serialized ExternalEvent; never by a pipe thread.</summary>
internal sealed class RevitStringChangePlanApply : IApplyOperationDispatcher
{
    private readonly UIApplication _application;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;
    private readonly ChangePlanRegistry _registry;

    internal RevitStringChangePlanApply(UIApplication application, RevitSessionRegistry sessions,
        DocumentRevisionService revisions, ChangePlanRegistry registry)
    {
        _application = application;
        _sessions = sessions;
        _revisions = revisions;
        _registry = registry;
    }

    public ApplyOutcome Apply(BridgeRequest request, CancellationToken cancellationToken)
    {
        ApplyChangePlanRequest payload = ContractJson.Deserialize<ApplyChangePlanRequest>(request.Payload.GetRawText());
        string sessionId = request.SessionId!;
        string documentKey = request.DocumentKey!;
        if (!_sessions.TryGetSnapshot(sessionId, out _))
            throw new ChangePlanFailure(BridgeErrorCodes.SessionNotFound);
        Document document = ResolveDocument(sessionId, documentKey);
        if (document.IsFamilyDocument || document.IsReadOnly || document.IsModifiable)
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotWritable);
        string currentRevision = _revisions.GetCurrent(documentKey);
        return _registry.Apply(payload, sessionId, documentKey, currentRevision, DateTimeOffset.UtcNow,
            plan => Execute(plan, document, cancellationToken), plan => Approve(plan, document, cancellationToken),
            () => DateTimeOffset.UtcNow, cancellationToken);
    }

    private Document ResolveDocument(string sessionId, string documentKey) =>
        ExactDocumentResolver.Resolve(_application.Application.Documents.Cast<Document>(), documentKey,
            candidate => RevitContextSnapshotProvider.CreateDocumentKey(candidate, sessionId));

    private bool Approve(ChangePlan plan, Document document, CancellationToken cancellationToken)
    {
        ChangeOperation operation = StringWritePreconditions.Single(plan.Operations);
        // Resolve and compare the live target before displaying the dialog. A denied
        // dialog never reserves an idempotency key; a later attempt must ask again.
        _ = ResolveParameter(document, operation);
        if (!string.Equals(_revisions.GetCurrent(plan.DocumentKey), plan.ExpectedRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        return ApprovalGate.Show(plan, document.Title, _application.MainWindowHandle, cancellationToken);
    }

    private ApplyOutcome Execute(ChangePlan plan, Document document, CancellationToken cancellationToken)
    {
        ChangeOperation operation = StringWritePreconditions.Single(plan.Operations);
        // Recheck after the modal UI before opening a transaction.
        if (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= plan.ExpiresAtUtc)
            throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
        if (!document.IsValidObject || document.IsReadOnly || document.IsFamilyDocument || document.IsModifiable ||
            !string.Equals(RevitContextSnapshotProvider.CreateDocumentKey(document, plan.SessionId),
                plan.DocumentKey, StringComparison.Ordinal) ||
            !string.Equals(_revisions.GetCurrent(plan.DocumentKey), plan.ExpectedRevision, StringComparison.Ordinal))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
        Parameter parameter = ResolveParameter(document, operation);
        using var transaction = new Transaction(document, "BIMassist MCP: Zeichenparameter setzen");
        bool started = false;
        try
        {
            if (transaction.Start() != TransactionStatus.Started)
                return new ApplyOutcome(WriteOutcome.OutcomeUnknown, null);
            started = true;
            if (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= plan.ExpiresAtUtc)
                return RollBack(transaction);
            if (!parameter.Set(operation.After!.StringValue!))
                return RollBack(transaction);
            if (!parameter.HasValue || !string.Equals(parameter.AsString(), operation.After.StringValue, StringComparison.Ordinal))
                return RollBack(transaction);
            TransactionStatus committed = transaction.Commit();
            started = false;
            if (committed == TransactionStatus.RolledBack)
                return new ApplyOutcome(WriteOutcome.RolledBack, null);
            if (committed != TransactionStatus.Committed)
                return new ApplyOutcome(WriteOutcome.OutcomeUnknown, null);
            // DocumentChanged is the only source of revision advancement; never invent it.
            string revision = _revisions.GetCurrent(plan.DocumentKey);
            if (string.Equals(revision, plan.ExpectedRevision, StringComparison.Ordinal) ||
                !parameter.HasValue ||
                !string.Equals(parameter.AsString(), operation.After.StringValue, StringComparison.Ordinal))
                return new ApplyOutcome(WriteOutcome.OutcomeUnknown, null);
            return new ApplyOutcome(WriteOutcome.Committed, revision);
        }
        catch
        {
            return started ? RollBack(transaction) : new ApplyOutcome(WriteOutcome.OutcomeUnknown, null);
        }
    }

    private static ApplyOutcome RollBack(Transaction transaction)
    {
        try
        {
            return transaction.RollBack() == TransactionStatus.RolledBack
                ? new ApplyOutcome(WriteOutcome.RolledBack, null)
                : new ApplyOutcome(WriteOutcome.OutcomeUnknown, null);
        }
        catch
        {
            return new ApplyOutcome(WriteOutcome.OutcomeUnknown, null);
        }
    }

    private static Parameter ResolveParameter(Document document, ChangeOperation operation)
    {
        ParameterTarget target = operation.Target;
        Element element;
        if (target.Kind == ParameterTargetKind.Document)
        {
            if (target.UniqueId is not null || target.ElementId is not null || document.ProjectInformation is null)
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            element = document.ProjectInformation;
        }
        else if (target.Kind is ParameterTargetKind.Element or ParameterTargetKind.ElementType)
        {
            if (target.UniqueId is null || target.ElementId is null)
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
            element = StrictWriteIdentityResolver.Resolve(target.UniqueId, target.ElementId,
                uid => document.GetElement(uid), id => document.GetElement(new ElementId(id)),
                found => found.UniqueId, found => found.Id.Value);
            if ((target.Kind == ParameterTargetKind.ElementType) != (element is ElementType))
                throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        }
        else throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);

        Parameter? foundParameter = null;
        foreach (Parameter candidate in element.Parameters)
        {
            bool matches = operation.Parameter.Kind switch
            {
                ParameterIdentityKind.SharedGuid => candidate.IsShared &&
                    candidate.GUID == operation.Parameter.SharedGuid,
                ParameterIdentityKind.BuiltIn => operation.Parameter.BuiltInId is not null &&
                    candidate.Id.Value == operation.Parameter.BuiltInId,
                _ => false
            };
            if (!matches) continue;
            if (foundParameter is not null)
                throw new ChangePlanFailure(BridgeErrorCodes.AmbiguousTarget);
            foundParameter = candidate;
        }
        if (foundParameter is null)
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        if (foundParameter.StorageType != StorageType.String)
            throw new ChangePlanFailure(BridgeErrorCodes.TypeMismatch);
        if (foundParameter.IsReadOnly ||
            foundParameter.Definition is ExternalDefinition { UserModifiable: false })
            throw new ChangePlanFailure(BridgeErrorCodes.ParameterReadOnly);
        if (document.IsWorkshared &&
            WorksharingUtils.GetCheckoutStatus(document, element.Id) == CheckoutStatus.OwnedByOtherUser)
            throw new ChangePlanFailure(BridgeErrorCodes.WorksharingOwnership);
        StringWritePreconditions.Check(operation, foundParameter.AsString(), foundParameter.HasValue);
        return foundParameter;
    }
}
