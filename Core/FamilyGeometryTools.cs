using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using System.Windows.Controls;

namespace BIMassist.Core
{
    /// <summary>
    /// Hilfsklasse zur Verarbeitung und Erstellung von Familien-Geometrien in Revit.
    /// Enthält Methoden zum Auslesen, Umwandeln, Vereinen und Einfügen von 3D-Geometrien in Familien.
    /// </summary>
    internal static class FamilyGeometryTools
    {
        /// <summary>
        /// Liest die Geometrie-Objekte (Solids) einer Liste von Elementen aus einer Familie aus.
        /// Optional können die gefundenen Solids zu einem einzigen Solid vereinigt werden.
        /// </summary>
        /// <param name="doc">Das aktuelle Revit-Dokument.</param>
        /// <param name="solidsElementIds">Liste der ElementIds, aus denen Geometrie ausgelesen werden soll.</param>
        /// <param name="vereinigung">Gibt an, ob die gefundenen Solids vereinigt werden sollen (Union).</param>
        /// <returns>Liste der ausgelesenen Solids, oder null bei Fehler.</returns>
        internal static IList<GeometryObject> ReadGeometryFromFamily(Document doc, IList<ElementId> solidsElementIds)
        {
            // Optionen für die Geometrie-Auslese im aktuellen View
            Options opt = new Options() { View = doc.ActiveView, ComputeReferences = true };
            List<GeometryObject> geometryObjs = new List<GeometryObject>();
            List<string> meshFehler = new List<string>();

            foreach (ElementId elementId in solidsElementIds)
            {
                Element element = doc.GetElement(elementId);

                // Geometrieobjekte (Solids & Meshes) des Elements auslesen
                var geometryObjects = GetAllGeometryObjects(element, opt);

                foreach (var geomObject in geometryObjects)
                {
                    // Direktes Hinzufügen von Solids mit Volumen > 0
                    if (geomObject is Solid s && s.Volume > 0)
                    {
                        geometryObjs.Add(s);
                    }
                    // Umwandlung von Mesh zu Solid mit robusterer Topologie-/Orientierungsbehandlung
                    else if (geomObject is Mesh mesh)
                    {
                        if (TryConvertMeshToSolid(mesh, doc.Application.ShortCurveTolerance, out Solid solid, out string failureReason))
                        {
                            geometryObjs.Add(solid);
                        }
                        else if (mesh.NumTriangles > 0)
                        {
                            // Solid-Konvertierung kann bei sehr dichten STL-/Import-Meshes an Revit-Kernelgrenzen scheitern.
                            // Damit der Nutzer trotzdem eine verwertbare Geometrie bekommt, übernehmen wir das Original-Mesh
                            // als DirectShape-Fallback in die Familie/Projektgeometrie.
                            geometryObjs.Add(mesh);
                            meshFehler.Add($"Element {elementId}: Solid nicht möglich; Mesh wurde als DirectShape übernommen.");
                        }
                    }
                }
            }

            // User-Meldung, falls Meshes nicht in Solid umwandelbar sind
            if (meshFehler.Count > 0)
            {
                string details = string.Join("\n", meshFehler.Take(5));
                if (meshFehler.Count > 5)
                    details += $"\n... und {meshFehler.Count - 5} weitere Mesh-Fehler.";

                TaskDialog.Show("BIMassist - Mesh-Fallback",
                    $"{meshFehler.Count} Mesh-Objekt(e) konnten nicht als Solid erzeugt werden.\n" +
                    "Sie wurden stattdessen als DirectShape übernommen.\n\n" +
                    details);
            }

            if (geometryObjs.Count > 0)
                return geometryObjs;
            else
            {
                Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Es wurden keine (gültigen) Geometrien erkannt");
                return null;
            }
        }

        private const double MinMeshVertexTolerance = 1e-6;
        private const double MeshWeldSafetyFactor = 0.25;

        private readonly record struct MeshVertexKey(long X, long Y, long Z)
        {
            internal static MeshVertexKey FromXyz(XYZ point, double tolerance)
            {
                return new MeshVertexKey(
                    (long)Math.Round(point.X / tolerance),
                    (long)Math.Round(point.Y / tolerance),
                    (long)Math.Round(point.Z / tolerance));
            }
        }

        private readonly record struct MeshTriangleInfo(MeshVertexKey AKey, MeshVertexKey BKey, MeshVertexKey CKey, XYZ A, XYZ B, XYZ C)
        {
            internal bool HasDirectedEdge(MeshVertexKey from, MeshVertexKey to, bool flipped)
            {
                if (!flipped)
                {
                    return (AKey.Equals(from) && BKey.Equals(to))
                        || (BKey.Equals(from) && CKey.Equals(to))
                        || (CKey.Equals(from) && AKey.Equals(to));
                }

                return (AKey.Equals(to) && BKey.Equals(from))
                    || (BKey.Equals(to) && CKey.Equals(from))
                    || (CKey.Equals(to) && AKey.Equals(from));
            }

            internal IList<XYZ> GetVertices(bool flipped)
            {
                return flipped
                    ? new List<XYZ> { A, C, B }
                    : new List<XYZ> { A, B, C };
            }

            internal IList<MeshVertexKey> GetVertexKeys(bool flipped)
            {
                return flipped
                    ? new List<MeshVertexKey> { AKey, CKey, BKey }
                    : new List<MeshVertexKey> { AKey, BKey, CKey };
            }
        }

        private readonly record struct MeshEdgeKey(MeshVertexKey V1, MeshVertexKey V2)
        {
            internal static MeshEdgeKey Create(MeshVertexKey a, MeshVertexKey b)
            {
                int cmp = Compare(a, b);
                return cmp <= 0 ? new MeshEdgeKey(a, b) : new MeshEdgeKey(b, a);
            }

            internal static int Compare(MeshVertexKey a, MeshVertexKey b)
            {
                int c = a.X.CompareTo(b.X);
                if (c != 0) return c;
                c = a.Y.CompareTo(b.Y);
                if (c != 0) return c;
                return a.Z.CompareTo(b.Z);
            }
        }

        private readonly record struct MeshTriangleSetKey(MeshVertexKey V1, MeshVertexKey V2, MeshVertexKey V3)
        {
            internal static MeshTriangleSetKey Create(MeshVertexKey a, MeshVertexKey b, MeshVertexKey c)
            {
                MeshVertexKey[] keys = new[] { a, b, c };
                Array.Sort(keys, MeshVertexKeyComparer.Instance);
                return new MeshTriangleSetKey(keys[0], keys[1], keys[2]);
            }
        }

