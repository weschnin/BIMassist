using BIMassist.Core;

var a = T(P(0, 0, 0), P(4, 0, 0), P(0, 4, 0));
int passed = 0;
Run("skew crossing returns clipped segment", () =>
{
    var b = T(P(1, -1, -1), P(1, 3, -1), P(1, 1, 1));
    Segment(FaceIntersectionMath.Intersect(a, b), P(1, 0, 0), P(1, 2, 0));
});
Run("argument order and winding are immaterial", () =>
{
    var b = T(P(1, 1, 1), P(1, 3, -1), P(1, -1, -1));
    Segment(FaceIntersectionMath.Intersect(b, a), P(1, 0, 0), P(1, 2, 0));
});
Run("nonparallel line outside triangles returns none", () =>
{
    var b = T(P(5, -1, -1), P(5, 3, -1), P(5, 1, 1));
    None(FaceIntersectionMath.Intersect(a, b));
});
Run("parallel displaced triangles return none", () =>
    None(FaceIntersectionMath.Intersect(a, T(P(0, 0, 2), P(4, 0, 2), P(0, 4, 2)))));
Run("coplanar disjoint triangles return none", () =>
    None(X(a, T(P(5, 0, 0), P(6, 0, 0), P(5, 1, 0)))));
Run("coplanar shared edge returns segment", () =>
    Segment(FaceIntersectionMath.Intersect(a, T(P(0, 0, 0), P(4, 0, 0), P(2, -2, 0))),
        P(0, 0, 0), P(4, 0, 0)));
Run("coplanar partially shared edge returns clipped segment", () =>
    Segment(FaceIntersectionMath.Intersect(a, T(P(1, 0, 0), P(3, 0, 0), P(2, -2, 0))),
        P(1, 0, 0), P(3, 0, 0)));
Run("coplanar proper area overlap is ambiguous, not none", () =>
    Ambiguous(FaceIntersectionMath.Intersect(a, T(P(1, -1, 0), P(3, 1, 0), P(1, 3, 0)))));
Run("coplanar contained triangle is ambiguous", () =>
    Ambiguous(FaceIntersectionMath.Intersect(a, T(P(0.5, 0.5, 0), P(1, 0.5, 0), P(0.5, 1, 0)))));
Run("coplanar identical triangles are ambiguous", () => Ambiguous(FaceIntersectionMath.Intersect(a, a)));
Run("noncoplanar point-only contact is ignored", () =>
    None(FaceIntersectionMath.Intersect(a, T(P(0, 0, 0), P(-1, 0, 1), P(0, -1, 1)))));
Run("coplanar point-only contact is ignored", () =>
    None(FaceIntersectionMath.Intersect(a, T(P(4, 0, 0), P(5, 0, 0), P(4, -1, 0)))));
Run("skew shared edge returns segment", () =>
    Segment(FaceIntersectionMath.Intersect(a, T(P(0, 0, 0), P(4, 0, 0), P(1, 1, 1))),
        P(0, 0, 0), P(4, 0, 0)));
Run("large translated coordinates retain segment", () =>
{
    Point3d O(Point3d p) => P(p.X + 1_000_000, p.Y - 2_000_000, p.Z + 3_000_000);
    var b = T(P(1, -1, -1), P(1, 3, -1), P(1, 1, 1));
    Segment(FaceIntersectionMath.Intersect(T(O(a.A), O(a.B), O(a.C)), T(O(b.A), O(b.B), O(b.C))),
        O(P(1, 0, 0)), O(P(1, 2, 0)));
});
Run("degenerate input is ambiguous, not called disjoint", () =>
    Ambiguous(X(a, T(P(0, 0, 0), P(1, 0, 0), P(2, 0, 0)))));
Console.WriteLine($"PASS {passed} triangle intersection tests");

void Run(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS {name}");
}
static Point3d P(double x, double y, double z) => new(x, y, z);
static Triangle3d T(Point3d a, Point3d b, Point3d c) => new(a, b, c);
static TriangleIntersectionResult X(Triangle3d a, Triangle3d b) => FaceIntersectionMath.Intersect(a, b, 1e-9);
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static bool Near(Point3d p, Point3d q) => Math.Abs(p.X - q.X) < 1e-7
    && Math.Abs(p.Y - q.Y) < 1e-7 && Math.Abs(p.Z - q.Z) < 1e-7;
static void Segment(TriangleIntersectionResult result, Point3d p, Point3d q)
{
    Check(result.Diagnostic == IntersectionDiagnostic.None && result.Segments.Count == 1,
        $"expected segment, got {result.Diagnostic}, {result.Segments.Count} segments");
    var s = result.Segments[0];
    Check((Near(s.Start, p) && Near(s.End, q)) || (Near(s.Start, q) && Near(s.End, p)),
        $"unexpected segment: {s.Start} to {s.End}, expected {p} to {q}");
}
static void None(TriangleIntersectionResult result) =>
    Check(result.Diagnostic == IntersectionDiagnostic.None && result.Segments.Count == 0,
        $"expected no intersection; got {result.Diagnostic}, {result.Segments.Count} segments");
static void Ambiguous(TriangleIntersectionResult result) =>
    Check(result.Diagnostic != IntersectionDiagnostic.None,
        $"expected ambiguity; got {result.Diagnostic}");
