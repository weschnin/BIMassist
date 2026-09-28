using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>
/// Process-lifetime state for plans and write outcomes. The only caller that may execute
/// a write is the serialized Revit ExternalEvent callback. Never evict an outcome and
/// silently permit a duplicate write: refuse new work when capacity is exhausted.
/// </summary>
internal sealed class ChangePlanRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ChangePlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordedAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly int _maximumPlans;
    private readonly int _maximumAttempts;

    public ChangePlanRegistry(int maximumPlans = 256, int maximumAttempts = 1024)
    {
        if (maximumPlans <= 0 || maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPlans));
        }
        _maximumPlans = maximumPlans;
        _maximumAttempts = maximumAttempts;
    }

    public void Register(ChangePlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ChangePlan copy = ContractJson.Deserialize<ChangePlan>(ContractJson.Serialize(plan));
        lock (_sync)
        {
            foreach (string expiredId in _plans.Where(entry => entry.Value.ExpiresAtUtc <= now)
                         .Select(entry => entry.Key).ToArray())
            {
                _plans.Remove(expiredId);
            }
            if (copy.ExpiresAtUtc <= now)
            {
                throw new ChangePlanFailure(BridgeErrorCodes.PlanExpired);
            }
            if (_plans.ContainsKey(copy.PlanId))
            {
                throw new ChangePlanFailure(BridgeErrorCodes.IdempotencyConflict);
            }
            if (_plans.Count >= _maximumPlans)
            {
                throw new ChangePlanFailure(BridgeErrorCodes.LimitExceeded);
            }
            _plans.Add(copy.PlanId, copy);
        }
    }

    public void InvalidateDocument(string documentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        lock (_sync)
        {
            foreach (string planId in _plans.Where(entry =>
                         string.Equals(entry.Value.DocumentKey, documentKey, StringComparison.Ordinal))
                     .Select(entry => entry.Key).ToArray())
            {
                _plans.Remove(planId);
            }
        }
    }

    public ApplyOutcome Apply(
        ApplyChangePlanRequest request,
        string sessionId,
        string documentKey,
        string currentRevision,
        DateTimeOffset now,
        Func<ChangePlan, ApplyOutcome> execute)
    {
        ContractValidator.Validate(request);
        ArgumentNullException.ThrowIfNull(execute);
        lock (_sync)
        {
            // A prior attempt must be consulted before expiry or revision: a successful
            // write itself advances the revision, and a client may lose its response.
            if (_attempts.TryGetValue(request.IdempotencyKey, out RecordedAttempt? prior))
            {
                if (prior.PlanId != request.PlanId || prior.PlanHash != request.PlanHash ||
                    prior.ExpectedRevision != request.ExpectedRevision ||
                    prior.SessionId != sessionId || prior.DocumentKey != documentKey)
                {
                    throw new ChangePlanFailure(BridgeErrorCodes.IdempotencyConflict);
                }
                return prior.Outcome;
            }
            if (!_plans.TryGetValue(request.PlanId, out ChangePlan? plan))
            {
                throw new ChangePlanFailure(BridgeErrorCodes.PlanNotFound);
            }
            if (!string.Equals(request.PlanHash, plan.PlanHash, StringComparison.Ordinal) ||
                !string.Equals(ChangePlanHasher.ComputeHash(plan), plan.PlanHash, StringComparison.Ordinal))
            {
                throw new ChangePlanFailure(BridgeErrorCodes.PlanHashMismatch);
            }
            if (!string.Equals(sessionId, plan.SessionId, StringComparison.Ordinal))
            {
                throw new ChangePlanFailure(BridgeErrorCodes.SessionNotFound);
            }
            if (!string.Equals(documentKey, plan.DocumentKey, StringComparison.Ordinal))
            {
                throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotFound);
            }
            if (!string.Equals(currentRevision, plan.ExpectedRevision, StringComparison.Ordinal) ||
                !string.Equals(request.ExpectedRevision, plan.ExpectedRevision, StringComparison.Ordinal))
            {
                throw new ChangePlanFailure(BridgeErrorCodes.DocumentChanged);
            }
            if (now >= plan.ExpiresAtUtc)
            {
                throw new ChangePlanFailure(BridgeErrorCodes.PlanExpired);
            }
            if (request.Approval.ApprovedAtUtc < plan.CreatedAtUtc ||
                request.Approval.ApprovedAtUtc > now)
            {
                throw new ChangePlanFailure(BridgeErrorCodes.InvalidRequest);
            }
            if (_attempts.Values.Any(attempt => attempt.PlanId == plan.PlanId))
            {
                throw new ChangePlanFailure(BridgeErrorCodes.IdempotencyConflict);
            }
            if (_attempts.Count >= _maximumAttempts)
            {
                throw new ChangePlanFailure(BridgeErrorCodes.LimitExceeded);
            }

            // Reserve before invoking Revit. An exception or connection loss does not
            // establish rollback; it must never invite an automatic second attempt.
            var attempt = new RecordedAttempt(request.PlanId, request.PlanHash,
                request.ExpectedRevision, sessionId, documentKey,
                new ApplyOutcome(WriteOutcome.OutcomeUnknown, null));
            _attempts.Add(request.IdempotencyKey, attempt);
            try
            {
                ChangePlan isolated = ContractJson.Deserialize<ChangePlan>(ContractJson.Serialize(plan));
                ApplyOutcome outcome = execute(isolated);
                if (outcome is null ||
                    (outcome.State == WriteOutcome.Committed &&
                     (string.IsNullOrWhiteSpace(outcome.DocumentRevision) ||
                      string.Equals(outcome.DocumentRevision, plan.ExpectedRevision, StringComparison.Ordinal))) ||
                    (outcome.State != WriteOutcome.Committed && outcome.State != WriteOutcome.RolledBack &&
                     outcome.State != WriteOutcome.OutcomeUnknown))
                {
                    return attempt.Outcome;
                }
                attempt.Outcome = outcome;
                return outcome;
            }
            catch
            {
                return attempt.Outcome;
            }
        }
    }

    private sealed class RecordedAttempt(
        string planId, string planHash, string expectedRevision,
        string sessionId, string documentKey, ApplyOutcome outcome)
    {
        public string PlanId { get; } = planId;
        public string PlanHash { get; } = planHash;
        public string ExpectedRevision { get; } = expectedRevision;
        public string SessionId { get; } = sessionId;
        public string DocumentKey { get; } = documentKey;
        public ApplyOutcome Outcome { get; set; } = outcome;
    }
}

public enum WriteOutcome { Committed, RolledBack, OutcomeUnknown }

public sealed record ApplyOutcome(WriteOutcome State, string? DocumentRevision);

public sealed class ChangePlanFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}
