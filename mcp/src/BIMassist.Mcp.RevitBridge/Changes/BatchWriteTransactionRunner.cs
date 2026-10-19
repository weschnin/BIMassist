using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Host-owned adapter. Resolution must use actual owner and parameter IDs, not DTO identities.</summary>
internal interface IBatchStringParameterResolver
{
    IBatchStringParameter Resolve(ChangeOperation operation);
}

internal interface IBatchStringParameter
{
    string DocumentKey { get; }
    /// <summary>All identities must be read from the resolved Revit owner/parameter, never copied from the operation.</summary>
    long OwnerId { get; }
    string OwnerUniqueId { get; }
    bool IsProjectInformation { get; }
    bool IsElementType { get; }
    long ParameterId { get; }
    ParameterIdentityKind ParameterKind { get; }
    Guid? SharedGuid { get; }
    long? DefinitionId { get; }
    string? DefinitionStableId { get; }
    string? DataTypeId { get; }
    /// <summary>Live Revit Parameter.StorageType == StorageType.String; never inferred from the plan.</summary>
    bool HasStringStorage { get; }
    /// <summary>Live Parameter.IsReadOnly.</summary>
    bool IsReadOnly { get; }
    /// <summary>Live ExternalDefinition.UserModifiable, or true for a non-external definition.</summary>
    bool IsUserModifiable { get; }
    /// <summary>Live WorksharingUtils checkout status for the owner, false if not workshared.</summary>
    bool IsOwnedByOtherUser { get; }
    bool HasValue { get; }
    string? AsString();
    bool Set(string value);
}

/// <summary>Status values must reflect the host transaction's actual returned status.</summary>
internal enum BatchTransactionStatus { Unknown, Started, Committed, RolledBack, Pending }

/// <summary>Dispose must close/roll back any active transaction, including one left active by a failed Start.</summary>
internal interface IBatchWriteTransaction : IDisposable
{
    string DocumentKey { get; }
    BatchTransactionStatus Start();
    BatchTransactionStatus Commit();
    BatchTransactionStatus RollBack();
}

