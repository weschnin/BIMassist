using Autodesk.Revit.DB;
using BIMassist.Core;

namespace BIMassist.Services;

/// <summary>
/// Reconstructs a filled, single-axis outer envelope. Only a continuous radial contour
/// supported by selected bounded surfaces is accepted; exactly two end discs are inferred.
/// Other shapes remain the responsibility of the existing enclosure paths.
/// </summary>
internal static class AxialSurfaceEnvelope
{
    private const int MaxTriangles = 12000;
    private const int AngularSamples = 72;

    internal static bool TryBuild(IReadOnlyList<Face> faces, double tolerance, double shortCurveTolerance,
        out Solid? solid, out string diagnostic)
    {
        solid = null;
        diagnostic = "Keine eindeutige, durchgehende rotationssymmetrische Außenkontur erkannt.";
        if (faces.Count == 0 || tolerance <= 0 || shortCurveTolerance <= 0)
            return false;
        var sideFaces = faces.Where(face => face is CylindricalFace or ConicalFace).ToList();
        if (sideFaces.Count == 0 || sideFaces.Count + faces.Count(face => face is PlanarFace) != faces.Count)
            return false;
        try
        {
            Face first = sideFaces[0];
            XYZ axis = first is CylindricalFace cylinder ? cylinder.Axis.Normalize()
                : ((ConicalFace)first).Axis.Normalize();
            XYZ origin = first is CylindricalFace cylinderOrigin ? cylinderOrigin.Origin
                : ((ConicalFace)first).Origin;
            XYZ radial = Math.Abs(axis.DotProduct(XYZ.BasisZ)) < 0.9
                ? axis.CrossProduct(XYZ.BasisZ).Normalize()
                : axis.CrossProduct(XYZ.BasisX).Normalize();
            XYZ tangent = axis.CrossProduct(radial).Normalize();
            var sides = new List<(Face Face, AxialEnvelopeSegment Segment)>();
            int triangleCount = 0;
            foreach (Face face in sideFaces)
            {
                XYZ direction = face is CylindricalFace c ? c.Axis.Normalize() : ((ConicalFace)face).Axis.Normalize();
                XYZ reference = face is CylindricalFace cOrigin ? cOrigin.Origin : ((ConicalFace)face).Origin;
                if (Math.Abs(direction.DotProduct(axis)) < 1 - 1e-7
                    || (reference - origin - axis * (reference - origin).DotProduct(axis)).GetLength() > tolerance)
                {
                    diagnostic = "Die ausgewählten Mantelflächen besitzen keine gemeinsame Achse.";
                    return false;
                }
                if (!TrySample(face, origin, axis, tolerance, ref triangleCount, out var samples, out diagnostic))
                    return false;
                if (!TryFullCircularLoops(face, origin, axis, radial, tangent, tolerance,
                    out var rings) || rings.Count != 2)
                {
                    diagnostic = "Ein gewählter Mantel hat keine zwei vollständig umlaufenden Kreisränder.";
                    return false;
                }
                double lo = samples.Min(point => point.Height), hi = samples.Max(point => point.Height);
                if (Math.Abs(rings.Min(point => point.Height) - lo) > tolerance
                    || Math.Abs(rings.Max(point => point.Height) - hi) > tolerance)
                    return false;
                if (hi - lo <= shortCurveTolerance)
                {
                    diagnostic = "Ein ausgewählter Mantel besitzt keine messbare axiale Höhe.";
                    return false;
                }
                double meanZ = samples.Average(point => point.Height);
                double meanR = samples.Average(point => point.Radius);
                double divisor = samples.Sum(point => Math.Pow(point.Height - meanZ, 2));
                if (divisor <= 1e-15) return false;
                double slope = samples.Sum(point => (point.Height - meanZ) * (point.Radius - meanR)) / divisor;
                double rLo = meanR + slope * (lo - meanZ), rHi = meanR + slope * (hi - meanZ);
                if (samples.Any(point => Math.Abs(point.Radius - (meanR + slope * (point.Height - meanZ))) > tolerance)
                    || rLo <= shortCurveTolerance || rHi <= shortCurveTolerance
                    || face is CylindricalFace && Math.Abs(rLo - rHi) > tolerance)
                {
                    diagnostic = "Die gekrümmte Fläche lässt sich innerhalb von 1 mm nicht als Zylinder/Kegel rekonstruieren.";
                    return false;
                }
                sides.Add((face, new AxialEnvelopeSegment(new AxialPoint(lo, rLo), new AxialPoint(hi, rHi))));
            }
            double minHeight = sides.Min(side => side.Segment.Start.Height);
            double maxHeight = sides.Max(side => side.Segment.End.Height);
            var segments = new List<AxialEnvelopeSegment>();
            foreach (var side in sides)
            {
                // Several picked patches may cover separate angular sectors of one ring.
                if (!segments.Any(existing => SameSegment(existing, side.Segment, tolerance)))
                    segments.Add(side.Segment);
            }
            foreach (PlanarFace face in faces.OfType<PlanarFace>())
            {
                if (Math.Abs(face.FaceNormal.Normalize().DotProduct(axis)) < 1 - 1e-7
                    || !TrySample(face, origin, axis, tolerance, ref triangleCount, out var samples, out diagnostic))
                {
                    diagnostic = "Eine planare Fläche ist keine zur gemeinsamen Achse senkrechte Stirn- oder Absatzfläche.";
                    return false;
                }
                double height = samples.Average(sample => sample.Height);
                if (samples.Any(sample => Math.Abs(sample.Height - height) > tolerance)
                    || height < minHeight - tolerance || height > maxHeight + tolerance)
                {
                    diagnostic = "Eine planare Auswahl liegt außerhalb des axialen Außenumrisses.";
                    return false;
                }
                // Selected end rings are subsets of the inferred end discs. Only interior
                // shelves belong to the outer radial contour.
                if (height <= minHeight + tolerance || height >= maxHeight - tolerance)
                    continue;
                if (!TryFullCircularLoops(face, origin, axis, radial, tangent, tolerance,
                    out var rings) || rings.Count != 2)
                {
                    diagnostic = "Ein innerer Ringabsatz hat keine zwei vollständig umlaufenden Kreisränder.";
                    return false;
                }
                double inner = rings.Min(ring => ring.Radius);
                double outer = rings.Max(ring => ring.Radius);
                if (inner <= shortCurveTolerance || outer - inner <= shortCurveTolerance)
                {
                    diagnostic = "Eine innere Ebene ist kein eindeutiger äußerer Ringabsatz.";
                    return false;
                }
                segments.Add(new AxialEnvelopeSegment(new AxialPoint(height, inner),
                    new AxialPoint(height, outer)));
            }
            if (!AxialEnvelopeProfile.TryOrder(segments, tolerance, out var profile, out diagnostic))
                return false;
            if (profile[0].Height < minHeight - tolerance || profile[^1].Height > maxHeight + tolerance)
                return false;
            var outline = new List<AxialPoint>(profile.Count + 2)
            {
                new(profile[0].Height, 0)
            };
            outline.AddRange(profile);
            outline.Add(new AxialPoint(profile[^1].Height, 0));
            CurveLoop loop = new CurveLoop();
            for (int i = 0; i < outline.Count; i++)
            {
                AxialPoint a = outline[i], b = outline[(i + 1) % outline.Count];
                XYZ start = origin + axis * a.Height + radial * a.Radius;
                XYZ end = origin + axis * b.Height + radial * b.Radius;
                if (start.DistanceTo(end) <= shortCurveTolerance)
                {
                    diagnostic = "Der Außenumriss enthält eine für Revit zu kurze Kante.";
                    return false;
                }
                loop.Append(Line.CreateBound(start, end));
            }
            Frame frame = new Frame(origin, radial, tangent, axis);
            if (!Frame.CanDefineRevitGeometry(frame)) return false;
            Solid candidate = GeometryCreationUtilities.CreateRevolvedGeometry(frame,
                new List<CurveLoop> { loop }, 0, 2 * Math.PI);
            if (!double.IsFinite(candidate.Volume) || candidate.Volume <= 0
                || !SolidUtils.ComputeIsTopologicallyClosed(candidate)
                || !SolidUtils.ComputeIsGeometricallyClosed(candidate))
            {
                diagnostic = "Revit konnte den gedrehten Außenumriss nicht als geschlossenen Volumenkörper erstellen.";
                return false;
            }
            // A ring missing most of its circumference must never turn into an invented
            // full cylinder or cone. Check each entire lateral interval against bounded picks.
            foreach (var segment in segments.Where(segment => segment.End.Height - segment.Start.Height > tolerance))
            {
                for (int step = 1; step <= 3; step++)
                for (int angle = 0; angle < AngularSamples; angle++)
                {
                    double fraction = step / 4.0;
                    double h = segment.Start.Height + fraction * (segment.End.Height - segment.Start.Height);
                    double r = segment.Start.Radius + fraction * (segment.End.Radius - segment.Start.Radius);
                    XYZ point = origin + axis * h + r *
                        (Math.Cos(2 * Math.PI * angle / AngularSamples) * radial
                        + Math.Sin(2 * Math.PI * angle / AngularSamples) * tangent);
                    if (!sideFaces.Any(face => OnSelectedFace(face, point, tolerance)))
                    {
                        diagnostic = "Der Mantel ist nicht vollständig ausgewählt (fehlender Umfangs- oder Höhenabschnitt).";
                        return false;
                    }
                }
            }
            if (!VerifySelectedShelfCoverage(segments, faces.OfType<PlanarFace>().ToList(),
                origin, axis, radial, tangent, tolerance))
            {
                diagnostic = "Ein waagerechter Ringabsatz ist nicht über seinen vollständigen Umfang und seine Breite ausgewählt.";
                return false;
            }
            // Compare actual bounded source patches to the resulting *outer* solid. This
            // rejects selected inner walls and overlapping/interior pieces, even if the
            // profile itself happens to form a closed solid.
            foreach (Face source in faces)
            {
                Mesh mesh = source.Triangulate(1.0);
                for (int index = 0; index < mesh.NumTriangles; index++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(index);
                    XYZ center = (triangle.get_Vertex(0) + triangle.get_Vertex(1) + triangle.get_Vertex(2)) / 3;
                    IntersectionResult? projected = source.Project(center);
                    XYZ sourcePoint = projected?.XYZPoint ?? center;
                    if (!CheckMantleNormal(source, sourcePoint, origin, axis)
                        || !candidate.Faces.Cast<Face>().Any(face => MatchingOutsideFace(source, face, sourcePoint, tolerance)))
                    {
                        diagnostic = "Mindestens eine gewählte Fläche liegt nicht mit auswärts gerichteter Normale auf der Außenfläche des erzeugten Volumenkörpers.";
                        return false;
                    }
                }
            }
            solid = candidate;
            diagnostic = $"Gefüllter Volumenkörper aus {faces.Count} gewählten Außenflächen erzeugt; " +
                "zwei ebene Enden wurden ergänzt, innere Schachtwände nicht übernommen. " +
                "Geschlossenes positives Volumen und Mantelabdeckung wurden geprüft.";
            return true;
        }
        catch (Exception error)
        {
            diagnostic = "Die axiale Außenkontur konnte nicht sicher aufgebaut werden: " + error.Message;
            return false;
        }
    }

