using System;
using System.Collections.Generic;

namespace BIMassist.Core;

/// <summary>Cartesian coordinates in the caller's length units.</summary>
public readonly record struct Point3d(double X, double Y, double Z)
{
    public static Point3d operator +(Point3d a, Point3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point3d operator -(Point3d a, Point3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3d operator *(Point3d a, double t) => new(a.X * t, a.Y * t, a.Z * t);
    public static Point3d operator /(Point3d a, double t) => new(a.X / t, a.Y / t, a.Z / t);
    public double Length => Math.Sqrt(Dot(this, this));
    public static double Dot(Point3d a, Point3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Point3d Cross(Point3d a, Point3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

public readonly record struct Triangle3d(Point3d A, Point3d B, Point3d C);
public readonly record struct Segment3d(Point3d Start, Point3d End);

public enum IntersectionDiagnostic
{
    None,
    CoplanarAreaOverlap,
    DegenerateTriangle,
    NumericalAmbiguity
}

/// <summary>
/// A segment is only a candidate intersection curve, not a trimmed face or a solid.
/// A coplanar positive-area overlap cannot be expressed as one segment: inspect Diagnostic
/// before treating an empty Segments list as disjoint. Point-only contacts are discarded.
/// </summary>
public sealed class TriangleIntersectionResult
{
    public IntersectionDiagnostic Diagnostic { get; }
    public IReadOnlyList<Segment3d> Segments { get; }

    internal TriangleIntersectionResult(IntersectionDiagnostic diagnostic, Segment3d? segment = null)
    {
        Diagnostic = diagnostic;
        Segments = segment.HasValue ? Array.AsReadOnly(new[] { segment.Value }) : Array.Empty<Segment3d>();
    }
}

/// <summary>Bounded triangle/triangle intersection only. No face trimming or solid reconstruction.</summary>
public static class FaceIntersectionMath
{
    /// <param name="tolerance">Positive absolute distance tolerance in input coordinate units.</param>
    public static TriangleIntersectionResult Intersect(Triangle3d a, Triangle3d b, double tolerance = 1e-9)
    {
        if (!double.IsFinite(tolerance) || tolerance <= 0 || !Finite(a) || !Finite(b))
            throw new ArgumentException("Coordinates must be finite and tolerance must be positive and finite.");

        var normalA = Point3d.Cross(a.B - a.A, a.C - a.A);
        var normalB = Point3d.Cross(b.B - b.A, b.C - b.A);
        double lengthA = normalA.Length, lengthB = normalB.Length;
        double edgeA = MaxEdge(a), edgeB = MaxEdge(b);
        if (lengthA <= tolerance * edgeA || lengthB <= tolerance * edgeB)
            return new(IntersectionDiagnostic.DegenerateTriangle);

        var unitA = normalA / lengthA;
        var unitB = normalB / lengthB;
        var direction = Point3d.Cross(unitA, unitB);
        double[] distancesB = { Point3d.Dot(b.A - a.A, unitA), Point3d.Dot(b.B - a.A, unitA), Point3d.Dot(b.C - a.A, unitA) };
        if (direction.Length <= 1e-12)
        {
            // This angular threshold also covers near-parallel inputs; do not claim a
            // disjoint result when the two nearly coincident planes might cross.
            if (Math.Abs(distancesB[0]) <= tolerance && Math.Abs(distancesB[1]) <= tolerance
                && Math.Abs(distancesB[2]) <= tolerance)
                return Coplanar(a, b, unitA, tolerance);
            if (distancesB[0] > tolerance && distancesB[1] > tolerance && distancesB[2] > tolerance
                || distancesB[0] < -tolerance && distancesB[1] < -tolerance && distancesB[2] < -tolerance)
                return new(IntersectionDiagnostic.None);
            return new(IntersectionDiagnostic.NumericalAmbiguity);
        }

        double[] distancesA = { Point3d.Dot(a.A - b.A, unitB), Point3d.Dot(a.B - b.A, unitB), Point3d.Dot(a.C - b.A, unitB) };
        if (OneSide(distancesA, tolerance) || OneSide(distancesB, tolerance))
            return new(IntersectionDiagnostic.None);

        var sliceA = Slice(a, distancesA, tolerance);
        var sliceB = Slice(b, distancesB, tolerance);
        if (sliceA.Count == 0 || sliceB.Count == 0)
            return new(IntersectionDiagnostic.None);

        var axis = direction / direction.Length;
        double minA = double.PositiveInfinity, maxA = double.NegativeInfinity;
        double minB = double.PositiveInfinity, maxB = double.NegativeInfinity;
        Point3d startA = default, endA = default;
        foreach (var p in sliceA)
        {
            double t = Point3d.Dot(p - a.A, axis);
            if (t < minA) { minA = t; startA = p; }
            if (t > maxA) { maxA = t; endA = p; }
        }
        foreach (var p in sliceB)
        {
            double t = Point3d.Dot(p - a.A, axis);
            minB = Math.Min(minB, t);
            maxB = Math.Max(maxB, t);
        }
        double lo = Math.Max(minA, minB), hi = Math.Min(maxA, maxB);
        if (hi - lo <= tolerance) return new(IntersectionDiagnostic.None);
        if (maxA - minA <= tolerance) return new(IntersectionDiagnostic.None);
        var first = startA + (endA - startA) * ((lo - minA) / (maxA - minA));
        var last = startA + (endA - startA) * ((hi - minA) / (maxA - minA));
        return new(IntersectionDiagnostic.None, new Segment3d(first, last));
    }

    private static bool Finite(Triangle3d t) => Finite(t.A) && Finite(t.B) && Finite(t.C);
    private static bool Finite(Point3d p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static double MaxEdge(Triangle3d t) => Math.Max((t.B - t.A).Length,
        Math.Max((t.C - t.B).Length, (t.A - t.C).Length));
    private static bool OneSide(double[] d, double tolerance) =>
        d[0] > tolerance && d[1] > tolerance && d[2] > tolerance
        || d[0] < -tolerance && d[1] < -tolerance && d[2] < -tolerance;

    private static List<Point3d> Slice(Triangle3d t, double[] distances, double tolerance)
    {
        Point3d[] vertices = { t.A, t.B, t.C };
        var points = new List<Point3d>(3);
        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3;
            if (Math.Abs(distances[i]) <= tolerance) AddUnique(points, vertices[i], tolerance);
            if ((distances[i] < -tolerance && distances[j] > tolerance)
                || (distances[i] > tolerance && distances[j] < -tolerance))
            {
                double fraction = distances[i] / (distances[i] - distances[j]);
                AddUnique(points, vertices[i] + (vertices[j] - vertices[i]) * fraction, tolerance);
            }
        }
        return points;
    }

    private static void AddUnique(List<Point3d> points, Point3d point, double tolerance)
    {
        foreach (var old in points)
            if ((old - point).Length <= tolerance) return;
        points.Add(point);
    }

    private static TriangleIntersectionResult Coplanar(Triangle3d a, Triangle3d b, Point3d normal, double tolerance)
    {
        // Drop the normal's largest component, keeping a well-conditioned 2D projection.
        int drop = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z) ? 0
            : Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? 1 : 2;
        Point3d origin = a.A;
        double U(Point3d p) => drop == 0 ? p.Y - origin.Y : p.X - origin.X;
        double V(Point3d p) => drop == 2 ? p.Y - origin.Y : p.Z - origin.Z;
        double Cross(Point3d p, Point3d q, Point3d r) =>
            (U(q) - U(p)) * (V(r) - V(p)) - (V(q) - V(p)) * (U(r) - U(p));
        double orientation = Math.Sign(Cross(b.A, b.B, b.C));
        if (orientation == 0) return new(IntersectionDiagnostic.NumericalAmbiguity);

        var polygon = new List<Point3d> { a.A, a.B, a.C };
        Point3d[] clip = { b.A, b.B, b.C };
        for (int side = 0; side < 3 && polygon.Count > 0; side++)
        {
            Point3d p = clip[side], q = clip[(side + 1) % 3];
            double edgeLength = Math.Sqrt(Math.Pow(U(q) - U(p), 2) + Math.Pow(V(q) - V(p), 2));
            double Signed(Point3d x) => orientation * Cross(p, q, x) / edgeLength;
            var input = polygon;
            polygon = new List<Point3d>();
            Point3d previous = input[^1];
            double oldDistance = Signed(previous);
            foreach (var current in input)
            {
                double distance = Signed(current);
                bool wasInside = oldDistance >= -tolerance;
                bool isInside = distance >= -tolerance;
                if (wasInside != isInside)
                {
                    double fraction = oldDistance / (oldDistance - distance);
                    polygon.Add(previous + (current - previous) * Math.Clamp(fraction, 0, 1));
                }
                if (isInside) polygon.Add(current);
                previous = current;
                oldDistance = distance;
            }
        }
        if (polygon.Count == 0) return new(IntersectionDiagnostic.None);
        // Signed shoelace area after projection. A nonzero region needs face trimming,
        // not an arbitrarily chosen boundary segment.
        double twiceArea = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Point3d p = polygon[i], q = polygon[(i + 1) % polygon.Count];
            twiceArea += U(p) * V(q) - U(q) * V(p);
        }
        double scale = Math.Max(MaxEdge(a), MaxEdge(b));
        if (Math.Abs(twiceArea) > 2 * tolerance * scale)
            return new(IntersectionDiagnostic.CoplanarAreaOverlap);

        double best = tolerance;
        Point3d first = default, last = default;
        for (int i = 0; i < polygon.Count; i++)
            for (int j = i + 1; j < polygon.Count; j++)
            {
                double d = (polygon[i] - polygon[j]).Length;
                if (d > best) { best = d; first = polygon[i]; last = polygon[j]; }
            }
        return best > tolerance
            ? new(IntersectionDiagnostic.None, new Segment3d(first, last))
            : new(IntersectionDiagnostic.None);
    }
}
