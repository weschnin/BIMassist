namespace BIMassist.Ava.Core;

public sealed record BgkReferenceType(string FamilyName, string TypeName, string? BgkValue);
public sealed record BgkTargetType(string FamilyName, string TypeName, bool CanWrite);
public enum BgkTransferStatus { Copy, NoExactMatch, AmbiguousReference, AmbiguousTarget, EmptyReferenceValue, TargetUnavailable }
public sealed record BgkTransferDecision(BgkTargetType Target, string? Value, BgkTransferStatus Status);

/// <summary>Creates a strict, auditable BGK copy plan; family and type comparisons are ordinal and exact.</summary>
public static class BgkReferenceTransfer
{
    public static IReadOnlyList<BgkTransferDecision> CreatePlan(IEnumerable<BgkTargetType> targets,
        IEnumerable<BgkReferenceType> references)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(references);
        var referenceGroups = references.GroupBy(x => (x.FamilyName, x.TypeName))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var targetArray = targets.ToArray();
        var targetCounts = targetArray.GroupBy(x => (x.FamilyName, x.TypeName)).ToDictionary(group => group.Key, group => group.Count());
        return targetArray.Select(target =>
        {
            if (targetCounts[(target.FamilyName, target.TypeName)] != 1)
                return new BgkTransferDecision(target, null, BgkTransferStatus.AmbiguousTarget);
            if (!referenceGroups.TryGetValue((target.FamilyName, target.TypeName), out var matches))
                return new BgkTransferDecision(target, null, BgkTransferStatus.NoExactMatch);
            if (matches.Length != 1)
                return new BgkTransferDecision(target, null, BgkTransferStatus.AmbiguousReference);
            var value = matches[0].BgkValue;
            if (string.IsNullOrWhiteSpace(value))
                return new BgkTransferDecision(target, value, BgkTransferStatus.EmptyReferenceValue);
            if (!target.CanWrite)
                return new BgkTransferDecision(target, value, BgkTransferStatus.TargetUnavailable);
            return new BgkTransferDecision(target, value, BgkTransferStatus.Copy);
        }).ToArray();
    }
}