        private sealed class MeshVertexKeyComparer : IComparer<MeshVertexKey>
        {
            internal static readonly MeshVertexKeyComparer Instance = new MeshVertexKeyComparer();

            public int Compare(MeshVertexKey x, MeshVertexKey y)
            {
                return MeshEdgeKey.Compare(x, y);
            }
        }

        private static bool TryConvertMeshToSolid(Mesh mesh, double shortCurveTolerance, out Solid solid, out string failureReason)
        {
            solid = null;
            failureReason = string.Empty;

            double weldTolerance = GetEffectiveMeshWeldTolerance(shortCurveTolerance);

            if (mesh == null || mesh.NumTriangles <= 0)
            {
                failureReason = "Leeres Mesh ohne Dreiecke.";
                return false;
            }

            if (TryBuildSolidDirect(mesh, out solid))
                return true;

            List<MeshTriangleInfo> triangles = ExtractMeshTriangles(mesh, weldTolerance);
            if (triangles.Count == 0)
            {
                failureReason = "Keine gültigen Dreiecke nach Vertex-Verschweißung gefunden.";
                return false;
            }

            Dictionary<MeshEdgeKey, List<int>> edgeToTriangles = BuildEdgeTriangleMap(triangles);
            var components = FindConnectedTriangleComponents(triangles.Count, edgeToTriangles);
            if (components.Count == 0)
            {
                failureReason = "Keine zusammenhängenden Mesh-Komponenten erkannt.";
                return false;
            }

            List<Solid> componentSolids = new List<Solid>();
            List<string> componentErrors = new List<string>();

            foreach (var component in components)
            {
                if (!TryBuildSolidFromComponent(triangles, component, edgeToTriangles, shortCurveTolerance, out Solid componentSolid, out string componentReason))
                {
                    componentErrors.Add(componentReason);
                    continue;
                }

                componentSolids.Add(componentSolid);
            }

            if (componentSolids.Count == 0)
            {
                failureReason = componentErrors.Count > 0
                    ? string.Join(" | ", componentErrors)
                    : "Unbekannter Fehler beim Erzeugen eines Solids aus dem Mesh.";
                return false;
            }

            solid = componentSolids[0];
            for (int i = 1; i < componentSolids.Count; i++)
            {
                try
                {
                    solid = BooleanOperationsUtils.ExecuteBooleanOperation(solid, componentSolids[i], BooleanOperationsType.Union);
                }
                catch (Exception ex)
                {
                    failureReason = $"Teil-Solids konnten nicht vereinigt werden: {ex.Message}";
                    return false;
                }
            }

            if (solid == null || solid.Volume <= 0)
            {
                failureReason = "Solid-Erzeugung lieferte kein Volumen.";
                return false;
            }

            return true;
        }


        internal static bool TryConvertMeshesToSolid(IEnumerable<Mesh> meshes, double shortCurveTolerance, out Solid solid, out string failureReason)
        {
            solid = null;
            failureReason = string.Empty;

            List<Mesh> meshList = meshes?
                .Where(m => m != null && m.NumTriangles > 0)
                .ToList() ?? new List<Mesh>();

            if (meshList.Count == 0)
            {
                failureReason = "Es wurden keine triangulierten Flächen mit Dreiecken gefunden.";
                return false;
            }

            if (TryBuildSolidDirect(meshList, out solid))
                return true;

            double weldTolerance = GetEffectiveMeshWeldTolerance(shortCurveTolerance);
            List<MeshTriangleInfo> triangles = ExtractMeshTriangles(meshList, weldTolerance);
            if (triangles.Count == 0)
            {
                failureReason = "Keine gültigen Dreiecke nach der Triangulation/Verschweißung gefunden.";
                return false;
            }

            Dictionary<MeshEdgeKey, List<int>> edgeToTriangles = BuildEdgeTriangleMap(triangles);
            var components = FindConnectedTriangleComponents(triangles.Count, edgeToTriangles);
            if (components.Count == 0)
            {
                failureReason = "Keine zusammenhängenden Dreiecks-Komponenten erkannt.";
                return false;
            }

            List<Solid> componentSolids = new List<Solid>();
            List<string> componentErrors = new List<string>();

            foreach (var component in components)
            {
                if (!TryBuildSolidFromComponent(triangles, component, edgeToTriangles, shortCurveTolerance, out Solid componentSolid, out string componentReason))
                {
                    componentErrors.Add(componentReason);
                    continue;
                }

                componentSolids.Add(componentSolid);
            }

            if (componentSolids.Count == 0)
            {
                failureReason = componentErrors.Count > 0
                    ? string.Join(" | ", componentErrors)
                    : "Unbekannter Fehler beim Erzeugen eines Solids aus den triangulierten Flächen.";
                return false;
            }

            solid = componentSolids[0];
            for (int i = 1; i < componentSolids.Count; i++)
            {
                try
                {
                    solid = BooleanOperationsUtils.ExecuteBooleanOperation(solid, componentSolids[i], BooleanOperationsType.Union);
                }
                catch (Exception ex)
                {
                    failureReason = $"Teil-Solids konnten nicht vereinigt werden: {ex.Message}";
                    return false;
                }
            }

            if (solid == null || solid.Volume <= 0)
            {
                failureReason = "Solid-Erzeugung aus triangulierten Flächen lieferte kein Volumen.";
                return false;
            }

            return true;
        }

        private static double GetEffectiveMeshWeldTolerance(double shortCurveTolerance)
        {
            if (shortCurveTolerance > 0)
                return Math.Max(MinMeshVertexTolerance, shortCurveTolerance * MeshWeldSafetyFactor);

            return MinMeshVertexTolerance;
        }

        private static double GetTriangleArea(XYZ a, XYZ b, XYZ c)
        {
            return 0.5 * b.Subtract(a).CrossProduct(c.Subtract(a)).GetLength();
        }

        private static XYZ GetComponentCenter(List<MeshTriangleInfo> triangles, List<int> component)
        {
            XYZ sum = XYZ.Zero;
            int count = 0;
            HashSet<MeshVertexKey> seen = new HashSet<MeshVertexKey>();

            foreach (int triIndex in component)
            {
                var tri = triangles[triIndex];
                if (seen.Add(tri.AKey))
                {
                    sum += tri.A;
                    count++;
                }
                if (seen.Add(tri.BKey))
                {
                    sum += tri.B;
                    count++;
                }
                if (seen.Add(tri.CKey))
                {
                    sum += tri.C;
                    count++;
                }
            }

            return count > 0 ? sum / count : XYZ.Zero;
        }