    private static bool SameSegment(AxialEnvelopeSegment a, AxialEnvelopeSegment b, double tolerance)
        => Math.Abs(a.Start.Height - b.Start.Height) <= tolerance
            && Math.Abs(a.End.Height - b.End.Height) <= tolerance
            && Math.Abs(a.Start.Radius - b.Start.Radius) <= tolerance
            && Math.Abs(a.End.Radius - b.End.Radius) <= tolerance;

    private static bool VerifySelectedShelfCoverage(IReadOnlyList<AxialEnvelopeSegment> segments,
        IReadOnlyList<PlanarFace> planars, XYZ origin, XYZ axis, XYZ radial, XYZ tangent, double tolerance)
    {
        foreach (var segment in segments.Where(segment =>
            Math.Abs(segment.Start.Height - segment.End.Height) <= tolerance))
        for (int step = 1; step <= 3; step++)
        for (int angle = 0; angle < AngularSamples; angle++)
        {
            double radius = segment.Start.Radius
                + step / 4.0 * (segment.End.Radius - segment.Start.Radius);
            double radians = 2 * Math.PI * angle / AngularSamples;
            XYZ point = origin + axis * segment.Start.Height
                + radius * (Math.Cos(radians) * radial + Math.Sin(radians) * tangent);
            if (!planars.Any(face => OnSelectedFace(face, point, tolerance)))
                return false;
        }
        return true;
    }

