using BIMassist.Ava.Core;

internal static class BgkReferenceTransferTests
{
    public static void Run()
    {
        var source = new[]
        {
            new BgkReferenceType("Familie A", "Typ 1", "001"),
            new BgkReferenceType("Familie A", "Typ 2", "002"),
            new BgkReferenceType("Familie B", "Typ X", "003"),
            new BgkReferenceType("Familie B", "Typ X", "004"),
            new BgkReferenceType("Familie C", "Typ 1", " ")
        };
        var targets = new[]
        {
            new BgkTargetType("Familie A", "Typ 1", true),
            new BgkTargetType("Familie A", "typ 2", true),
            new BgkTargetType("Familie B", "Typ X", true),
            new BgkTargetType("Familie C", "Typ 1", true),
            new BgkTargetType("Familie D", "Typ 1", true),
            new BgkTargetType("Familie A", "Typ 2", false)
            ,new BgkTargetType("Familie A", "Typ 1", true)
        };
        var plan = BgkReferenceTransfer.CreatePlan(targets, source);
        if (plan.Count != 7) throw new Exception("Transfer plan did not account for every active type.");
        if (plan.Where(x => x.Target.TypeName == "Typ 1" && x.Target.FamilyName == "Familie A").Any(x => x.Status != BgkTransferStatus.AmbiguousTarget))
            throw new Exception("Duplicate active family/type identity was not rejected.");
        if (plan.Single(x => x.Target.FamilyName == "Familie A" && x.Target.TypeName == "typ 2").Status != BgkTransferStatus.NoExactMatch)
            throw new Exception("Case-insensitive type matching was incorrectly allowed.");
        if (plan.Single(x => x.Target.FamilyName == "Familie B").Status != BgkTransferStatus.AmbiguousReference)
            throw new Exception("Ambiguous reference types were not rejected.");
        if (plan.Single(x => x.Target.FamilyName == "Familie C").Status != BgkTransferStatus.EmptyReferenceValue)
            throw new Exception("Blank reference BGK value was accepted.");
        if (plan.Single(x => x.Target.FamilyName == "Familie D").Status != BgkTransferStatus.NoExactMatch)
            throw new Exception("Unmatched target was not reported.");
        if (plan.Single(x => x.Target.FamilyName == "Familie A" && !x.Target.CanWrite).Status != BgkTransferStatus.TargetUnavailable)
            throw new Exception("Unavailable target parameter was accepted.");
        var uniquePlan = BgkReferenceTransfer.CreatePlan(targets.Take(6), source);
        if (uniquePlan.Single(x => x.Target.TypeName == "Typ 1" && x.Target.FamilyName == "Familie A").Status != BgkTransferStatus.Copy)
            throw new Exception("Unique exact family+type did not match.");
        Console.WriteLine("PASS BGK reference transfer plan requires one exact ordinal family+type match and reports every active type");
    }
}