        private static IList<XYZ> PrepareVertices(IList<XYZ> vertices, XYZ offset, double scaleFactor = 1.0)
        {
            if (Math.Abs(scaleFactor - 1.0) < 1e-9)
                return vertices.Select(v => v - offset).ToList();

            return vertices.Select(v => (v - offset) * scaleFactor).ToList();
        }

        private static Solid RestoreSolidToOriginalSpace(Solid solid, XYZ componentCenter, double scaleFactor)
        {
            if (solid == null)
                return null;

            if (Math.Abs(scaleFactor - 1.0) >= 1e-9)
                solid = SolidUtils.CreateTransformed(solid, Transform.Identity.ScaleBasis(1.0 / scaleFactor));

            return SolidUtils.CreateTransformed(solid, Transform.CreateTranslation(componentCenter));
        }

        private static bool TryCreateRobustTrianglePlane(XYZ a, XYZ b, XYZ c, out Plane plane)
        {
            plane = null;

            try
            {
                plane = Plane.CreateByThreePoints(a, b, c);
                return true;
            }
            catch
            {
                // Bei sehr flachen/kleinen Dreiecken akzeptiert Revit manchmal drei Punkte
                // nicht als eindeutige Ebene, obwohl das Kreuzprodukt noch eine nutzbare
                // Dreiecks-Normale liefert. In diesem Fall die Ebene explizit aus Normale
                // und Ursprung erzeugen, damit das Face nicht aus der geschlossenen Hülle
                // entfernt wird.
            }

            try
            {
                XYZ normal = b.Subtract(a).CrossProduct(c.Subtract(a));
                double normalLength = normal.GetLength();
                if (normalLength <= 1e-12)
                    return false;

                plane = Plane.CreateByNormalAndOrigin(normal / normalLength, a);
                return true;
            }
            catch
            {
                plane = null;
                return false;
            }
        }

        private static bool TryBuildSolidDirect(Mesh mesh, out Solid solid)
        {
            solid = null;
            try
            {
                var builder = new TessellatedShapeBuilder
                {
                    Target = TessellatedShapeBuilderTarget.Solid,
                    Fallback = TessellatedShapeBuilderFallback.Abort,
                    GraphicsStyleId = ElementId.InvalidElementId
                };
                builder.OpenConnectedFaceSet(true);

                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(i);
                    var tessFace = new TessellatedFace(
                        new List<XYZ>
                        {
                            triangle.get_Vertex(0),
                            triangle.get_Vertex(1),
                            triangle.get_Vertex(2)
                        },
                        ElementId.InvalidElementId);

                    if (builder.DoesFaceHaveEnoughLoopsAndVertices(tessFace))
                        builder.AddFace(tessFace);
                }

                builder.CloseConnectedFaceSet();
                builder.Build();

                solid = builder.GetBuildResult().GetGeometricalObjects().OfType<Solid>().FirstOrDefault(s => s.Volume > 0);
                return solid != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildSolidDirect(IList<Mesh> meshes, out Solid solid)
        {
            solid = null;
            try
            {
                var builder = new TessellatedShapeBuilder
                {
                    Target = TessellatedShapeBuilderTarget.Solid,
                    Fallback = TessellatedShapeBuilderFallback.Abort,
                    GraphicsStyleId = ElementId.InvalidElementId
                };
                builder.OpenConnectedFaceSet(true);

                foreach (Mesh mesh in meshes)
                {
                    for (int i = 0; i < mesh.NumTriangles; i++)
                    {
                        MeshTriangle triangle = mesh.get_Triangle(i);
                        var tessFace = new TessellatedFace(
                            new List<XYZ>
                            {
                                triangle.get_Vertex(0),
                                triangle.get_Vertex(1),
                                triangle.get_Vertex(2)
                            },
                            ElementId.InvalidElementId);

                        if (builder.DoesFaceHaveEnoughLoopsAndVertices(tessFace))
                            builder.AddFace(tessFace);
                    }
                }

                builder.CloseConnectedFaceSet();
                builder.Build();

                solid = builder.GetBuildResult().GetGeometricalObjects().OfType<Solid>().FirstOrDefault(s => s.Volume > 0);
                return solid != null;
            }
            catch
            {
                return false;
            }
        }

        private static List<MeshTriangleInfo> ExtractMeshTriangles(Mesh mesh, double weldTolerance)
        {
            Dictionary<MeshVertexKey, XYZ> canonicalVertices = new Dictionary<MeshVertexKey, XYZ>();
            HashSet<MeshTriangleSetKey> uniqueTriangles = new HashSet<MeshTriangleSetKey>();
            List<MeshTriangleInfo> triangles = new List<MeshTriangleInfo>();

            XYZ Canonicalize(XYZ point)
            {
                var key = MeshVertexKey.FromXyz(point, weldTolerance);
                if (!canonicalVertices.TryGetValue(key, out XYZ canonical))
                {
                    canonical = point;
                    canonicalVertices[key] = canonical;
                }
                return canonical;
            }

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                XYZ a = Canonicalize(triangle.get_Vertex(0));
                XYZ b = Canonicalize(triangle.get_Vertex(1));
                XYZ c = Canonicalize(triangle.get_Vertex(2));

                var aKey = MeshVertexKey.FromXyz(a, weldTolerance);
                var bKey = MeshVertexKey.FromXyz(b, weldTolerance);
                var cKey = MeshVertexKey.FromXyz(c, weldTolerance);

                if (aKey.Equals(bKey) || bKey.Equals(cKey) || cKey.Equals(aKey))
                    continue;

                var triangleKey = MeshTriangleSetKey.Create(aKey, bKey, cKey);
                if (!uniqueTriangles.Add(triangleKey))
                    continue;

                triangles.Add(new MeshTriangleInfo(aKey, bKey, cKey, a, b, c));
            }

            return triangles;
        }

