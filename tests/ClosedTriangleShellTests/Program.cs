using BIMassist.Core;

int passed = 0;
Run("closed cube accepted", () =>
{
    var result = ClosedTriangleShell.Validate(Cube());
    Check(result.IsValid, $"closed cube rejected: {result}");
    Check(result.OpenEdgeCount == 0 && result.NonManifoldEdgeCount == 0 && result.UnpairedOrientationEdgeCount == 0,
        $"closed cube has edge diagnostics: {result}");
    Check(Math.Abs(result.Volume - 1) < 1e-9, $"cube volume: {result.Volume}");
});
Run("missing cube face leaves open edges", () =>
{
    var faces = Cube();
    faces.RemoveAt(5);
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.OpenEdgeCount == 4,
        $"missing wall was not detected: {result}");
});
Run("annular top and bottom retain through tunnel", () =>
{
    var result = ClosedTriangleShell.Validate(HollowPrism());
    Check(result.IsValid, $"closed hollow shell rejected: {result}");
    Check(Math.Abs(result.Volume - 12) < 1e-9,
        $"tunnel should subtract four cubic units, got {result.Volume}");
});
Run("missing inner wall leaves tunnel open", () =>
{
    var faces = HollowPrism();
    faces.RemoveAt(3);
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.OpenEdgeCount == 4,
        $"missing inner wall accepted: {result}");
});
Run("partial inner wall leaves triangle-shaped opening", () =>
{
    var faces = HollowPrism();
    faces[3].RemoveAt(0);
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.OpenEdgeCount == 3,
        $"partial inner wall accepted: {result}");
});
Run("duplicate cap face creates nonmanifold edges", () =>
{
    var faces = HollowPrism();
    faces.Add(new List<Triangle3d>(faces[0]));
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.NonManifoldEdgeCount >= 4,
        $"duplicate cap accepted: {result}");
});
Run("oppositely wound duplicate cap is still nonmanifold", () =>
{
    var faces = Cube();
    faces.Add(faces[1].Select(t => T(t.C, t.B, t.A)).ToList());
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.NonManifoldEdgeCount > 0,
        $"overlapping opposite cap accepted: {result}");
});
Run("two disconnected closed cubes are rejected", () =>
{
    var faces = Cube();
    faces.AddRange(Cube(3));
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.ConnectedComponentCount == 2,
        $"disconnected shell accepted: {result}");
});
Run("reversed triangle violates paired-edge orientation", () =>
{
    var faces = Cube();
    var t = faces[0][0];
    faces[0][0] = T(t.C, t.B, t.A);
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.UnpairedOrientationEdgeCount == 3,
        $"opposing orientations not checked: {result}");
});
Run("global orientation flip remains valid", () =>
{
    var faces = HollowPrism().Select(f => f.Select(t => T(t.C, t.B, t.A)).ToList()).ToList();
    var result = ClosedTriangleShell.Validate(faces);
    Check(result.IsValid && Math.Abs(result.Volume - 12) < 1e-9,
        $"global reversal rejected: {result}");
});
Run("degenerate triangle is rejected", () =>
{
    var faces = Cube();
    faces.Add([T(P(0, 0, 0), P(0.5, 0, 0), P(1, 0, 0))]);
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.DegenerateTriangleCount == 1,
        $"collinear triangle accepted: {result}");
});
Run("vertex gap larger than tolerance is not welded", () =>
{
    var faces = Cube();
    var t = faces[1][0];
    faces[1][0] = T(P(t.A.X + 1e-5, t.A.Y, t.A.Z), t.B, t.C);
    var result = ClosedTriangleShell.Validate(faces, 1e-9);
    Check(!result.IsValid && result.OpenEdgeCount > 0,
        $"large vertex gap erroneously welded: {result}");
});
Run("two far-apart near-origin corners are not transitively welded", () =>
{
    const double tol = 1e-6;
    var faces = Cube();
    for (int i = 0; i < 2; i++)
    {
        var triangle = faces[2][i];
        faces[2][i] = T(P(0.9 * tol, 0, 0), triangle.B, triangle.C);
    }
    var side = faces[5][0];
    faces[5][0] = T(side.A, P(-0.9 * tol, 0, 0), side.C);
    var result = ClosedTriangleShell.Validate(faces, tol);
    Check(!result.IsValid && result.OpenEdgeCount > 0,
        $"transitive weld closed a gap wider than tolerance: {result}");
});
Run("pinched vertex has disconnected one-ring", () =>
{
    var faces = Cube();
    var a = P(0, 0, 0);
    var t = faces[1][0]; faces[1][0] = T(t.A, t.B, a);
    t = faces[1][1]; faces[1][1] = T(t.A, a, t.C);
    t = faces[3][0]; faces[3][0] = T(t.A, t.B, a);
    t = faces[3][1]; faces[3][1] = T(t.A, a, t.C);
    t = faces[4][1]; faces[4][1] = T(t.A, t.B, a);
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.NonManifoldVertexCount > 0,
        $"pinched closed edge mesh accepted: {result}");
});
Run("flat tetrahedron with paired edges has no enclosed volume", () =>
{
    var a = P(0, 0, 0); var b = P(1, 0, 0);
    var c = P(1, 1, 0); var d = P(0, 1, 0);
    List<List<Triangle3d>> faces = [[T(a, c, b)], [T(a, b, d)],
        [T(b, c, d)], [T(c, a, d)]];
    var result = ClosedTriangleShell.Validate(faces);
    Check(!result.IsValid && result.Volume == 0,
        $"zero-volume closed topology accepted: {result}");
});
Run("exact full face selection accepts hollow source solid", () =>
{
    var source = HollowPrism();
    var selected = source.Select(face => face.ToList()).ToList();
    Check(ExactSelectedSolidFaces.Match(selected, source, 1e-7), "matching trimmed source shell rejected");
});
Run("missing interior wall never inherits an unselected source face", () =>
{
    var source = HollowPrism();
    var selected = source.Where((_, index) => index != 3).ToList();
    Check(!ExactSelectedSolidFaces.Match(selected, source, 1e-7), "missing inner wall accepted");
});
Run("duplicate picked face cannot substitute for a missing face", () =>
{
    var source = HollowPrism();
    var selected = source.Select(face => face.ToList()).ToList();
    selected[3] = source[0].ToList();
    Check(!ExactSelectedSolidFaces.Match(selected, source, 1e-7), "duplicate pick accepted");
});
Run("different geometry with same triangle count is rejected", () =>
{
    var source = HollowPrism();
    var selected = source.Select(face => face.ToList()).ToList();
    var t = selected[0][0];
    selected[0][0] = T(P(t.A.X + 0.01, t.A.Y, t.A.Z), t.B, t.C);
    Check(!ExactSelectedSolidFaces.Match(selected, source, 1e-7), "changed boundary accepted");
});
Console.WriteLine($"PASS {passed} closed triangle shell tests");