    private static bool TryFullCircularLoops(Face face, XYZ origin, XYZ axis, XYZ radial,
        XYZ tangent, double tolerance, out List<AxialPoint> rings)
    {
        rings = new List<AxialPoint>();
        foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
        {
            var world = new List<XYZ>();
            foreach (Curve curve in loop)
            {
                IList<XYZ> tessellation = curve.Tessellate();
                if (tessellation.Count < 2) return false;
                if (curve is Arc arc)
                {
                    XYZ center = arc.Center - origin;
                    if ((center - axis * center.DotProduct(axis)).GetLength() > tolerance
                        || Math.Abs(Math.Abs(arc.Normal.Normalize().DotProduct(axis)) - 1) > 1e-7)
                        return false;
                }
                else if (curve is not Line)
                    return false;
                foreach (XYZ vertex in tessellation)
                {
                    if (world.Count > 0 && vertex.DistanceTo(world[^1]) <= tolerance * 0.001)
                        continue;
                    world.Add(vertex);
                }
            }
            if (world.Count < 4 || world[0].DistanceTo(world[^1]) > tolerance)
                return false;
            var points = world.Select(vertex =>
            {
                XYZ delta = vertex - origin;
                double height = delta.DotProduct(axis);
                XYZ offset = delta - axis * height;
                return new AxialLoopPoint(Math.Atan2(offset.DotProduct(tangent), offset.DotProduct(radial)),
                    offset.GetLength(), height);
            }).ToArray();
            if (!AxialLoopCoverage.IsCompleteCircle(points, tolerance))
                return false;
            double radius = points.Average(point => point.Radius);
            double heightAtLoop = points.Average(point => point.Height);
            foreach (Curve curve in loop)
            {
                if (curve is Arc arc && Math.Abs(arc.Radius - radius) > tolerance)
                    return false;
                if (curve is Line line)
                {
                    double halfChord = line.Length / 2;
                    if (halfChord >= radius || radius - Math.Sqrt(radius * radius - halfChord * halfChord) > tolerance)
                        return false;
                }
            }
            rings.Add(new AxialPoint(heightAtLoop, radius));
        }
        return rings.Count == 2;
    }

