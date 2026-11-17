using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist.Ava.Core;

public sealed record X31SelectionPlan(IReadOnlyList<string> ElementIds, bool SelectionIncomplete);

/// <summary>Pure selection planning shared by the review window and the Revit API event handler.</summary>
public static class X31ElementSelectionPlanner
{
    public static X31SelectionPlan FromRows(IEnumerable<X31ReviewRow> selectedRows)
    {
        ArgumentNullException.ThrowIfNull(selectedRows);
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = false;
        foreach (var row in selectedRows)
        {
            if (row is null) continue;
            incomplete |= row.SelectionIncomplete;
            foreach (var id in row.ElementIdsForSelection)
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) ids.Add(id);
        }
        return new X31SelectionPlan(ids, incomplete);
    }

    /// <summary>Returns the seed IDs plus all nested group members, de-duplicated and cycle-safe.</summary>
    public static IReadOnlyList<long> ExpandGroupMembers(IEnumerable<long> elementIds,
        Func<long, IEnumerable<long>?> getMemberIds)
    {
        ArgumentNullException.ThrowIfNull(elementIds);
        ArgumentNullException.ThrowIfNull(getMemberIds);
        var visited = new HashSet<long>();
        var pending = new Stack<long>(elementIds);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!visited.Add(id)) continue;
            var members = getMemberIds(id);
            if (members is null) continue;
            foreach (var memberId in members)
                if (!visited.Contains(memberId)) pending.Push(memberId);
        }
        return visited.OrderBy(id => id).ToArray();
    }
}