void Run(string label, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {label}");
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static Point3d P(double x, double y, double z) => new(x, y, z);
static Triangle3d T(Point3d a, Point3d b, Point3d c) => new(a, b, c);
static List<Triangle3d> Quad(Point3d a, Point3d b, Point3d c, Point3d d) =>
    [T(a, b, c), T(a, c, d)];
static List<List<Triangle3d>> Cube(double x = 0)
{
    Point3d A = P(x, 0, 0), B = P(x + 1, 0, 0), C = P(x + 1, 1, 0), D = P(x, 1, 0);
    Point3d E = P(x, 0, 1), F = P(x + 1, 0, 1), G = P(x + 1, 1, 1), H = P(x, 1, 1);
    return [Quad(A, D, C, B), Quad(E, F, G, H), Quad(A, B, F, E),
        Quad(B, C, G, F), Quad(C, D, H, G), Quad(D, A, E, H)];
}
static List<List<Triangle3d>> HollowPrism()
{
    // Four annular trapezoids at each end; the interior remains an actual void.
    Point3d[] outer0 = [P(0, 0, 0), P(4, 0, 0), P(4, 4, 0), P(0, 4, 0)];
    Point3d[] inner0 = [P(1, 1, 0), P(3, 1, 0), P(3, 3, 0), P(1, 3, 0)];
    Point3d[] outer1 = outer0.Select(p => P(p.X, p.Y, 1)).ToArray();
    Point3d[] inner1 = inner0.Select(p => P(p.X, p.Y, 1)).ToArray();
    var faces = new List<List<Triangle3d>>();
    for (int i = 0; i < 4; i++)
    {
        int j = (i + 1) % 4;
        faces.Add(Quad(outer1[i], outer1[j], inner1[j], inner1[i]));
        faces.Add(Quad(outer0[j], outer0[i], inner0[i], inner0[j]));
        faces.Add(Quad(outer0[i], outer0[j], outer1[j], outer1[i]));
        faces.Add(Quad(inner0[j], inner0[i], inner1[i], inner1[j]));
    }
    return faces;
}
