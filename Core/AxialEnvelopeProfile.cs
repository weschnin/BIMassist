namespace BIMassist.Core;

internal readonly record struct AxialPoint(double Height, double Radius);
internal readonly record struct AxialEnvelopeSegment(AxialPoint Start, AxialPoint End);

/// <summary>
/// Orders a connected, unbranched radial contour. End discs may be added only at its
/// two free ends; gaps and ambiguous branches are never silently bridged.
/// </summary>
internal static class AxialEnvelopeProfile
{
    internal static bool TryOrder(IReadOnlyList<AxialEnvelopeSegment> segments, double tolerance,
        out IReadOnlyList<AxialPoint> profile, out string diagnostic)
    {
        profile = Array.Empty<AxialPoint>();
        diagnostic = "Der Außenumriss ist nicht eindeutig zusammenhängend.";
        if (segments.Count == 0 || tolerance <= 0 || segments.Count > 256)
            return false;

        var nodes = new List<AxialPoint>();
        var edges = new List<(int A, int B)>();
        int Node(AxialPoint point)
        {
            for (int index = 0; index < nodes.Count; index++)
                if (Math.Abs(nodes[index].Height - point.Height) <= tolerance
                    && Math.Abs(nodes[index].Radius - point.Radius) <= tolerance)
                    return index;
            nodes.Add(point);
            return nodes.Count - 1;
        }
        foreach (var segment in segments)
        {
            if (!double.IsFinite(segment.Start.Height) || !double.IsFinite(segment.End.Height)
                || !double.IsFinite(segment.Start.Radius) || !double.IsFinite(segment.End.Radius)
                || segment.Start.Radius <= tolerance || segment.End.Radius <= tolerance)
                return false;
            int a = Node(segment.Start), b = Node(segment.End);
            if (a == b || edges.Any(edge => edge.A == a && edge.B == b || edge.A == b && edge.B == a))
                return false;
            edges.Add((a, b));
        }
        var adjacent = Enumerable.Range(0, nodes.Count).Select(_ => new List<int>()).ToArray();
        foreach (var (a, b) in edges)
        {
            adjacent[a].Add(b);
            adjacent[b].Add(a);
        }
        if (adjacent.Any(neighbours => neighbours.Count > 2)
            || adjacent.Count(neighbours => neighbours.Count == 1) != 2)
            return false;
        var ends = Enumerable.Range(0, nodes.Count).Where(index => adjacent[index].Count == 1).ToArray();
        int start = nodes[ends[0]].Height <= nodes[ends[1]].Height ? ends[0] : ends[1];
        var path = new List<AxialPoint>();
        int previous = -1, current = start;
        while (true)
        {
            path.Add(nodes[current]);
            int next = adjacent[current].FirstOrDefault(index => index != previous, -1);
            if (next == -1) break;
            if (path.Count >= nodes.Count) return false;
            previous = current;
            current = next;
        }
        if (path.Count != nodes.Count || path[^1].Height - path[0].Height <= tolerance)
            return false;
        for (int index = 1; index < path.Count; index++)
            if (path[index].Height < path[index - 1].Height - tolerance)
                return false;
        // More than one ledge at the same height would be ambiguous after revolving.
        for (int index = 2; index < path.Count; index++)
            if (Math.Abs(path[index].Height - path[index - 2].Height) <= tolerance)
                return false;
        profile = path;
        diagnostic = "Zusammenhängender radialer Außenumriss mit zwei definierten Enden.";
        return true;
    }
}
