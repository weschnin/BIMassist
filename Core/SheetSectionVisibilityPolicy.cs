namespace BIMassist.Core;

internal enum MarkerUpdate { None, Hide, Unhide, Ambiguous }

internal static class SheetSectionVisibilityPolicy
{
    internal static MarkerUpdate Decide(string? markerName,
        IReadOnlyDictionary<string, IReadOnlyCollection<long>> sectionIdsByName,
        IReadOnlySet<long> placedViewIds, bool isHidden)
    {
        if (string.IsNullOrWhiteSpace(markerName)
            || !sectionIdsByName.TryGetValue(markerName, out var matches)
            || matches.Count == 0)
            return MarkerUpdate.None;
        if (matches.Count != 1)
            return MarkerUpdate.Ambiguous;
        bool belongsToSheet = placedViewIds.Contains(matches.Single());
        return belongsToSheet ? (isHidden ? MarkerUpdate.Unhide : MarkerUpdate.None)
            : (isHidden ? MarkerUpdate.None : MarkerUpdate.Hide);
    }
}