        private static List<MeshTriangleInfo> ExtractMeshTriangles(IEnumerable<Mesh> meshes, double weldTolerance)
        {
            Dictionary<MeshVertexKey, XYZ> canonicalVertices = new Dictionary<MeshVertexKey, XYZ>();
            HashSet<MeshTriangleSetKey> uniqueTriangles = new HashSet<MeshTriangleSetKey>();
            List<MeshTriangleInfo> triangles = new List<MeshTriangleInfo>();

            XYZ Canonicalize(XYZ point)
            {
                var key = MeshVertexKey.FromXyz(point, weldTolerance);
                if (!canonicalVertices.TryGetValue(key, out XYZ canonical))
                {
                    canonical = point;
                    canonicalVertices[key] = canonical;
                }
                return canonical;
            }

            foreach (Mesh mesh in meshes)
            {
                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(i);
                    XYZ a = Canonicalize(triangle.get_Vertex(0));
                    XYZ b = Canonicalize(triangle.get_Vertex(1));
                    XYZ c = Canonicalize(triangle.get_Vertex(2));

                    var aKey = MeshVertexKey.FromXyz(a, weldTolerance);
                    var bKey = MeshVertexKey.FromXyz(b, weldTolerance);
                    var cKey = MeshVertexKey.FromXyz(c, weldTolerance);

                    if (aKey.Equals(bKey) || bKey.Equals(cKey) || cKey.Equals(aKey))
                        continue;

                    var triangleKey = MeshTriangleSetKey.Create(aKey, bKey, cKey);
                    if (!uniqueTriangles.Add(triangleKey))
                        continue;

                    triangles.Add(new MeshTriangleInfo(aKey, bKey, cKey, a, b, c));
                }
            }

            return triangles;
        }

        private static Dictionary<MeshEdgeKey, List<int>> BuildEdgeTriangleMap(List<MeshTriangleInfo> triangles)
        {
            Dictionary<MeshEdgeKey, List<int>> edgeToTriangles = new Dictionary<MeshEdgeKey, List<int>>();

            void AddEdge(MeshEdgeKey edge, int triangleIndex)
            {
                if (!edgeToTriangles.TryGetValue(edge, out List<int> list))
                {
                    list = new List<int>();
                    edgeToTriangles[edge] = list;
                }
                list.Add(triangleIndex);
            }

            for (int i = 0; i < triangles.Count; i++)
            {
                var tri = triangles[i];
                AddEdge(MeshEdgeKey.Create(tri.AKey, tri.BKey), i);
                AddEdge(MeshEdgeKey.Create(tri.BKey, tri.CKey), i);
                AddEdge(MeshEdgeKey.Create(tri.CKey, tri.AKey), i);
            }

            return edgeToTriangles;
        }

