using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Resolves a write target using every identity supplied by the caller.</summary>
internal static class StrictWriteIdentityResolver
{
    internal static T Resolve<T>(
        string? uniqueId,
        long? elementId,
        Func<string, T?> lookupByUniqueId,
        Func<long, T?> lookupByElementId,
        Func<T, string?> getUniqueId,
        Func<T, long?> getElementId) where T : class
    {
        if (uniqueId is not null && string.IsNullOrWhiteSpace(uniqueId))
        {
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        }
        T? byUniqueId = uniqueId is null ? null : lookupByUniqueId(uniqueId);
        T? byElementId = elementId is null ? null : lookupByElementId(elementId.Value);
        if (uniqueId is not null && byUniqueId is null || elementId is not null && byElementId is null)
        {
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        }
        if (byUniqueId is not null && byElementId is not null &&
            (!string.Equals(getUniqueId(byUniqueId), getUniqueId(byElementId), StringComparison.Ordinal) ||
             getElementId(byUniqueId) != getElementId(byElementId)))
        {
            throw new ChangePlanFailure(BridgeErrorCodes.AmbiguousTarget);
        }
        if (byUniqueId is not null &&
            (!string.Equals(getUniqueId(byUniqueId), uniqueId, StringComparison.Ordinal) ||
             (elementId is not null && getElementId(byUniqueId) != elementId)))
        {
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        }
        if (byElementId is not null &&
            (getElementId(byElementId) != elementId ||
             (uniqueId is not null && !string.Equals(getUniqueId(byElementId), uniqueId, StringComparison.Ordinal))))
        {
            throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
        }
        return byUniqueId ?? byElementId ?? throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
    }
}
