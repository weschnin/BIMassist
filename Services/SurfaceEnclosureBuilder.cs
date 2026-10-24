using Autodesk.Revit.DB;
using BIMassist.Core;

namespace BIMassist.Services
{
    /// <summary>
    /// Reconstructs a convex enclosure from bounded planar or faceted faces. Never accepts a
    /// positive volume alone: every output patch must still belong to a selected bounded face.
    /// Concave or ambiguous arrangements are deliberately rejected rather than capped.
    /// </summary>
    internal static class SurfaceEnclosureBuilder
    {
        private const int MaxSourceTriangles = 12000;
        private const long MaxTrianglePairs = 2000000;
        private const int MaxCutPlanes = 1500;

        internal static bool TryReuseCompleteSourceSolid(Document document, IReadOnlyList<Reference> references,
            IReadOnlyList<Face> faces, double tolerance, out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = "Die Auswahl entspricht nicht allen Flächen genau eines bestehenden Volumenkörpers.";
            if (references.Count != faces.Count || faces.Count < 2 || tolerance <= 0)
                return false;
            try
            {
                Element? owner = document.GetElement(references[0]);
                if (owner == null || references.Any(reference => document.GetElement(reference)?.Id.Value != owner.Id.Value))
                    return false;
                List<IEnumerable<Triangle3d>> selected = faces.Select(face =>
                    (IEnumerable<Triangle3d>)TriangulateForMatching(face)).ToList();
                if (selected.Sum(triangles => triangles.Count()) > MaxSourceTriangles)
                {
                    diagnostic = "Die Auswahl enthält zu viele Dreiecke für eine sichere Quellkörperprüfung.";
                    return false;
                }
                GeometryElement? geometry = owner.get_Geometry(new Options
                {
                    ComputeReferences = true,
                    DetailLevel = ViewDetailLevel.Fine
                });
                Solid? matchingSolid = null;
                int matches = 0;
                void Inspect(GeometryElement? objects, int depth)
                {
                    if (objects == null || depth > 8) return;
                    foreach (GeometryObject item in objects)
                    {
                        if (item is GeometryInstance instance)
                        {
                            Inspect(instance.GetInstanceGeometry(), depth + 1);
                            continue;
                        }
                        if (item is not Solid source || source.Faces.Size != faces.Count
                            || !double.IsFinite(source.Volume) || source.Volume <= 0)
                            continue;
                        List<IEnumerable<Triangle3d>> candidate = source.Faces.Cast<Face>()
                            .Select(face => (IEnumerable<Triangle3d>)TriangulateForMatching(face)).ToList();
                        if (!ExactSelectedSolidFaces.Match(selected, candidate, tolerance * 0.01))
                            continue;
                        matches++;
                        matchingSolid = source;
                    }
                }
                Inspect(geometry, 0);
                if (matches != 1 || matchingSolid == null)
                {
                    if (matches > 1) diagnostic = "Mehrere Quellkörper passen zur Auswahl; eine eindeutige Zuordnung ist nicht möglich.";
                    return false;
                }
                if (!SolidUtils.ComputeIsTopologicallyClosed(matchingSolid)
                    || !SolidUtils.ComputeIsGeometricallyClosed(matchingSolid))
                {
                    diagnostic = "Der vollständig ausgewählte Quellkörper ist nicht geometrisch geschlossen.";
                    return false;
                }
                solid = SolidUtils.Clone(matchingSolid);
                diagnostic = $"Alle {faces.Count} Flächen eines bereits geschlossenen Quellkörpers sind ausgewählt. " +
                    "Der Körper wurde mit seiner vorhandenen Öffnung und seinen tatsächlichen Innenflächen übernommen.";
                return true;
            }
            catch (Exception error)
            {
                diagnostic = "Die Quellkörperprüfung konnte nicht abgeschlossen werden: " + error.Message;
                return false;
            }
        }