        private static List<List<int>> FindConnectedTriangleComponents(int triangleCount, Dictionary<MeshEdgeKey, List<int>> edgeToTriangles)
        {
            List<int>[] adjacency = Enumerable.Range(0, triangleCount).Select(_ => new List<int>()).ToArray();

            foreach (var kvp in edgeToTriangles)
            {
                List<int> list = kvp.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        adjacency[list[i]].Add(list[j]);
                        adjacency[list[j]].Add(list[i]);
                    }
                }
            }

            List<List<int>> components = new List<List<int>>();
            bool[] visited = new bool[triangleCount];
            for (int i = 0; i < triangleCount; i++)
            {
                if (visited[i]) continue;
                List<int> component = new List<int>();
                Queue<int> queue = new Queue<int>();
                queue.Enqueue(i);
                visited[i] = true;

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(current);
                    foreach (int next in adjacency[current])
                    {
                        if (visited[next]) continue;
                        visited[next] = true;
                        queue.Enqueue(next);
                    }
                }

                components.Add(component);
            }

            return components;
        }

        private static bool TryBuildSolidFromComponent(List<MeshTriangleInfo> triangles, List<int> component, Dictionary<MeshEdgeKey, List<int>> edgeToTriangles, double shortCurveTolerance, out Solid solid, out string reason)
        {
            solid = null;
            reason = string.Empty;

            int boundaryEdges = 0;
            int nonManifoldEdges = 0;
            HashSet<int> componentSet = new HashSet<int>(component);
            HashSet<MeshEdgeKey> componentEdges = new HashSet<MeshEdgeKey>();
            foreach (int triIndex in component)
            {
                var tri = triangles[triIndex];
                componentEdges.Add(MeshEdgeKey.Create(tri.AKey, tri.BKey));
                componentEdges.Add(MeshEdgeKey.Create(tri.BKey, tri.CKey));
                componentEdges.Add(MeshEdgeKey.Create(tri.CKey, tri.AKey));
            }

            foreach (var edge in componentEdges)
            {
                int countInComponent = edgeToTriangles[edge].Count(idx => componentSet.Contains(idx));
                if (countInComponent == 1) boundaryEdges++;
                else if (countInComponent > 2) nonManifoldEdges++;
            }

            if (boundaryEdges > 0)
            {
                reason = $"Offene Komponente mit {boundaryEdges} Randkante(n).";
                return false;
            }

            if (nonManifoldEdges > 0)
            {
                reason = $"Nicht-mannigfaltige Komponente mit {nonManifoldEdges} problematischen Kante(n).";
                return false;
            }

            Dictionary<int, bool> orientation = new Dictionary<int, bool>();
            Queue<int> queue = new Queue<int>();
            int seed = component[0];
            orientation[seed] = false;
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                var tri = triangles[current];
                bool currentFlip = orientation[current];
                foreach (var edge in new[]
                {
                    MeshEdgeKey.Create(tri.AKey, tri.BKey),
                    MeshEdgeKey.Create(tri.BKey, tri.CKey),
                    MeshEdgeKey.Create(tri.CKey, tri.AKey)
                })
                {
                    List<int> sharedTriangles = edgeToTriangles[edge].Where(idx => componentSet.Contains(idx)).ToList();
                    foreach (int neighbor in sharedTriangles)
                    {
                        if (neighbor == current) continue;
                        bool currentHasEdge = tri.HasDirectedEdge(edge.V1, edge.V2, currentFlip);
                        bool neighborHasEdgeWithoutFlip = triangles[neighbor].HasDirectedEdge(edge.V1, edge.V2, false);
                        bool neighborFlip = neighborHasEdgeWithoutFlip == currentHasEdge;

                        if (orientation.TryGetValue(neighbor, out bool existingFlip))
                        {
                            if (existingFlip != neighborFlip)
                            {
                                reason = "Inkonsistente Dreiecksorientierung im Mesh erkannt.";
                                return false;
                            }
                            continue;
                        }

                        orientation[neighbor] = neighborFlip;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            double signedVolume = 0;
            foreach (int triIndex in component)
            {
                var tri = triangles[triIndex];
                var vertices = tri.GetVertices(orientation[triIndex]);
                XYZ a = vertices[0];
                XYZ b = vertices[1];
                XYZ c = vertices[2];
                signedVolume += a.DotProduct(b.CrossProduct(c)) / 6.0;
            }

            bool invertAll = signedVolume < 0;
            double minEdgeLength = shortCurveTolerance > 0 ? shortCurveTolerance * 1.01 : MinMeshVertexTolerance;
            double minTriangleArea = minEdgeLength * minEdgeLength * 0.25;
            int shortEdgeTriangles = 0;
            int tinyAreaTriangles = 0;
            double minObservedEdgeLength = double.MaxValue;
            XYZ componentCenter = GetComponentCenter(triangles, component);

            foreach (int triIndex in component)
            {
                var tri = triangles[triIndex];
                bool flipped = orientation[triIndex] ^ invertAll;
                var vertices = tri.GetVertices(flipped);
                XYZ a = vertices[0];
                XYZ b = vertices[1];
                XYZ c = vertices[2];

                double ab = a.DistanceTo(b);
                double bc = b.DistanceTo(c);
                double ca = c.DistanceTo(a);
                minObservedEdgeLength = Math.Min(minObservedEdgeLength, Math.Min(ab, Math.Min(bc, ca)));

                if (ab <= minEdgeLength || bc <= minEdgeLength || ca <= minEdgeLength)
                    shortEdgeTriangles++;

                if (GetTriangleArea(a, b, c) <= minTriangleArea)
                    tinyAreaTriangles++;
            }

            double geometryDrivenScaleFactor = 1.0;
            if (shortEdgeTriangles > 0 && minObservedEdgeLength < double.MaxValue && minObservedEdgeLength > 0)
                geometryDrivenScaleFactor = Math.Max(10.0, (minEdgeLength / minObservedEdgeLength) * 8.0);

            var scaleAttempts = new List<double> { 1.0, geometryDrivenScaleFactor, 10.0, 100.0 }
                .Where(s => s >= 1.0)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            bool TryBuildWithTessellatedShapeBuilder(double activeScaleFactor, out Solid builtSolid)
            {
                builtSolid = null;
                var builder = new TessellatedShapeBuilder
                {
                    Target = TessellatedShapeBuilderTarget.Solid,
                    Fallback = TessellatedShapeBuilderFallback.Abort,
                    GraphicsStyleId = ElementId.InvalidElementId
                };
                builder.OpenConnectedFaceSet(true);

                foreach (int triIndex in component)
                {
                    var tri = triangles[triIndex];
                    bool flipped = orientation[triIndex] ^ invertAll;
                    var prepared = PrepareVertices(tri.GetVertices(flipped), componentCenter, activeScaleFactor);
                    var tessFace = new TessellatedFace(prepared, ElementId.InvalidElementId);
                    if (builder.DoesFaceHaveEnoughLoopsAndVertices(tessFace))
                        builder.AddFace(tessFace);
                }

                builder.CloseConnectedFaceSet();
                builder.Build();
                builtSolid = builder.GetBuildResult().GetGeometricalObjects().OfType<Solid>().FirstOrDefault(s => s.Volume > 0);
                if (builtSolid == null)
                    return false;

                builtSolid = RestoreSolidToOriginalSpace(builtSolid, componentCenter, activeScaleFactor);
                return builtSolid != null && builtSolid.Volume > 0;
            }

            var tsbErrors = new List<string>();
            foreach (double scaleAttempt in scaleAttempts)
            {
                try
                {
                    if (TryBuildWithTessellatedShapeBuilder(scaleAttempt, out solid))
                        return true;

                    tsbErrors.Add($"Skalierung {scaleAttempt:G4}: kein Solid erzeugt");
                }
                catch (Exception ex)
                {
                    tsbErrors.Add($"Skalierung {scaleAttempt:G4}: {ex.Message}");
                }
            }

            reason = $"Revit-TSB-Fehler: {string.Join("; ", tsbErrors)} (kurze Dreiecke: {shortEdgeTriangles}, Mini-Flächen: {tinyAreaTriangles}, Versuche: {string.Join(", ", scaleAttempts.Select(s => s.ToString("G4")))})";

            double brepScaleFactor = scaleAttempts.LastOrDefault(s => s > 1.0);
            if (brepScaleFactor <= 0)
                brepScaleFactor = 1.0;

            var brepErrors = new List<string>();
            foreach (var attempt in new[]
            {
                new { Invert = invertAll, FaceReversed = false, Name = "berechnete Orientierung" },
                new { Invert = invertAll, FaceReversed = true, Name = "berechnete Orientierung + FaceReversed" },
                new { Invert = !invertAll, FaceReversed = false, Name = "invertierte Orientierung" },
                new { Invert = !invertAll, FaceReversed = true, Name = "invertierte Orientierung + FaceReversed" }
            })
            {
                if (TryBuildSolidWithBRepBuilder(triangles, component, orientation, attempt.Invert, attempt.FaceReversed, shortCurveTolerance, brepScaleFactor, out solid, out string brepReason))
                    return true;

                brepErrors.Add($"{attempt.Name}: {brepReason}");
            }

            string combinedBrepReason = string.Join(" || ", brepErrors);
            if (string.IsNullOrWhiteSpace(reason))
                reason = combinedBrepReason;
            else
                reason += $" | BRep-Fallback: {combinedBrepReason}";

            return false;
        }

        private static bool TryBuildSolidWithBRepBuilder(List<MeshTriangleInfo> triangles, List<int> component, Dictionary<int, bool> orientation, bool invertAll, bool faceReversed, double shortCurveTolerance, double scaleFactor, out Solid solid, out string reason)
        {
            solid = null;
            reason = string.Empty;

            try
            {
                double minEdgeLength = shortCurveTolerance > 0 ? shortCurveTolerance * 1.01 : MinMeshVertexTolerance;
                double minTriangleArea = minEdgeLength * minEdgeLength * 0.25;
                XYZ componentCenter = GetComponentCenter(triangles, component);
                var brep = new BRepBuilder(BRepType.Solid);
                var edges = new Dictionary<MeshEdgeKey, BRepBuilderGeometryId>();
                var uvBounds = new BoundingBoxUV(-1e6, -1e6, 1e6, 1e6);
                int skippedShortEdgeFaces = 0;
                int skippedTinyAreaFaces = 0;
                int skippedInvalidPlaneFaces = 0;
                int addedFaces = 0;

                BRepBuilderGeometryId GetEdgeId(MeshVertexKey aKey, MeshVertexKey bKey, XYZ a, XYZ b)
                {
                    var edgeKey = MeshEdgeKey.Create(aKey, bKey);
                    if (!edges.TryGetValue(edgeKey, out BRepBuilderGeometryId id))
                    {
                        double edgeLength = a.DistanceTo(b);
                        if (edgeLength <= minEdgeLength)
                            throw new InvalidOperationException($"BRep-Kante unterschreitet Revit-Kurztoleranz ({edgeLength:G6} <= {minEdgeLength:G6}).");

                        XYZ start = edgeKey.V1.Equals(aKey) ? a : b;
                        XYZ end = edgeKey.V2.Equals(bKey) ? b : a;
                        id = brep.AddEdge(BRepBuilderEdgeGeometry.Create(Line.CreateBound(start, end)));
                        edges[edgeKey] = id;
                    }

                    return id;
                }

                void AddCoEdge(BRepBuilderGeometryId loopId, MeshVertexKey fromKey, MeshVertexKey toKey, XYZ from, XYZ to)
                {
                    var edgeKey = MeshEdgeKey.Create(fromKey, toKey);
                    var edgeId = GetEdgeId(fromKey, toKey, from, to);
                    bool reversed = !edgeKey.V1.Equals(fromKey);
                    brep.AddCoEdge(loopId, edgeId, reversed);
                }

                foreach (int triIndex in component)
                {
                    var tri = triangles[triIndex];
                    bool flipped = orientation[triIndex] ^ invertAll;
                    var vertices = PrepareVertices(tri.GetVertices(flipped), componentCenter, scaleFactor);
                    XYZ a = vertices[0];
                    XYZ b = vertices[1];
                    XYZ c = vertices[2];

                    var keys = tri.GetVertexKeys(flipped);
                    var aKey = keys[0];
                    var bKey = keys[1];
                    var cKey = keys[2];

                    if (a.DistanceTo(b) <= minEdgeLength || b.DistanceTo(c) <= minEdgeLength || c.DistanceTo(a) <= minEdgeLength)
                    {
                        skippedShortEdgeFaces++;
                        continue;
                    }

                    if (GetTriangleArea(a, b, c) <= minTriangleArea)
                    {
                        skippedTinyAreaFaces++;
                        continue;
                    }

                    if (!TryCreateRobustTrianglePlane(a, b, c, out Plane plane))
                    {
                        skippedInvalidPlaneFaces++;
                        continue;
                    }

                    var faceId = brep.AddFace(BRepBuilderSurfaceGeometry.Create(plane, uvBounds), faceReversed);
                    var loopId = brep.AddLoop(faceId);
                    AddCoEdge(loopId, aKey, bKey, a, b);
                    AddCoEdge(loopId, bKey, cKey, b, c);
                    AddCoEdge(loopId, cKey, aKey, c, a);
                    brep.FinishLoop(loopId);
                    brep.FinishFace(faceId);
                    addedFaces++;
                }

                if (addedFaces == 0)
                {
                    reason = $"BRepBuilder-Fallback fand keine gültigen Faces (kurze Faces übersprungen: {skippedShortEdgeFaces}, Mini-Flächen übersprungen: {skippedTinyAreaFaces}, ungültige Ebenen übersprungen: {skippedInvalidPlaneFaces}).";
                    return false;
                }

                var outcome = brep.Finish();
                if (outcome != BRepBuilderOutcome.Success)
                {
                    reason = $"BRepBuilder-Fallback fehlgeschlagen: {outcome} (hinzugefügte Faces: {addedFaces}, übersprungene kurze Faces: {skippedShortEdgeFaces}, übersprungene Mini-Flächen: {skippedTinyAreaFaces}, übersprungene ungültige Ebenen: {skippedInvalidPlaneFaces})";
                    return false;
                }

                solid = brep.GetResult();
                if (solid == null || solid.Volume <= 0)
                {
                    reason = "BRepBuilder-Fallback lieferte kein Volumen.";
                    return false;
                }

                solid = RestoreSolidToOriginalSpace(solid, componentCenter, scaleFactor);
                return solid != null && solid.Volume > 0;
            }
            catch (Exception ex)
            {
                reason = $"BRepBuilder-Fehler: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Liefert alle Geometrieobjekte (Solids, Meshes, Instanzen und Sub-Komponenten) eines Elements rekursiv zurück.
        /// NEU: Geht auch beliebig tief in verschachtelte GeometryInstance (z.B. Blockreferenzen aus CAD).
        /// </summary>
        internal static List<GeometryObject> GetAllGeometryObjects(Element element, Options opt)
        {
            List<GeometryObject> geometryObjects = new List<GeometryObject>();
            GeometryElement geometryElement = element.get_Geometry(opt);

            if (geometryElement == null)
                return geometryObjects;

            // --- LOKALE rekursive Funktion, damit du beliebig tief in GeometryInstances (Blocks) gehst ---
            void ExtractRecursive(GeometryElement geoElem)
            {
                foreach (var geometryObject in geoElem)
                {
                    if (geometryObject is Solid solid && solid.Volume > 0)
                    {
                        geometryObjects.Add(solid);
                    }
                    else if (geometryObject is Mesh mesh)
                    {
                        geometryObjects.Add(mesh);
                    }
                    else if (geometryObject is GeometryInstance geometryInstance)
                    {
                        // REKURSION für weitere Blockreferenzen/Unterfamilien
                        ExtractRecursive(geometryInstance.GetInstanceGeometry());
                    }
                }
            }

            ExtractRecursive(geometryElement);

            // Zusätzlich: Für FamilyInstance auch Sub-Komponenten rekursiv durchsuchen (wie gehabt)
            if (element is FamilyInstance famInst)
            {
                foreach (ElementId subComponentId in famInst.GetSubComponentIds())
                {
                    Element subComponent = element.Document.GetElement(subComponentId);
                    if (subComponent != null)
                        geometryObjects.AddRange(GetAllGeometryObjects(subComponent, opt));
                }
            }

            return geometryObjects;
        }

        /// <summary>
        /// Erzeugt eine Punktwolke (Liste von XYZ) aus einer Face-Referenz, trianguliert mit gewünschtem Detaillierungsgrad.
        /// </summary>
        internal static IList<XYZ> ReadGeometryPoints(Document doc, Reference faceReference, double levelOfDetails)
        {
            IList<XYZ> m_points = new List<XYZ>();
            Face elementFace = doc.GetElement(faceReference).GetGeometryObjectFromReference(faceReference) as Face;

            // Prüfen ob Face existiert
            if (elementFace == null) return m_points;

            IList<XYZ> points = elementFace.Triangulate(levelOfDetails)?.Vertices;

            if (points != null)
            {
                foreach (XYZ pt in points)
                    m_points.Add(pt);
            }
            return m_points;
        }

        private static bool TryCreateDirectShapeGeometry(Document doc, GeometryObject geometryObject, string name)
        {
            try
            {
                if (geometryObject == null)
                    return false;

                var categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
                DirectShape directShape = DirectShape.CreateElement(doc, categoryId);
                directShape.Name = string.IsNullOrWhiteSpace(name) ? "BIMassist Mesh-Fallback" : name;
                directShape.ApplicationId = "BIMassist";
                directShape.ApplicationDataId = Guid.NewGuid().ToString();
                directShape.SetShape(new List<GeometryObject> { geometryObject });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Erstellt eine neue Familien-Datei mit den übergebenen Solids und speichert diese.
        /// Familieninstanz wird ggf. direkt im aktiven Dokument platziert.
        /// </summary>
        internal static void CreateNewFamily(UIApplication uiapp, Document dc, IList<GeometryObject> geos, string famName, XYZ position = null)
        {
            // Standard-Vorlagendatei für Familien bestimmen
            string templatePath = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            if (string.IsNullOrEmpty(templatePath))
                templatePath = uiapp.Application.FamilyTemplatePath + "\\Allgemeine Familie.rft";

            Document familyDoc = null;
            Family family;

            // Vorlagen-Datei laden/auswählen falls nicht vorhanden
            while (familyDoc == null)
            {
                try
                {
                    familyDoc = uiapp.Application.NewFamilyDocument(templatePath);
                }
                catch
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Vorlagendatei 'Allgemeine Familie.rft' konnte nicht gefunden werden. Bitte wählen Sie die Datei aus.");

                    Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog()
                    {
                        Filter = "rft files (*.rft)|*.rft",
                        InitialDirectory = uiapp.Application.FamilyTemplatePath
                    };
                    if (openFileDialog.ShowDialog() == true)
                    {
                        templatePath = openFileDialog.FileName;
                        Properties.Settings.Default.PfadVorlageAllgemeineFamilie = templatePath;
                        Properties.Settings.Default.Save();
                    }
                    else
                        return; // Abbruch durch Nutzer
                }
            }

            // Geometrien einfügen
            FamilyManager fammanage = familyDoc.FamilyManager;
            using (Transaction t = new Transaction(familyDoc, "Neue Familie mit Geometrien erstellen"))
            {
                t.Start();

                FamilyType famType = fammanage.Types.Size > 0
                    ? fammanage.Types.Cast<FamilyType>().First()
                    : fammanage.NewType("Geometrie");

                fammanage.CurrentType = famType;

                int meshFallbackCreated = 0;
                foreach (var obj in geos)
                {
                    if (obj is Solid solid)
                    {
                        FreeFormElement.Create(familyDoc, solid);
                    }
                    else if (obj is Mesh mesh)
                    {
                        if (TryCreateDirectShapeGeometry(familyDoc, mesh, famName + " Mesh-Fallback"))
                            meshFallbackCreated++;
                    }
                }

                t.Commit();

                if (meshFallbackCreated > 0)
                {
                    TaskDialog.Show("BIMassist - Mesh-Fallback",
                        $"Es wurden {meshFallbackCreated} Mesh-Objekt(e) als DirectShape-Fallback eingefügt. " +
                        "Diese Geometrie ist sichtbar und nutzbar, aber kein echter schneid-/boolesch bearbeitbarer Solid-Körper.");
                }
            }

            // Familie ins Projekt laden
            family = familyDoc.LoadFamily(dc, new JtFamilyLoadOptions());
            familyDoc.Close(false);

            // Familie umbenennen
            using (Transaction t = new Transaction(dc, "Familienname anpassen"))
            {
                t.Start();
                family.Name = famName;
                t.Commit();
            }

            // Familie im Dokument platzieren, falls kein FamilyDoc
            if (!dc.IsFamilyDocument)
            {
                if (position == null)
                    AddFamilyinstance(dc, family);
                else
                    AddFamilyinstance(dc, family, position);
            }
            else
            {
                Autodesk.Revit.UI.TaskDialog.Show("Hinweis", "Familie wurde erstellt. Instanziierung im Familien-Dokument ist nicht möglich.");
            }
        }

        /// <summary>
        /// Fügt eine oder mehrere Solids zu einer bestehenden Familien-Dokumentdatei hinzu.
        /// </summary>
        internal static void AddSolidsToFamily(UIApplication uiapp, Document doc, IList<GeometryObject> geos, ElementId targetDocId)
        {
            Document targetFamilyDoc = null;
            FamilyManager targetFamManager = null;

            Element element = doc.GetElement(targetDocId);
            if (element is FamilyInstance familyInstance)
            {
                Family family = familyInstance.Symbol.Family;
                targetFamilyDoc = doc.EditFamily(family);
                targetFamManager = targetFamilyDoc.FamilyManager;
            }

            if (targetFamilyDoc == null) return;

            using (Transaction t = new Transaction(targetFamilyDoc, "Geometrien zu bestehender Familie hinzufügen"))
            {
                t.Start();

                int meshFallbackCreated = 0;
                foreach (var obj in geos)
                {
                    if (obj is Solid solid)
                    {
                        FreeFormElement.Create(targetFamilyDoc, solid);
                    }
                    else if (obj is Mesh mesh)
                    {
                        if (TryCreateDirectShapeGeometry(targetFamilyDoc, mesh, "BIMassist Mesh-Fallback"))
                            meshFallbackCreated++;
                    }
                }

                t.Commit();

                if (meshFallbackCreated > 0)
                {
                    TaskDialog.Show("BIMassist - Mesh-Fallback",
                        $"Es wurden {meshFallbackCreated} Mesh-Objekt(e) als DirectShape-Fallback eingefügt. " +
                        "Diese Geometrie ist sichtbar und nutzbar, aber kein echter schneid-/boolesch bearbeitbarer Solid-Körper.");
                }
            }

            targetFamilyDoc.LoadFamily(doc, new JtFamilyLoadOptions());
            targetFamilyDoc.Close(false);
        }

        /// <summary>
        /// Erstellt eine neue Instanz der Familie im aktuellen Projekt-Dokument (an zufälliger Position).
        /// </summary>
        internal static void AddFamilyinstance(Document docu, Family fam)
        {
            var fsLst = new FilteredElementCollector(docu)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => fs.Family.Id == fam.Id && fs.Name == "Geometrie") // nur Typ „Geometrie“
                .ToList();

            if (fsLst.Count == 0)
            {
                TaskDialog.Show("Fehler", "Kein FamilySymbol mit Namen 'Geometrie' gefunden!");
                return;
            }

            FamilySymbol fs = fsLst.First();

            using (Transaction t = new Transaction(docu, "Familieninstanz 'Geometrie' platzieren"))
            {
                t.Start();
                if (!fs.IsActive) fs.Activate();
                docu.Create.NewFamilyInstance(new XYZ(), fs, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                t.Commit();
            }
        }

        /// <summary>
        /// Erstellt eine neue Instanz der Familie an einer definierten Position.
        /// </summary>
        internal static void AddFamilyinstance(Document docu, Family fam, XYZ position)
        {
            var symbolIds = new FilteredElementCollector(docu)
                .WherePasses(new FamilySymbolFilter(fam.Id))
                .ToElementIds();

            foreach (ElementId id in symbolIds)
            {
                FamilySymbol symbol = docu.GetElement(id) as FamilySymbol;
                if (symbol == null)
                    continue;

                // NUR wenn der Typ-Name "Geometrie" ist:
                if (symbol.Name != "Geometrie")
                    continue;

                using (Transaction t = new Transaction(docu, "Familieninstanz 'Geometrie' platzieren"))
                {
                    t.Start();
                    if (!symbol.IsActive) symbol.Activate();

                    // Beispiel: an Ursprung platzieren, kann natürlich positioniert werden!
                    docu.Create.NewFamilyInstance(new XYZ(), symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    t.Commit();
                }
            }
        }

        /// <summary>
        /// Erstellt eine neue Familie mit dem Volumenkörper als ABZUGSKÖRPER.
        /// Die Option "Abzugskörper beim Laden in Projekt schneiden" wird aktiviert.
        /// </summary>
        /// <param name="uiapp">UIApplication</param>
        /// <param name="doc">Das aktuelle Projekt-Dokument</param>
        /// <param name="solid">Das Solid das als Abzugskörper verwendet werden soll</param>
        /// <param name="famName">Name der neuen Familie</param>
        /// <param name="position">Position für die Platzierung (optional) - wenn null, wird das Solid an seiner Original-Position belassen</param>
        /// <returns>Die erstellte FamilyInstance oder null bei Fehler</returns>
        internal static FamilyInstance CreateVoidFamily(UIApplication uiapp, Document doc, Solid solid, string famName, XYZ position = null)
        {
            // Standard-Vorlagendatei für Familien bestimmen
            string templatePath = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            if (string.IsNullOrEmpty(templatePath))
                templatePath = uiapp.Application.FamilyTemplatePath + "\\Allgemeine Familie.rft";

            Document familyDoc = null;
            Family family = null;

            // Vorlagen-Datei laden/auswählen falls nicht vorhanden
            while (familyDoc == null)
            {
                try
                {
                    familyDoc = uiapp.Application.NewFamilyDocument(templatePath);
                }
                catch
                {
                    TaskDialog.Show("Fehler", "Vorlagendatei 'Allgemeine Familie.rft' konnte nicht gefunden werden. Bitte wählen Sie die Datei aus.");

                    Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog()
                    {
                        Filter = "rft files (*.rft)|*.rft",
                        InitialDirectory = uiapp.Application.FamilyTemplatePath
                    };
                    if (openFileDialog.ShowDialog() == true)
                    {
                        templatePath = openFileDialog.FileName;
                        Properties.Settings.Default.PfadVorlageAllgemeineFamilie = templatePath;
                        Properties.Settings.Default.Save();
                    }
                    else
                        return null; // Abbruch durch Nutzer
                }
            }

            FreeFormElement voidElement = null;

            // Geometrie als Abzugskörper einfügen
            // WICHTIG: Das Solid wird NICHT verschoben - es behält seine Original-Koordinaten
            // Die Familie wird dann bei (0,0,0) platziert, sodass das Solid an seiner Original-Position erscheint
            using (Transaction t = new Transaction(familyDoc, "Abzugskörper erstellen"))
            {
                t.Start();

                FamilyManager famManager = familyDoc.FamilyManager;

                // Familientyp erstellen/verwenden
                FamilyType famType = famManager.Types.Size > 0
                    ? famManager.Types.Cast<FamilyType>().First()
                    : famManager.NewType("Bodenverdrängung");

                famManager.CurrentType = famType;

                // FreeFormElement erstellen - Solid behält seine Original-Koordinaten
                voidElement = FreeFormElement.Create(familyDoc, solid);

                if (voidElement != null)
                {
                    // Als Abzugskörper (Void) setzen
                    Parameter isSolidParam = voidElement.get_Parameter(BuiltInParameter.ELEMENT_IS_CUTTING);
                    if (isSolidParam != null && !isSolidParam.IsReadOnly)
                    {
                        isSolidParam.Set(1); // 1 = Abzugskörper (Void), 0 = Volumenkörper (Solid)
                    }
                }

                t.Commit();
            }

            // "Abzugskörper beim Laden schneiden" aktivieren
            using (Transaction t = new Transaction(familyDoc, "Abzugskörper-Option aktivieren"))
            {
                t.Start();

                // Familienparameter für "Beim Laden in Projekt schneiden" setzen
                FamilyManager famManager = familyDoc.FamilyManager;

                // Diese Option wird über den FamilyManager gesetzt
                // Parameter: "Schneidet mit Abzugskörper beim Laden"
                try
                {
                    Parameter cutWithVoidsParam = familyDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
                    if (cutWithVoidsParam != null && !cutWithVoidsParam.IsReadOnly)
                    {
                        cutWithVoidsParam.Set(1);
                    }
                }
                catch { }

                t.Commit();
            }

            // Familie ins Projekt laden
            family = familyDoc.LoadFamily(doc, new JtFamilyLoadOptions());
            familyDoc.Close(false);

            if (family == null)
            {
                TaskDialog.Show("Fehler", "Familie konnte nicht in das Projekt geladen werden.");
                return null;
            }

            // Familie umbenennen
            FamilyInstance placedInstance = null;

            using (Transaction t = new Transaction(doc, "Abzugskörper-Familie benennen und platzieren"))
            {
                t.Start();

                // Name setzen
                family.Name = famName;

                // FamilySymbol finden und aktivieren
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .Where(fs => fs.Family.Id == family.Id)
                    .ToList();

                if (collector.Count > 0)
                {
                    FamilySymbol symbol = collector.First();
                    if (!symbol.IsActive)
                        symbol.Activate();

                    // WICHTIG: Familie bei (0,0,0) platzieren!
                    // Das Solid in der Familie hat bereits seine Original-Welt-Koordinaten,
                    // daher muss die Familie bei (0,0,0) platziert werden, damit das Solid
                    // an seiner ursprünglichen Position erscheint.
                    XYZ placementPos = XYZ.Zero;

                    // Instanz platzieren
                    placedInstance = doc.Create.NewFamilyInstance(
                        placementPos, 
                        symbol, 
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                }

                t.Commit();
            }

            return placedInstance;
        }
    }

}
