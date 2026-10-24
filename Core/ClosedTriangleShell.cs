using System;
using System.Collections.Generic;

namespace BIMassist.Core;

/// <summary>Topology and signed enclosed-volume diagnostics for a triangle shell.</summary>
public sealed record ShellValidationResult(
    bool IsValid, int OpenEdgeCount, int UnpairedOrientationEdgeCount,
    int NonManifoldEdgeCount, int DegenerateTriangleCount, int ConnectedComponentCount,
    double Volume, int NonManifoldVertexCount = 0);

/// <summary>Validates triangles grouped by their selected source face; never fills missing faces.</summary>
public static class ClosedTriangleShell
{
    /// <param name="faces">Selected faces, each represented by triangles in consistent winding.</param>
    /// <param name="tolerance">Maximum vertex matching distance, in input length units.</param>
    public static ShellValidationResult Validate(IEnumerable<IEnumerable<Triangle3d>> faces, double tolerance = 1e-9)
    {
        if (faces is null) throw new ArgumentNullException(nameof(faces));
        if (!double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        var triangles = new List<Triangle3d>();
        foreach (var face in faces)
        {
            if (face is null) throw new ArgumentException("A selected face cannot be null.", nameof(faces));
            triangles.AddRange(face);
        }
        if (triangles.Count == 0) return new(false, 0, 0, 0, 0, 0, 0);
        var origin = triangles[0].A;
        var vertices = new List<Point3d>();
        var vertexSamples = new List<List<Point3d>>();
        var buckets = new Dictionary<(double, double, double), List<int>>();
        var edges = new Dictionary<(int, int), List<(int triangle, bool forward)>>();
        var triangleVertices = new (int a, int b, int c)[triangles.Count];
        var neighbors = new List<int>[triangles.Count];
        for (int i = 0; i < neighbors.Length; i++) neighbors[i] = new List<int>();
        int degenerate = 0;
        double signedVolume = 0;
        for (int index = 0; index < triangles.Count; index++)
        {
            var triangle = triangles[index];
            if (!Finite(triangle.A) || !Finite(triangle.B) || !Finite(triangle.C))
                throw new ArgumentException("Triangle coordinates must be finite.", nameof(faces));
            int a = Vertex(triangle.A), b = Vertex(triangle.B), c = Vertex(triangle.C);
            triangleVertices[index] = (a, b, c);
            double ab = (triangle.B - triangle.A).Length;
            double bc = (triangle.C - triangle.B).Length;
            double ca = (triangle.A - triangle.C).Length;
            double scale = Math.Max(ab, Math.Max(bc, ca));
            if (a == b || b == c || c == a || scale <= tolerance ||
                Point3d.Cross(triangle.B - triangle.A, triangle.C - triangle.A).Length <= tolerance * scale)
                degenerate++;
            AddEdge(a, b, index);
            AddEdge(b, c, index);
            AddEdge(c, a, index);
            signedVolume += Point3d.Dot(triangle.A - origin,
                Point3d.Cross(triangle.B - origin, triangle.C - origin)) / 6;
        }
        int open = 0, unpaired = 0, nonManifold = 0;
        var fans = new Dictionary<int, List<int>>[vertices.Count];
        var incidentTriangles = new List<int>[vertices.Count];
        for (int i = 0; i < vertices.Count; i++)
        {
            fans[i] = new Dictionary<int, List<int>>();
            incidentTriangles[i] = new List<int>();
        }
        for (int i = 0; i < triangleVertices.Length; i++)
        {
            var (a, b, c) = triangleVertices[i];
            incidentTriangles[a].Add(i);
            if (b != a) incidentTriangles[b].Add(i);
            if (c != a && c != b) incidentTriangles[c].Add(i);
        }
        foreach (var (edge, incidences) in edges)
        {
            if (incidences.Count == 1) open++;
            else if (incidences.Count > 2) nonManifold++;
            else if (incidences[0].forward == incidences[1].forward) unpaired++;
            if (incidences.Count == 2)
            {
                JoinFan(edge.Item1, incidences[0].triangle, incidences[1].triangle);
                JoinFan(edge.Item2, incidences[0].triangle, incidences[1].triangle);
            }
            for (int i = 1; i < incidences.Count; i++)
            {
                int first = incidences[0].triangle, other = incidences[i].triangle;
                neighbors[first].Add(other);
                neighbors[other].Add(first);
            }
        }
        var visited = new bool[triangles.Count];
        var stack = new Stack<int>();
        int components = 0;
        for (int i = 0; i < visited.Length; i++)
        {
            if (visited[i]) continue;
            components++;
            stack.Push(i);
            visited[i] = true;
            while (stack.Count > 0)
                foreach (int next in neighbors[stack.Pop()])
                    if (!visited[next]) { visited[next] = true; stack.Push(next); }
        }
        int nonManifoldVertices = 0;
        for (int v = 0; v < vertices.Count; v++)
        {
            if (incidentTriangles[v].Count == 0) continue;
            var seen = new HashSet<int>();
            stack.Clear();
            stack.Push(incidentTriangles[v][0]);
            seen.Add(incidentTriangles[v][0]);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                if (!fans[v].TryGetValue(current, out var adjacent)) continue;
                foreach (int next in adjacent)
                    if (seen.Add(next)) stack.Push(next);
            }
            if (seen.Count != incidentTriangles[v].Count) nonManifoldVertices++;
        }
        double volume = Math.Abs(signedVolume);
        // A zero-volume double skin is not an enclosed solid; use length tolerance cubed.
        bool positiveVolume = double.IsFinite(volume) && volume > tolerance * tolerance * tolerance;
        return new(open == 0 && unpaired == 0 && nonManifold == 0 && nonManifoldVertices == 0 && degenerate == 0
            && components == 1 && positiveVolume,
            open, unpaired, nonManifold, degenerate, components, volume, nonManifoldVertices);

        void JoinFan(int vertex, int first, int second)
        {
            if (!fans[vertex].TryGetValue(first, out var a)) fans[vertex][first] = a = new List<int>();
            if (!fans[vertex].TryGetValue(second, out var b)) fans[vertex][second] = b = new List<int>();
            a.Add(second);
            b.Add(first);
        }

        int Vertex(Point3d point)
        {
            double x = Math.Floor((point.X - origin.X) / tolerance);
            double y = Math.Floor((point.Y - origin.Y) / tolerance);
            double z = Math.Floor((point.Z - origin.Z) / tolerance);
            // When bucket coordinates exceed exact integer precision, use the slow but
            // correct search rather than risk overlooking a near neighbor.
            if (Math.Abs(x) >= 4503599627370496d || Math.Abs(y) >= 4503599627370496d
                || Math.Abs(z) >= 4503599627370496d)
            {
                for (int i = 0; i < vertices.Count; i++)
                    if (Join(i)) return i;
            }
            else
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (buckets.TryGetValue((x + dx, y + dy, z + dz), out var candidates))
                                foreach (int candidate in candidates)
                                    if (Join(candidate)) return candidate;
            }
            int id = vertices.Count;
            vertices.Add(point);
            vertexSamples.Add(new List<Point3d> { point });
            var key = (x, y, z);
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new List<int>();
            bucket.Add(id);
            return id;

            bool Join(int candidate)
            {
                // A representative-only test can chain two vertices farther apart
                // than tolerance through a third point. Keep cluster diameter bounded.
                foreach (var sample in vertexSamples[candidate])
                    if ((sample - point).Length > tolerance) return false;
                vertexSamples[candidate].Add(point);
                return true;
            }
        }

        void AddEdge(int start, int end, int triangle)
        {
            var key = (Math.Min(start, end), Math.Max(start, end));
            if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<(int, bool)>();
            list.Add((triangle, start < end));
        }
    }

    private static bool Finite(Point3d p) =>
        double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
}