        private static List<Triangle3d> TriangulateForMatching(Face face)
        {
            Mesh mesh = face.Triangulate(1.0);
            var triangles = new List<Triangle3d>();
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                XYZ a = triangle.get_Vertex(0), b = triangle.get_Vertex(1), c = triangle.get_Vertex(2);
                triangles.Add(new Triangle3d(new Point3d(a.X, a.Y, a.Z),
                    new Point3d(b.X, b.Y, b.Z), new Point3d(c.X, c.Y, c.Z)));
            }
            return triangles;
        }

        internal static bool TryBuild(IReadOnlyList<Face> faces, double tolerance, double shortCurveTolerance,
            out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = "Die Flächen konnten nicht zu einer geschlossenen Hülle verbunden werden.";
            if (faces == null || faces.Count < 2 || tolerance <= 0 || shortCurveTolerance <= 0)
                return false;

            try
            {
                // A face with an internal boundary cannot be represented by the convex
                // half-space path: it would fill the opening. Assemble only the actual
                // selected, trimmed face triangles when they already form a closed shell.
                if (faces.Any(face => face.GetEdgesAsCurveLoops().Count > 1
                    && (face is not CylindricalFace || face.GetEdgesAsCurveLoops().Count != 2)))
                    return TryBuildClosedSelectedShell(faces, tolerance, shortCurveTolerance,
                        out solid, out diagnostic);

                List<List<SourceTriangle>> meshes = new();
                List<XYZ> points = new();
                foreach (Face face in faces)
                {
                    if (face.IsTwoSided || face.Area <= tolerance * tolerance)
                    {
                        diagnostic = "Die Auswahl enthält eine degenerierte oder zu kleine Fläche.";
                        return false;
                    }
                    // Vertex/midpoint coverage alone cannot prove a filled polygon does not
                    // bridge a small hole. Periodic cylinders have two legitimate end loops;
                    // all other multi-loop patches require a real trimming kernel.
                    int boundaryLoops = face.GetEdgesAsCurveLoops().Count;
                    if (boundaryLoops == 0 || boundaryLoops > 1
                        && (face is not CylindricalFace || boundaryLoops != 2))
                    {
                        diagnostic = $"Fläche {meshes.Count + 1} ({face.GetType().Name}, {boundaryLoops} Rand-Loops): " +
                            (boundaryLoops == 0
                                ? "Es wurde kein geschlossener Rand gefunden."
                                : "Mehrere Rand-Loops können Öffnungen oder zusätzliche Begrenzungen enthalten. " +
                                  "Diese Geometrie kann die derzeitige konvexe Rekonstruktion nicht sicher trimmen; " +
                                  "es wurde keine Familie erstellt.");
                        return false;
                    }
                    if (face is PlanarFace planar && !IsConvexPlanarBoundary(planar, tolerance))
                    {
                        diagnostic = "Eine planare Fläche hat einen konkaven oder degenerierten Rand. Ohne vollständiges Flächentrimmen kann daraus keine sichere Hülle gebildet werden.";
                        return false;
                    }
                    Mesh mesh = face.Triangulate(1.0);
                    if (mesh == null || mesh.NumTriangles == 0)
                    {
                        diagnostic = "Eine ausgewählte Fläche konnte nicht trianguliert werden.";
                        return false;
                    }
                    List<SourceTriangle> triangles = new();
                    for (int i = 0; i < mesh.NumTriangles; i++)
                    {
                        MeshTriangle triangle = mesh.get_Triangle(i);
                        XYZ a = triangle.get_Vertex(0);
                        XYZ b = triangle.get_Vertex(1);
                        XYZ c = triangle.get_Vertex(2);
                        if ((b - a).CrossProduct(c - a).GetLength() <= tolerance * tolerance * 1e-4)
                            continue;
                        triangles.Add(new SourceTriangle(a, b, c));
                        points.Add(a);
                        points.Add(b);
                        points.Add(c);
                    }
                    if (triangles.Count == 0)
                    {
                        diagnostic = "Eine Fläche besteht nur aus degenerierten Dreiecken.";
                        return false;
                    }
                    meshes.Add(triangles);
                    if (meshes.Sum(part => part.Count) > MaxSourceTriangles)
                    {
                        diagnostic = "Die ausgewählten Flächen sind für die begrenzte, prüfbare Facettierung zu komplex.";
                        return false;
                    }
                }

                // Actual pairwise intersections of the bounded, triangulated input faces are
                // evaluated BEFORE generating a solid. An overlap in the same plane is
                // ambiguous (two selected faces cover the same region) and must not be guessed.
                var connected = new bool[faces.Count];
                var intersectionSegments = new List<Segment3d>();
                int intersectionEdges = 0;
                long checkedPairs = 0;
                for (int first = 0; first < meshes.Count; first++)
                {
                    for (int second = first + 1; second < meshes.Count; second++)
                    {
                        foreach (SourceTriangle a in meshes[first])
                        foreach (SourceTriangle b in meshes[second])
                        {
                            if (!BoundingBoxesOverlap(a, b, tolerance))
                                continue;
                            if (++checkedPairs > MaxTrianglePairs)
                            {
                                diagnostic = "Zu viele mögliche Flächenschnitte; die Rekonstruktion wurde sicher abgebrochen.";
                                return false;
                            }
                            TriangleIntersectionResult intersection = FaceIntersectionMath.Intersect(
                                a.AsTriangle(), b.AsTriangle(),
                                Math.Min(tolerance * 0.1, shortCurveTolerance * 0.1));
                            if (intersection.Diagnostic != IntersectionDiagnostic.None)
                            {
                                diagnostic = $"Flächenschnitt nicht eindeutig ({intersection.Diagnostic}); keine sichere Schnittkante.";
                                return false;
                            }
                            foreach (Segment3d edge in intersection.Segments)
                            {
                                if ((edge.Start - edge.End).Length > shortCurveTolerance)
                                {
                                    intersectionSegments.Add(edge);
                                    intersectionEdges++;
                                    connected[first] = true;
                                    connected[second] = true;
                                }
                            }
                        }
                    }
                }
                if (intersectionEdges == 0 || connected.Any(value => !value))
                {
                    diagnostic = "Nicht jede ausgewählte Fläche hat eine Schnittkante mit einer anderen ausgewählten Fläche. Eine offene Hülle wird nicht ergänzt.";
                    return false;
                }
                int requestedCuts = meshes.Select((mesh, index) =>
                    faces[index] is PlanarFace ? 1 : mesh.Count).Sum();
                if (requestedCuts > MaxCutPlanes)
                {
                    diagnostic = $"Die gekrümmten Flächen erfordern {requestedCuts} Schnitte (Grenze: {MaxCutPlanes}). " +
                        "Die facettierte Konstruktion wurde vor den Revit-Boolean-Operationen abgebrochen.";
                    return false;
                }

                // A single intersection of inward half-spaces reconstructs convex enclosures.
                // Each faceted curved patch contributes its own chord planes. The original
                // bounded faces, not the infinite planes, remain the authority for validation.
                XYZ center = points.Aggregate(XYZ.Zero, (sum, point) => sum + point) / points.Count;
                double minX = points.Min(point => point.X), maxX = points.Max(point => point.X);
                double minY = points.Min(point => point.Y), maxY = points.Max(point => point.Y);
                double minZ = points.Min(point => point.Z), maxZ = points.Max(point => point.Z);
                double span = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
                if (span < shortCurveTolerance)
                {
                    diagnostic = "Die Ausdehnung der Auswahl ist zu klein für einen Revit-Volumenkörper.";
                    return false;
                }
                double margin = Math.Max(span, shortCurveTolerance * 100);
                XYZ origin = new XYZ(minX - margin, minY - margin, minZ - margin);
                XYZ x = origin + XYZ.BasisX * ((maxX - minX) + 2 * margin);
                XYZ xy = x + XYZ.BasisY * ((maxY - minY) + 2 * margin);
                XYZ y = origin + XYZ.BasisY * ((maxY - minY) + 2 * margin);
                CurveLoop outline = new CurveLoop();
                outline.Append(Line.CreateBound(origin, x));
                outline.Append(Line.CreateBound(x, xy));
                outline.Append(Line.CreateBound(xy, y));
                outline.Append(Line.CreateBound(y, origin));
                Solid candidate = GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { outline }, XYZ.BasisZ, (maxZ - minZ) + 2 * margin);
                int cutCount = 0;
                for (int sourceIndex = 0; sourceIndex < meshes.Count; sourceIndex++)
                {
                    // All triangles of one planar face share the same supporting plane.
                    int planeCount = faces[sourceIndex] is PlanarFace ? 1 : meshes[sourceIndex].Count;
                    for (int index = 0; index < planeCount; index++)
                    {
                        SourceTriangle triangle = meshes[sourceIndex][index];
                        XYZ centroid = (triangle.A + triangle.B + triangle.C) / 3;
                        XYZ normal = (triangle.B - triangle.A).CrossProduct(triangle.C - triangle.A);
                        if (normal.GetLength() <= tolerance * tolerance * 1e-4)
                            continue;
                        normal = normal.Normalize();
                        double inward = normal.DotProduct(center - centroid);
                        if (Math.Abs(inward) <= tolerance * 0.01)
                        {
                            diagnostic = "Die Innenseite einer Fläche lässt sich nicht eindeutig bestimmen.";
                            return false;
                        }
                        if (inward < 0)
                            normal = normal.Negate();
                        // CutWithHalfSpace retains the POSITIVE side of Plane.Normal.
                        candidate = BooleanOperationsUtils.CutWithHalfSpace(candidate,
                            Plane.CreateByNormalAndOrigin(normal, centroid));
                        if (candidate == null || candidate.Volume <= Math.Pow(shortCurveTolerance, 3) * 0.001)
                        {
                            diagnostic = "Die Flächen schneiden keinen gemeinsamen geschlossenen, konvexen Innenraum aus.";
                            return false;
                        }
                        if (++cutCount > MaxCutPlanes)
                        {
                            diagnostic = "Zu viele Schnitt-Ebenen für eine sichere Konstruktion.";
                            return false;
                        }
                    }
                }

                if (!VerifyOutputPatches(candidate, faces, tolerance, out diagnostic))
                    return false;
                if (!VerifyIntersectionEdges(candidate, faces, intersectionSegments, tolerance,
                    shortCurveTolerance, out diagnostic))
                    return false;
                solid = candidate;
                diagnostic = $"Familie aus {faces.Count} umschließenden Flächen erzeugt. " +
                    $"{intersectionEdges} facettierte Schnittsegmente ermittelt; " +
                    $"{cutCount} Begrenzungsebenen beschnitten. " +
                    "Gekrümmte Flächen wurden facettiert (Abstand der geprüften Stützpunkte höchstens 1 mm).";
                return true;
            }
            catch (Exception error)
            {
                diagnostic = "Die Rekonstruktion ist fehlgeschlagen; es wurde keine Familie verändert: " + error.Message;
                return false;
            }
        }

        private static bool TryBuildClosedSelectedShell(IReadOnlyList<Face> faces, double tolerance,
            double shortCurveTolerance, out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = "Die gewählten Flächen bilden keine geschlossene Hülle mit erhaltener Öffnung.";
            var sourceTriangles = new List<List<Triangle3d>>();
            var orientedTriangles = new List<(XYZ A, XYZ B, XYZ C)>();
            double sourceArea = 0;
            foreach (Face face in faces)
            {
                int boundaryLoops = face.GetEdgesAsCurveLoops().Count;
                if (face.IsTwoSided || face.Area <= tolerance * tolerance || boundaryLoops == 0)
                {
                    diagnostic = $"Fläche {sourceTriangles.Count + 1} ist degeneriert oder hat keinen gültigen Rand.";
                    return false;
                }
                Mesh mesh = face.Triangulate(1.0);
                if (mesh == null || mesh.NumTriangles == 0)
                {
                    diagnostic = $"Fläche {sourceTriangles.Count + 1} konnte nicht trianguliert werden.";
                    return false;
                }
                var triangles = new List<Triangle3d>();
                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(i);
                    XYZ a = triangle.get_Vertex(0), b = triangle.get_Vertex(1), c = triangle.get_Vertex(2);
                    XYZ cross = (b - a).CrossProduct(c - a);
                    if (cross.GetLength() <= tolerance * tolerance * 1e-4)
                    {
                        diagnostic = $"Fläche {sourceTriangles.Count + 1} enthält ein degeneriertes Dreieck.";
                        return false;
                    }
                    IntersectionResult? projection = face.Project((a + b + c) / 3);
                    if (projection?.UVPoint == null)
                    {
                        diagnostic = $"Die Außenseite von Fläche {sourceTriangles.Count + 1} lässt sich nicht bestimmen.";
                        return false;
                    }
                    XYZ normal = face.ComputeNormal(projection.UVPoint);
                    if (Math.Abs(cross.Normalize().DotProduct(normal)) < 0.1)
                    {
                        diagnostic = $"Die Dreiecksorientierung von Fläche {sourceTriangles.Count + 1} ist mehrdeutig.";
                        return false;
                    }
                    if (cross.DotProduct(normal) < 0)
                        (b, c) = (c, b);
                    triangles.Add(new Triangle3d(new Point3d(a.X, a.Y, a.Z),
                        new Point3d(b.X, b.Y, b.Z), new Point3d(c.X, c.Y, c.Z)));
                    orientedTriangles.Add((a, b, c));
                    sourceArea += cross.GetLength() / 2;
                }
                sourceTriangles.Add(triangles);
                if (orientedTriangles.Count > MaxSourceTriangles)
                {
                    diagnostic = "Die ausgewählten Flächen sind für eine prüfbare Hülle zu komplex.";
                    return false;
                }
            }
            double weldTolerance = Math.Min(tolerance * 0.01, shortCurveTolerance * 0.1);
            ShellValidationResult topology = ClosedTriangleShell.Validate(sourceTriangles, weldTolerance);
            if (!topology.IsValid)
            {
                diagnostic = $"Die tatsächlich ausgewählten Flächen bilden keine geschlossene Hülle: " +
                    $"{topology.OpenEdgeCount} offene Kanten (z. B. fehlende Innenwand), " +
                    $"{topology.NonManifoldEdgeCount} mehrfach belegte Kanten, " +
                    $"{topology.UnpairedOrientationEdgeCount} falsch orientierte Kanten, " +
                    $"{topology.NonManifoldVertexCount} nicht-manifold Ecken, " +
                    $"{topology.ConnectedComponentCount} getrennte Teilhüllen. " +
                    "Fehlende Flächen werden nicht ergänzt; die Öffnung bleibt erhalten.";
                return false;
            }
            TessellatedShapeBuilder builder = new TessellatedShapeBuilder
            {
                Target = TessellatedShapeBuilderTarget.Solid,
                Fallback = TessellatedShapeBuilderFallback.Abort,
                GraphicsStyleId = ElementId.InvalidElementId
            };
            builder.OpenConnectedFaceSet(true);
            foreach (var (a, b, c) in orientedTriangles)
            {
                TessellatedFace patch = new TessellatedFace(new List<XYZ> { a, b, c }, ElementId.InvalidElementId);
                if (!patch.IsValidObject)
                {
                    diagnostic = "Ein ausgewähltes Dreieck ist ungültig; es wird keine Fläche ausgelassen.";
                    return false;
                }
                builder.AddFace(patch);
            }
            builder.CloseConnectedFaceSet();
            builder.Build();
            List<Solid> solids = builder.GetBuildResult().GetGeometricalObjects().OfType<Solid>().ToList();
            if (solids.Count != 1 || solids[0].Volume <= 0
                || !SolidUtils.ComputeIsTopologicallyClosed(solids[0])
                || !SolidUtils.ComputeIsGeometricallyClosed(solids[0]))
            {
                diagnostic = "Revit konnte die gewählten Flächen nicht als einen geschlossenen Volumenkörper aufbauen.";
                return false;
            }
            Solid result = solids[0];
            if (Math.Abs(result.Volume - topology.Volume) > Math.Max(topology.Volume * 1e-5, Math.Pow(tolerance, 3))
                || Math.Abs(result.SurfaceArea - sourceArea) > Math.Max(sourceArea * 1e-5, tolerance * tolerance))
            {
                diagnostic = "Die Revit-Hülle unterscheidet sich in Volumen oder Oberfläche von den ausgewählten Flächen; keine Familie erstellt.";
                return false;
            }
            solid = result;
            diagnostic = $"Geschlossene Hülle aus {faces.Count} ausgewählten Flächen und {orientedTriangles.Count} Dreiecken " +
                "erstellt. Innere Öffnungen bleiben erhalten; keine fehlenden Innenwände ergänzt.";
            return true;
        }

        private static bool VerifyOutputPatches(Solid candidate, IReadOnlyList<Face> sources,
            double tolerance, out string diagnostic)
        {
            bool[] contributed = new bool[sources.Count];
            int patches = 0;
            foreach (Face resultFace in candidate.Faces)
            {
                Mesh mesh = resultFace.Triangulate(1.0);
                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(i);
                    XYZ a = triangle.get_Vertex(0);
                    XYZ b = triangle.get_Vertex(1);
                    XYZ c = triangle.get_Vertex(2);
                    if ((b - a).CrossProduct(c - a).GetLength() <= tolerance * tolerance * 1e-4)
                        continue;
                    XYZ[] samples = { a, b, c, (a + b + c) / 3, (a + b) / 2, (a + c) / 2, (b + c) / 2 };
                    bool matched = false;
                    for (int index = 0; index < sources.Count; index++)
                    {
                        if (!samples.All(sample => OnBoundedFace(sources[index], sample, tolerance)))
                            continue;
                        contributed[index] = true;
                        matched = true;
                    }
                    if (!matched)
                    {
                        diagnostic = "Der Körper hätte eine Fläche außerhalb der ausgewählten, begrenzten Flächen (z. B. eine erfundene Kappe oder eine unbeschnittene Mantelfläche). Es wurde keine Familie erstellt.";
                        return false;
                    }
                    patches++;
                }
            }
            if (patches == 0 || contributed.Any(value => !value))
            {
                diagnostic = "Nicht jede ausgewählte Fläche bildet einen Teil der geschlossenen Außenhülle; keine Familie erstellt.";
                return false;
            }
            diagnostic = "Alle Flächen des resultierenden Volumenkörpers wurden gegen die ausgewählten, begrenzten Flächen geprüft.";
            return true;
        }

        private static bool OnBoundedFace(Face face, XYZ sample, double tolerance)
        {
            IntersectionResult? projection = face.Project(sample);
            if (projection != null && projection.XYZPoint != null && projection.UVPoint != null
                && face.IsInside(projection.UVPoint)
                && projection.XYZPoint.DistanceTo(sample) <= tolerance)
                return true;
            return NearExistingBoundary(face, sample, tolerance);
        }

        private static bool NearExistingBoundary(Face face, XYZ sample, double tolerance)
        {
            foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
            foreach (Curve curve in loop)
            {
                IList<XYZ> vertices = curve.Tessellate();
                for (int index = 1; index < vertices.Count; index++)
                {
                    XYZ start = vertices[index - 1];
                    XYZ end = vertices[index];
                    XYZ delta = end - start;
                    double squared = delta.DotProduct(delta);
                    if (squared <= 0)
                        continue;
                    double t = Math.Clamp((sample - start).DotProduct(delta) / squared, 0, 1);
                    if (sample.DistanceTo(start + delta * t) <= tolerance)
                        return true;
                }
            }
            return false;
        }

        private static bool VerifyIntersectionEdges(Solid candidate, IReadOnlyList<Face> faces,
            IReadOnlyList<Segment3d> intersections, double tolerance, double shortCurveTolerance,
            out string diagnostic)
        {
            int sharedEdges = 0;
            foreach (Edge outputEdge in candidate.Edges)
            {
                IList<XYZ> points = outputEdge.AsCurve().Tessellate();
                if (points.Count < 2)
                    continue;
                XYZ midpoint = outputEdge.AsCurve().Evaluate(0.5, true);
                if (faces.Count(face => OnBoundedFace(face, midpoint, tolerance)) < 2)
                    continue;
                sharedEdges++;
                if (outputEdge.AsCurve().Length <= shortCurveTolerance)
                    continue;
                foreach (XYZ point in new[] { points[0], midpoint, points[^1] })
                {
                    if (intersections.Any(segment => DistanceToSegment(point, segment) <= tolerance * 2))
                        continue;
                    diagnostic = "Eine gemeinsame Kante der erzeugten Geometrie stimmt nicht mit den zuvor ermittelten Schnittlinien überein.";
                    return false;
                }
            }
            if (sharedEdges == 0)
            {
                diagnostic = "Keine Schnittlinien wurden zu gemeinsamen Volumenkörperkanten; es wird keine Familie erzeugt.";
                return false;
            }
            diagnostic = $"{sharedEdges} gemeinsame Kanten stimmen mit den ermittelten Schnittlinien überein.";
            return true;
        }

        private static double DistanceToSegment(XYZ point, Segment3d segment)
        {
            XYZ start = new XYZ(segment.Start.X, segment.Start.Y, segment.Start.Z);
            XYZ end = new XYZ(segment.End.X, segment.End.Y, segment.End.Z);
            XYZ delta = end - start;
            double squared = delta.DotProduct(delta);
            if (squared <= 0)
                return point.DistanceTo(start);
            double parameter = Math.Clamp((point - start).DotProduct(delta) / squared, 0, 1);
            return point.DistanceTo(start + delta * parameter);
        }

        private static bool BoundingBoxesOverlap(SourceTriangle a, SourceTriangle b, double tol)
        {
            return a.MinX <= b.MaxX + tol && b.MinX <= a.MaxX + tol
                && a.MinY <= b.MaxY + tol && b.MinY <= a.MaxY + tol
                && a.MinZ <= b.MaxZ + tol && b.MinZ <= a.MaxZ + tol;
        }

        private static bool IsConvexPlanarBoundary(PlanarFace face, double tolerance)
        {
            List<XYZ> vertices = new();
            foreach (Curve curve in face.GetEdgesAsCurveLoops()[0])
            foreach (XYZ point in curve.Tessellate())
            {
                if (vertices.Count == 0 || vertices[^1].DistanceTo(point) > tolerance * 0.01)
                    vertices.Add(point);
            }
            if (vertices.Count > 1 && vertices[0].DistanceTo(vertices[^1]) <= tolerance * 0.01)
                vertices.RemoveAt(vertices.Count - 1);
            if (vertices.Count < 3)
                return false;
            int orientation = 0;
            for (int index = 0; index < vertices.Count; index++)
            {
                XYZ previous = vertices[(index - 1 + vertices.Count) % vertices.Count];
                XYZ current = vertices[index];
                XYZ next = vertices[(index + 1) % vertices.Count];
                XYZ before = current - previous;
                XYZ after = next - current;
                double turn = before.CrossProduct(after).DotProduct(face.FaceNormal);
                if (Math.Abs(turn) <= tolerance * Math.Max(before.GetLength(), after.GetLength()))
                    continue;
                int sign = Math.Sign(turn);
                if (orientation != 0 && sign != orientation)
                    return false;
                orientation = sign;
            }
            return orientation != 0;
        }

        private readonly record struct SourceTriangle(XYZ A, XYZ B, XYZ C)
        {
            public Triangle3d AsTriangle() => new Triangle3d(ToPoint(A), ToPoint(B), ToPoint(C));
            private static Point3d ToPoint(XYZ xyz) => new Point3d(xyz.X, xyz.Y, xyz.Z);
            public double MinX => Math.Min(A.X, Math.Min(B.X, C.X));
            public double MaxX => Math.Max(A.X, Math.Max(B.X, C.X));
            public double MinY => Math.Min(A.Y, Math.Min(B.Y, C.Y));
            public double MaxY => Math.Max(A.Y, Math.Max(B.Y, C.Y));
            public double MinZ => Math.Min(A.Z, Math.Min(B.Z, C.Z));
            public double MaxZ => Math.Max(A.Z, Math.Max(B.Z, C.Z));
        }
    }
}