    private static bool CheckMantleNormal(Face source, XYZ point, XYZ origin, XYZ axis)
    {
        if (source is PlanarFace) return true;
        IntersectionResult? hit = source.Project(point);
        if (hit?.UVPoint == null) return false;
        XYZ offset = point - origin;
        XYZ radial = offset - axis * offset.DotProduct(axis);
        return radial.GetLength() > 0
            && source.ComputeNormal(hit.UVPoint).Normalize().DotProduct(radial.Normalize()) > 1e-3;
    }

    private static bool MatchingOutsideFace(Face source, Face result, XYZ point, double tolerance)
    {
        if (!OnSelectedFace(result, point, tolerance)) return false;
        IntersectionResult? sourceHit = source.Project(point);
        IntersectionResult? resultHit = result.Project(point);
        return sourceHit?.UVPoint != null && resultHit?.UVPoint != null
            && source.ComputeNormal(sourceHit.UVPoint).Normalize()
                .DotProduct(result.ComputeNormal(resultHit.UVPoint).Normalize()) > 0.5;
    }

    private static bool TrySample(Face face, XYZ origin, XYZ axis, double tolerance,
        ref int triangleCount, out List<AxialPoint> samples, out string diagnostic)
    {
        samples = new List<AxialPoint>();
        diagnostic = "Eine ausgewählte Fläche hat keine gültige Triangulation.";
        if (face.IsTwoSided || face.Area <= tolerance * tolerance || face.GetEdgesAsCurveLoops().Count == 0)
            return false;
        Mesh mesh = face.Triangulate(1.0);
        if (mesh == null || mesh.NumTriangles == 0 || (triangleCount += mesh.NumTriangles) > MaxTriangles)
        {
            diagnostic = "Die Flächen sind für die geprüfte Rekonstruktion zu komplex oder ungültig.";
            return false;
        }
        for (int i = 0; i < mesh.NumTriangles; i++)
        {
            MeshTriangle triangle = mesh.get_Triangle(i);
            for (int vertex = 0; vertex < 3; vertex++)
            {
                XYZ delta = triangle.get_Vertex(vertex) - origin;
                double height = delta.DotProduct(axis);
                double radius = (delta - axis * height).GetLength();
                if (!double.IsFinite(height) || !double.IsFinite(radius)) return false;
                samples.Add(new AxialPoint(height, radius));
            }
        }
        return true;
    }

    private static bool OnSelectedFace(Face face, XYZ point, double tolerance)
    {
        IntersectionResult? hit = face.Project(point);
        return hit?.XYZPoint != null && hit.UVPoint != null
            && face.IsInside(hit.UVPoint) && hit.XYZPoint.DistanceTo(point) <= tolerance;
    }
}