/// <summary>
/// One-document transaction coordinator, not an Apply entry point. A future ExternalEvent adapter
/// must supply a transaction and resolved parameters for the same document on Revit's UI thread.
/// No Revit API is called here; fake tests cannot establish real Revit transaction behavior.
/// </summary>
internal sealed class BatchWriteTransactionRunner
{
    internal ApplyOutcome Execute(ChangePlan plan, IBatchStringParameterResolver resolver,
        IBatchWriteTransaction transaction, Func<string> currentRevision,
        Func<DateTimeOffset> utcNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            using (transaction)
            {
                IReadOnlyList<ChangeOperation> operations = BatchStringWritePreconditions.Validate(plan);
                ArgumentNullException.ThrowIfNull(resolver);
                ArgumentNullException.ThrowIfNull(currentRevision);
                ArgumentNullException.ThrowIfNull(utcNow);
                if (!string.Equals(transaction.DocumentKey, plan.DocumentKey, StringComparison.Ordinal))
                    return Unknown();
                if (cancellationToken.IsCancellationRequested || utcNow() >= plan.ExpiresAtUtc)
                    throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
                // The adapter owns cleanup even if Start returned a non-started status or threw after activation.
                return ExecuteStarted(plan, operations, resolver, transaction, currentRevision, utcNow, cancellationToken);
            }
        }
        catch (ChangePlanFailure) { throw; }
        catch { return Unknown(); }
    }

    private static ApplyOutcome ExecuteStarted(ChangePlan plan, IReadOnlyList<ChangeOperation> operations,
        IBatchStringParameterResolver resolver, IBatchWriteTransaction transaction,
        Func<string> currentRevision, Func<DateTimeOffset> utcNow, CancellationToken cancellationToken)
    {
        try
        {
            if (transaction.Start() != BatchTransactionStatus.Started)
                return Unknown();
        }
        catch { return Unknown(); }

        // From a confirmed Start through Commit, ANY pre-commit failure attempts rollback.
        try
        {
            if (!string.Equals(currentRevision(), plan.ExpectedRevision, StringComparison.Ordinal))
                return RollBack(transaction);
            var resolved = new List<IBatchStringParameter>(operations.Count);
            var seen = new HashSet<(long OwnerId, long ParameterId)>();
            foreach (ChangeOperation operation in operations)
            {
                IBatchStringParameter parameter = resolver.Resolve(operation);
                if (parameter is null ||
                    !string.Equals(parameter.DocumentKey, plan.DocumentKey, StringComparison.Ordinal) ||
                    !MatchesIdentity(parameter, operation, plan.DocumentKey) ||
                    !seen.Add((parameter.OwnerId, parameter.ParameterId)))
                    return RollBack(transaction);
                resolved.Add(parameter);
            }
            for (int i = 0; i < operations.Count; i++)
            {
                // Preflight every live parameter, not the DTO's IsReadOnly or its declared value kind.
                if (!resolved[i].HasStringStorage || resolved[i].IsReadOnly ||
                    !resolved[i].IsUserModifiable || resolved[i].IsOwnedByOtherUser)
                    return RollBack(transaction);
                bool hasValue = resolved[i].HasValue;
                StringWritePreconditions.Check(operations[i], hasValue ? resolved[i].AsString() : null, hasValue);
            }

            if (!string.Equals(currentRevision(), plan.ExpectedRevision, StringComparison.Ordinal))
                return RollBack(transaction);

            for (int i = 0; i < operations.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested || utcNow() >= plan.ExpiresAtUtc ||
                    !string.Equals(currentRevision(), plan.ExpectedRevision, StringComparison.Ordinal))
                    return RollBack(transaction);
                IBatchStringParameter parameter = resolved[i];
                // A prior Set/updater may also change live writability without advancing revision.
                if (!parameter.HasStringStorage || parameter.IsReadOnly ||
                    !parameter.IsUserModifiable || parameter.IsOwnedByOtherUser)
                    return RollBack(transaction);
                // Set/updaters can change a later parameter without advancing DocumentChanged revision.
                // Keep the full preflight above, then compare the immediate live value before each Set.
                bool hasValue = parameter.HasValue;
                StringWritePreconditions.Check(operations[i], hasValue ? parameter.AsString() : null, hasValue);
                string after = operations[i].After!.StringValue!;
                if (!parameter.Set(after) || !parameter.HasValue ||
                    !string.Equals(parameter.AsString(), after, StringComparison.Ordinal))
                    return RollBack(transaction);
            }
            // A later Set/updater may have changed an earlier approved After value.
            // Check every value while rollback is still possible, never only post-commit.
            for (int i = 0; i < operations.Count; i++)
                if (!resolved[i].HasValue || !string.Equals(resolved[i].AsString(),
                        operations[i].After!.StringValue, StringComparison.Ordinal))
                    return RollBack(transaction);
            if (cancellationToken.IsCancellationRequested || utcNow() >= plan.ExpiresAtUtc ||
                !string.Equals(currentRevision(), plan.ExpectedRevision, StringComparison.Ordinal))
                return RollBack(transaction);
            // After Commit is attempted, even a thrown exception is OutcomeUnknown, never rollback.
            BatchTransactionStatus committed;
            try { committed = transaction.Commit(); }
            catch { return Unknown(); }
            if (committed == BatchTransactionStatus.RolledBack)
                return new ApplyOutcome(WriteOutcome.RolledBack, null);
            if (committed != BatchTransactionStatus.Committed)
                return Unknown();

            try
            {
                string revision = currentRevision();
                if (string.IsNullOrWhiteSpace(revision) ||
                    string.Equals(revision, plan.ExpectedRevision, StringComparison.Ordinal))
                    return Unknown();
                for (int i = 0; i < operations.Count; i++)
                    if (!resolved[i].HasValue ||
                        !string.Equals(resolved[i].AsString(), operations[i].After!.StringValue, StringComparison.Ordinal))
                        return Unknown();
                return new ApplyOutcome(WriteOutcome.Committed, revision);
            }
            catch { return Unknown(); }
        }
        catch { return RollBack(transaction); }
    }

    private static bool MatchesIdentity(IBatchStringParameter actual, ChangeOperation operation, string documentKey)
    {
        ParameterTarget target = operation.Target;
        bool matchesOwner = target.Kind switch
        {
            ParameterTargetKind.Document => actual.IsProjectInformation && !actual.IsElementType && actual.OwnerId > 0 &&
                !string.IsNullOrWhiteSpace(actual.OwnerUniqueId),
            ParameterTargetKind.Element or ParameterTargetKind.ElementType =>
                actual.IsElementType == (target.Kind == ParameterTargetKind.ElementType) &&
                actual.OwnerId == target.ElementId &&
                string.Equals(actual.OwnerUniqueId, target.UniqueId, StringComparison.Ordinal),
            _ => false
        };
        // IsInstance is read/search context, not a stable owner/parameter identity. In particular,
        // a Document target may carry false, and project BindingMap metadata need not match owner kind.
        if (!matchesOwner || actual.ParameterId == 0 || actual.ParameterKind != operation.Parameter.Kind)
            return false;

        ParameterIdentity requested = operation.Parameter;
        return requested.Kind switch
        {
            ParameterIdentityKind.SharedGuid => actual.SharedGuid is not null && actual.SharedGuid != Guid.Empty &&
                actual.SharedGuid == requested.SharedGuid,
            ParameterIdentityKind.BuiltIn => actual.ParameterId == requested.BuiltInId,
            ParameterIdentityKind.ParameterElement => actual.ParameterId == requested.DefinitionId &&
                actual.DefinitionId == requested.DefinitionId &&
                string.Equals(actual.DefinitionStableId, requested.StableId, StringComparison.Ordinal) &&
                string.Equals(actual.DataTypeId, requested.DataTypeId, StringComparison.Ordinal) &&
                string.Equals(requested.OwnerContext, $"document:{documentKey}", StringComparison.Ordinal),
            _ => false
        };
    }

    private static ApplyOutcome RollBack(IBatchWriteTransaction transaction)
    {
        try
        {
            return transaction.RollBack() == BatchTransactionStatus.RolledBack
                ? new ApplyOutcome(WriteOutcome.RolledBack, null) : Unknown();
        }
        catch { return Unknown(); }
    }

    private static ApplyOutcome Unknown() => new(WriteOutcome.OutcomeUnknown, null);
}
