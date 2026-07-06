using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ShellOffsetCommand : IExternalCommand
    {
        private const double MinArea = 1e-10;
        private const double DefaultWeldToleranceMm = 1.0;
        private const double DefaultHardEdgeAngleDegrees = 35.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                ShellOffsetOptions options = AskOptions(uiapp);
                if (options == null)
                    return Result.Cancelled;

                List<FaceSource> faces = CollectFaces(uidoc, doc, out string selectionInfo);
                if (faces.Count == 0)
                {
                    TaskDialog.Show("BIMassist", "Es wurden keine Revit-Flächen gefunden. Bitte einzelne Flächen wählen oder ganze Elemente/Solids ausdrücklich als Vorwahl verwenden.");
                    return Result.Cancelled;
                }

                if (!TryBuildShellGeometry(faces, options, doc.Application.ShortCurveTolerance, out IList<GeometryObject> shellGeometry, out string diagnostic))
                {
                    int planarCount = faces.Count(f => f.Face is PlanarFace);
                    int curvedCount = faces.Count - planarCount;
                    TaskDialog.Show("BIMassist - Hülle / Shell Offset",
                        $"{diagnostic}\n\n" +
                        $"Diagnose:\n" +
                        $"Auswahl: {selectionInfo}\n" +
                        $"Quellflächen: {faces.Count}\n" +
                        $"Planar: {planarCount}\n" +
                        $"Gekrümmt/Nicht-planar: {curvedCount}");
                    return Result.Failed;
                }

                int originalGeometryCount = shellGeometry.Count;
                int unionOperations = TryUnionConnectedSolids(shellGeometry, out shellGeometry);

                string familyName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Bitte einen neuen Familienamen für die Hülle eingeben:",
                    "Hülle / Shell Offset",
                    "Shell_Offset");

                if (string.IsNullOrWhiteSpace(familyName))
                    return Result.Cancelled;

                FamilyGeometryTools.CreateNewFamily(uiapp, doc, shellGeometry, familyName.Trim());

                TaskDialog.Show("BIMassist - Hülle / Shell Offset",
                    $"Neue Familie wurde erstellt.\n\n" +
                    $"Verarbeitete Quellflächen: {faces.Count}\n" +
                    $"Geometrien vor Verbindung: {originalGeometryCount}\n" +
                    $"Geometrien nach Verbindung: {shellGeometry.Count}\n" +
                    $"Erfolgreiche Verbindungen: {unionOperations}\n" +
                    $"Wandstärke: {options.ThicknessCm:0.###} cm\n" +
                    $"Tessellierung: {options.TessellationLevel:0.###}\n\n" +
                    "Hinweis: Gekrümmte Flächen werden über Revit-Triangulierung/Tessellierung approximiert. Die Genauigkeit wird über den Tessellierungswert gesteuert.");

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BIMassist - Hülle / Shell Offset", "Fehler beim Erzeugen der Hülle:\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static int TryUnionConnectedSolids(IList<GeometryObject> inputGeometry, out IList<GeometryObject> outputGeometry)
        {
            var solids = inputGeometry
                .OfType<Solid>()
                .Where(s => s != null && s.Volume > MinArea)
                .ToList();
            var otherGeometry = inputGeometry
                .Where(g => g is not Solid)
                .ToList();

            int unionCount = 0;
            bool changed;

            // Revit-Boolean-Union ist die zuverlässigste Möglichkeit, echte Volumenkörper in der Familie
            // wirklich zu einem Körper zu verbinden. Bei nicht berührenden Körpern oder Kernel-Grenzfällen
            // bleibt die Geometrie bewusst separat erhalten, statt fälschlich Erfolg zu melden.
            do
            {
                changed = false;

                for (int i = 0; i < solids.Count && !changed; i++)
                {
                    for (int j = i + 1; j < solids.Count && !changed; j++)
                    {
                        if (!BoundingBoxesCanTouch(solids[i], solids[j]))
                            continue;

                        try
                        {
                            Solid union = BooleanOperationsUtils.ExecuteBooleanOperation(solids[i], solids[j], BooleanOperationsType.Union);
                            if (union != null && union.Volume > Math.Max(solids[i].Volume, solids[j].Volume) - MinArea)
                            {
                                solids[i] = union;
                                solids.RemoveAt(j);
                                unionCount++;
                                changed = true;
                            }
                        }
                        catch
                        {
                            // Nicht jede Berührung lässt sich vom Revit-Kernel als Union berechnen.
                            // In diesem Fall bleibt der Körper separat, damit keine gültige Geometrie verloren geht.
                        }
                    }
                }
            }
            while (changed);

            outputGeometry = solids.Cast<GeometryObject>().Concat(otherGeometry).ToList();
            return unionCount;
        }

        private static bool BoundingBoxesCanTouch(Solid a, Solid b)
        {
            BoundingBoxXYZ boxA = a.GetBoundingBox();
            BoundingBoxXYZ boxB = b.GetBoundingBox();
            if (boxA == null || boxB == null)
                return true;

            const double tolerance = 1e-6;
            return boxA.Min.X <= boxB.Max.X + tolerance && boxA.Max.X + tolerance >= boxB.Min.X &&
                   boxA.Min.Y <= boxB.Max.Y + tolerance && boxA.Max.Y + tolerance >= boxB.Min.Y &&
                   boxA.Min.Z <= boxB.Max.Z + tolerance && boxA.Max.Z + tolerance >= boxB.Min.Z;
        }

        private static ShellOffsetOptions AskOptions(UIApplication uiapp)
        {
            string thicknessText = Microsoft.VisualBasic.Interaction.InputBox(
                "Wandstärke in cm eingeben:",
                "Hülle / Shell Offset",
                "10");
            if (string.IsNullOrWhiteSpace(thicknessText))
                return null;

            if (!double.TryParse(thicknessText.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double thicknessCm) || thicknessCm <= 0)
            {
                TaskDialog.Show("BIMassist", "Ungültige Wandstärke. Bitte eine positive Zahl in cm eingeben.");
                return null;
            }

            string tessText = Microsoft.VisualBasic.Interaction.InputBox(
                "Tessellierungsgenauigkeit für gekrümmte Flächen eingeben (0,0 bis 1,0):\n\n0 = grob / weniger Dreiecke\n1 = fein / mehr Dreiecke",
                "Hülle / Shell Offset",
                "0,5");
            if (string.IsNullOrWhiteSpace(tessText))
                return null;

            if (!double.TryParse(tessText.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tessellationLevel))
            {
                TaskDialog.Show("BIMassist", "Ungültige Tessellierungsgenauigkeit.");
                return null;
            }

            tessellationLevel = Math.Max(0.0, Math.Min(1.0, tessellationLevel));

            TaskDialog directionDialog = new TaskDialog("Hülle / Shell Offset")
            {
                MainInstruction = "Offset-Richtung wählen",
                MainContent = "Ja = Richtung umkehren (innen statt außen bzw. umgekehrt)\nNein = Standardrichtung verwenden",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.No
            };

            TaskDialogResult directionResult = directionDialog.Show();
            if (directionResult == TaskDialogResult.Cancel)
                return null;

            return new ShellOffsetOptions
            {
                ThicknessCm = thicknessCm,
                ThicknessInternal = UnitUtils.ConvertToInternalUnits(thicknessCm, UnitTypeId.Centimeters),
                TessellationLevel = tessellationLevel,
                WeldTolerance = Math.Max(uiapp.Application.ShortCurveTolerance, UnitUtils.ConvertToInternalUnits(DefaultWeldToleranceMm, UnitTypeId.Millimeters)),
                HardEdgeCosLimit = Math.Cos(DefaultHardEdgeAngleDegrees * Math.PI / 180.0),
                ReverseDirection = directionResult == TaskDialogResult.Yes
            };
        }

        private static List<FaceSource> CollectFaces(UIDocument uidoc, Document doc, out string selectionInfo)
        {
            var faces = new List<FaceSource>();
            selectionInfo = "Einzelne Flächen";
            var selectedElementIds = uidoc.Selection.GetElementIds()?.ToList() ?? new List<ElementId>();

            if (selectedElementIds.Count > 0)
            {
                TaskDialog preselectionDialog = new TaskDialog("Hülle / Shell Offset")
                {
                    MainInstruction = "Vorselektierte Elemente als ganze Solids verwenden?",
                    MainContent =
                        $"Es sind {selectedElementIds.Count} Element(e) vorselektiert.\n\n" +
                        "Ja = komplette Elemente/Solids verarbeiten.\n" +
                        "Nein = Vorwahl ignorieren und danach einzelne Flächen anklicken.\n\n" +
                        "Für deinen Fall mit nur den oberen Flächen bitte 'Nein' wählen und anschließend die Flächen anklicken.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.No
                };

                TaskDialogResult result = preselectionDialog.Show();
                if (result == TaskDialogResult.Cancel)
                {
                    selectionInfo = "Auswahl abgebrochen";
                    return faces;
                }

                if (result == TaskDialogResult.Yes)
                {
                    Options options = new Options
                    {
                        ComputeReferences = true,
                        DetailLevel = ViewDetailLevel.Fine,
                        IncludeNonVisibleObjects = false
                    };

                    foreach (ElementId elementId in selectedElementIds)
                    {
                        Element element = doc.GetElement(elementId);
                        CollectElementFaces(element, options, faces);
                    }

                    if (faces.Count > 0)
                    {
                        selectionInfo = $"{selectedElementIds.Count} vorselektierte(s) Element(e) als ganze Solids";
                        return DeduplicateFaces(faces);
                    }
                }
            }

            IList<Reference> pickedFaces = uidoc.Selection.PickObjects(ObjectType.Face, "Einzelne Flächen für Hülle / Shell Offset auswählen. Mit Fertig/Esc abschließen.");
            foreach (Reference reference in pickedFaces)
            {
                Element element = doc.GetElement(reference.ElementId);
                GeometryObject geometryObject = element?.GetGeometryObjectFromReference(reference);
                if (geometryObject is Face face)
                {
                    faces.Add(new FaceSource(face, reference.ElementId, face.MaterialElementId));
                }
            }

            selectionInfo = $"{faces.Count} einzeln angeklickte Fläche(n)";
            return DeduplicateFaces(faces);
        }

        private static void CollectElementFaces(Element element, Options options, List<FaceSource> faces)
        {
            if (element == null)
                return;

            GeometryElement geometry = element.get_Geometry(options);
            if (geometry == null)
                return;

            foreach (GeometryObject geometryObject in geometry)
                CollectGeometryObjectFaces(geometryObject, element.Id, faces);
        }

        private static void CollectGeometryObjectFaces(GeometryObject geometryObject, ElementId sourceElementId, List<FaceSource> faces)
        {
            switch (geometryObject)
            {
                case Solid solid when solid.Faces.Size > 0 && solid.Volume > 0:
                    foreach (Face face in solid.Faces)
                        faces.Add(new FaceSource(face, sourceElementId, face.MaterialElementId));
                    break;
                case GeometryInstance instance:
                    GeometryElement instanceGeometry = instance.GetInstanceGeometry();
                    if (instanceGeometry == null) return;
                    foreach (GeometryObject child in instanceGeometry)
                        CollectGeometryObjectFaces(child, sourceElementId, faces);
                    break;
            }
        }

        private static List<FaceSource> DeduplicateFaces(List<FaceSource> faces)
        {
            var result = new List<FaceSource>();
            var seen = new HashSet<string>();

            foreach (FaceSource face in faces)
            {
                Mesh mesh = face.Face.Triangulate(0.0);
                if (mesh == null || mesh.NumTriangles == 0)
                    continue;

                XYZ p = mesh.get_Triangle(0).get_Vertex(0);
                string key = $"{face.SourceElementId.Value}:{Math.Round(p.X, 6)}:{Math.Round(p.Y, 6)}:{Math.Round(p.Z, 6)}:{Math.Round(face.Face.Area, 6)}";
                if (seen.Add(key))
                    result.Add(face);
            }

            return result;
        }

        private static bool TryBuildShellGeometry(List<FaceSource> faces, ShellOffsetOptions options, double shortCurveTolerance, out IList<GeometryObject> geometry, out string diagnostic)
        {
            geometry = new List<GeometryObject>();
            diagnostic = string.Empty;

            // Einschränkung bewusst dokumentiert:
            // Revit bietet für beliebige NURBS-/gekrümmte Faces keinen direkten robusten BRep-Offset wie AutoCAD-SHELL.
            // Deshalb wird jede Fläche kontrolliert trianguliert, intern als Mesh versetzt und mit TessellatedShapeBuilder
            // wieder zu einem geschlossenen Körper aufgebaut. Starke Krümmung, enge Radien oder zu große Wandstärken können
            // Selbstüberschneidungen erzeugen. Diese Fälle werden nach Möglichkeit erkannt oder vom Builder abgebrochen.

            List<MeshVertex> vertices = new List<MeshVertex>();
            List<MeshTriangleInfo> triangles = new List<MeshTriangleInfo>();
            var vertexLookup = new Dictionary<VertexKey, int>();

            foreach (FaceSource source in faces)
            {
                Mesh mesh = source.Face.Triangulate(options.TessellationLevel);
                if (mesh == null || mesh.NumTriangles == 0)
                    continue;

                for (int i = 0; i < mesh.NumTriangles; i++)
                {
                    MeshTriangle meshTriangle = mesh.get_Triangle(i);
                    int a = GetOrCreateVertex(meshTriangle.get_Vertex(0), source, options, vertices, vertexLookup);
                    int b = GetOrCreateVertex(meshTriangle.get_Vertex(1), source, options, vertices, vertexLookup);
                    int c = GetOrCreateVertex(meshTriangle.get_Vertex(2), source, options, vertices, vertexLookup);

                    XYZ ab = vertices[b].Point - vertices[a].Point;
                    XYZ ac = vertices[c].Point - vertices[a].Point;
                    XYZ normal = ab.CrossProduct(ac);
                    if (normal.GetLength() <= shortCurveTolerance * shortCurveTolerance)
                        continue;

                    normal = normal.Normalize();
                    XYZ centroid = (vertices[a].Point + vertices[b].Point + vertices[c].Point) / 3.0;
                    XYZ faceNormal = TryGetFaceNormalAtPoint(source.Face, centroid);
                    if (faceNormal != null && faceNormal.GetLength() > 1e-9 && normal.DotProduct(faceNormal) < 0)
                    {
                        int tmp = b;
                        b = c;
                        c = tmp;
                        normal = -normal;
                        ab = vertices[b].Point - vertices[a].Point;
                        ac = vertices[c].Point - vertices[a].Point;
                    }

                    double area2 = ab.CrossProduct(ac).GetLength();
                    if (area2 <= MinArea)
                        continue;

                    triangles.Add(new MeshTriangleInfo(a, b, c, source.SourceElementId, source.MaterialId, normal, area2));

                    vertices[a].AddNormal(normal, area2);
                    vertices[b].AddNormal(normal, area2);
                    vertices[c].AddNormal(normal, area2);
                }
            }

            if (triangles.Count == 0 || vertices.Count < 3)
            {
                diagnostic = "Keine gültigen Dreiecke aus den selektierten Flächen erzeugt.";
                return false;
            }

            if (TryBuildPlanarFaceExtrusions(faces, options, out geometry, out diagnostic))
                return true;

            var edgeMap = BuildEdgeMap(triangles);
            int nonManifoldCount = edgeMap.Count(x => x.Value.Count > 2);
            if (nonManifoldCount > 0)
            {
                diagnostic = $"Nicht-mannigfaltige Kanten erkannt ({nonManifoldCount}).\n\nBitte eine sauber zusammenhängende Flächenmenge wählen.";
                return false;
            }

            List<List<int>> components = FindTriangleComponents(triangles, edgeMap);
            if (components.Count == 0)
            {
                diagnostic = "Keine zusammenhängenden Dreieckskomponenten erzeugt.";
                return false;
            }

            var componentOutputs = new List<List<OutputTriangle>>();
            foreach (List<int> component in components)
            {
                List<MeshTriangleInfo> componentTriangles = component.Select(i => triangles[i]).ToList();
                Dictionary<EdgeKey, List<int>> componentEdgeMap = BuildEdgeMap(componentTriangles);
                List<int> componentVertexIndices = componentTriangles
                    .SelectMany(t => new[] { t.A, t.B, t.C })
                    .Distinct()
                    .ToList();

                XYZ componentCenter = componentVertexIndices.Aggregate(XYZ.Zero, (sum, i) => sum + vertices[i].Point) / componentVertexIndices.Count;

                List<OutputTriangle> outputTriangles = BuildOutputTriangles(vertices, componentTriangles, componentEdgeMap, options, componentCenter, shortCurveTolerance, out diagnostic);
                if (outputTriangles.Count == 0)
                    return false;

                componentOutputs.Add(outputTriangles);
            }

            if (TryBuildTessellatedGeometry(componentOutputs, TessellatedShapeBuilderTarget.Solid, TessellatedShapeBuilderFallback.Abort, out geometry, out string solidError))
                return true;

            // Kein Mesh-/DirectShape-Fallback: Diese Funktion soll einen echten Volumenkörper erzeugen,
            // damit Materialzuweisung und weitere Solid-Workflows funktionieren. Wenn Revit aus der
            // tessellierten Hülle keinen Solid bauen kann, gilt das als Fehler und wird dem Nutzer gemeldet.
            diagnostic =
                "Für die ausgewählten gekrümmten Flächen konnte kein echter Volumenkörper erzeugt werden.\n" +
                "Es wurde bewusst kein DirectShape/Mesh-Fallback erstellt.\n\n" +
                "Bitte Wandstärke reduzieren, Tessellierung erhöhen oder die Fläche in kleinere Teilflächen aufteilen.\n\n" +
                "Revit-Fehler beim Solid-Aufbau:\n" + solidError;
            return false;
        }

        private static bool TryBuildTessellatedGeometry(
            List<List<OutputTriangle>> componentOutputs,
            TessellatedShapeBuilderTarget target,
            TessellatedShapeBuilderFallback fallback,
            out IList<GeometryObject> geometry,
            out string error)
        {
            geometry = new List<GeometryObject>();
            error = string.Empty;

            try
            {
                TessellatedShapeBuilder builder = new TessellatedShapeBuilder
                {
                    Target = target,
                    Fallback = fallback,
                    GraphicsStyleId = ElementId.InvalidElementId
                };

                int added = 0;
                int builtComponents = 0;

                foreach (List<OutputTriangle> outputTriangles in componentOutputs)
                {
                    // Wichtig für mehrere getrennte ausgewählte Oberflächen/Körper:
                    // Jede topologisch getrennte Shell muss als eigenes ConnectedFaceSet an TessellatedShapeBuilder
                    // übergeben werden. Eine einzige FaceSet-Gruppe für mehrere getrennte Körper führt bei Revit oft zu
                    // "TessellatedShapeBuilder failed to build the requested geometry", obwohl jede Teilhülle gültig ist.
                    builder.OpenConnectedFaceSet(true);
                    int componentAdded = 0;
                    foreach (OutputTriangle triangle in outputTriangles)
                    {
                        var tessFace = new TessellatedFace(new List<XYZ> { triangle.A, triangle.B, triangle.C }, triangle.MaterialId ?? ElementId.InvalidElementId);
                        if (builder.DoesFaceHaveEnoughLoopsAndVertices(tessFace))
                        {
                            builder.AddFace(tessFace);
                            added++;
                            componentAdded++;
                        }
                    }
                    builder.CloseConnectedFaceSet();

                    if (componentAdded > 0)
                        builtComponents++;
                }

                if (added == 0 || builtComponents == 0)
                {
                    error = "Keine gültigen TessellatedFace-Dreiecke erzeugt.";
                    return false;
                }

                builder.Build();
                geometry = builder.GetBuildResult().GetGeometricalObjects();
                if (geometry == null || geometry.Count == 0)
                {
                    error = $"TessellatedShapeBuilder hat keine Geometrie erzeugt. Target={target}, Fallback={fallback}.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryBuildPlanarFaceExtrusions(List<FaceSource> faces, ShellOffsetOptions options, out IList<GeometryObject> geometry, out string diagnostic)
        {
            geometry = new List<GeometryObject>();
            diagnostic = string.Empty;

            if (faces.Count == 0 || faces.Any(f => f.Face is not PlanarFace))
                return false;

            double sign = options.ReverseDirection ? -1.0 : 1.0;
            var solids = new List<GeometryObject>();
            int failed = 0;

            foreach (FaceSource source in faces)
            {
                var planarFace = (PlanarFace)source.Face;
                XYZ normal = planarFace.FaceNormal;
                if (normal == null || normal.GetLength() <= 1e-9)
                {
                    failed++;
                    continue;
                }

                normal = normal.Normalize() * sign;

                IList<CurveLoop> loops;
                try
                {
                    loops = planarFace.GetEdgesAsCurveLoops();
                }
                catch
                {
                    failed++;
                    continue;
                }

                if (loops == null || loops.Count == 0)
                {
                    failed++;
                    continue;
                }

                try
                {
                    Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, normal, options.ThicknessInternal);
                    if (solid != null && solid.Volume > 1e-9)
                    {
                        solids.Add(solid);
                        continue;
                    }
                }
                catch
                {
                    // Revit verlangt in manchen Fällen die Gegenrichtung bzw. andere Loop-Orientierung.
                }

                try
                {
                    Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, -normal, options.ThicknessInternal);
                    if (solid != null && solid.Volume > 1e-9)
                    {
                        solids.Add(solid);
                        continue;
                    }
                }
                catch
                {
                    failed++;
                }
            }

            if (solids.Count == 0)
            {
                diagnostic = "Die ausgewählten planaren Flächen konnten nicht direkt extrudiert werden. Es wird der Mesh-Offset-Pfad versucht.";
                return false;
            }

            geometry = solids;
            if (failed > 0)
            {
                diagnostic = $"{solids.Count} planare Fläche(n) wurden als Volumenkörper erzeugt; {failed} Fläche(n) konnten nicht extrudiert werden.";
            }

            return true;
        }

        private static int GetOrCreateVertex(XYZ point, FaceSource source, ShellOffsetOptions options, List<MeshVertex> vertices, Dictionary<VertexKey, int> lookup)
        {
            VertexKey key = VertexKey.FromPoint(point, options.WeldTolerance);
            if (lookup.TryGetValue(key, out int existing))
                return existing;

            XYZ normal = TryGetFaceNormalAtPoint(source.Face, point);
            int index = vertices.Count;
            vertices.Add(new MeshVertex(point, normal));
            lookup[key] = index;
            return index;
        }

        private static XYZ TryGetFaceNormalAtPoint(Face face, XYZ point)
        {
            try
            {
                IntersectionResult projection = face.Project(point);
                if (projection != null)
                {
                    XYZ normal = face.ComputeNormal(projection.UVPoint);
                    if (normal != null && normal.GetLength() > 1e-9)
                        return normal.Normalize();
                }
            }
            catch
            {
                // Fallback auf Dreiecksnormalen/gewichtete Mesh-Normalen.
            }

            return XYZ.Zero;
        }

        private static Dictionary<EdgeKey, List<int>> BuildEdgeMap(List<MeshTriangleInfo> triangles)
        {
            var edgeMap = new Dictionary<EdgeKey, List<int>>();
            for (int i = 0; i < triangles.Count; i++)
            {
                foreach (EdgeKey edge in triangles[i].Edges())
                {
                    if (!edgeMap.TryGetValue(edge, out List<int> list))
                    {
                        list = new List<int>();
                        edgeMap[edge] = list;
                    }
                    list.Add(i);
                }
            }
            return edgeMap;
        }

        private static List<List<int>> FindTriangleComponents(List<MeshTriangleInfo> triangles, Dictionary<EdgeKey, List<int>> edgeMap)
        {
            var adjacency = new List<int>[triangles.Count];
            for (int i = 0; i < adjacency.Length; i++)
                adjacency[i] = new List<int>();

            foreach (List<int> sharingTriangles in edgeMap.Values)
            {
                if (sharingTriangles.Count < 2)
                    continue;

                for (int i = 0; i < sharingTriangles.Count; i++)
                {
                    for (int j = i + 1; j < sharingTriangles.Count; j++)
                    {
                        int a = sharingTriangles[i];
                        int b = sharingTriangles[j];
                        adjacency[a].Add(b);
                        adjacency[b].Add(a);
                    }
                }
            }

            var components = new List<List<int>>();
            var visited = new bool[triangles.Count];
            for (int i = 0; i < triangles.Count; i++)
            {
                if (visited[i])
                    continue;

                var component = new List<int>();
                var queue = new Queue<int>();
                visited[i] = true;
                queue.Enqueue(i);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(current);

                    foreach (int next in adjacency[current])
                    {
                        if (visited[next])
                            continue;

                        visited[next] = true;
                        queue.Enqueue(next);
                    }
                }

                components.Add(component);
            }

            return components;
        }

        private static List<OutputTriangle> BuildOutputTriangles(
            List<MeshVertex> vertices,
            List<MeshTriangleInfo> triangles,
            Dictionary<EdgeKey, List<int>> edgeMap,
            ShellOffsetOptions options,
            XYZ center,
            double shortCurveTolerance,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            var output = new List<OutputTriangle>();
            double sign = options.ReverseDirection ? -1.0 : 1.0;

            XYZ OffsetPoint(int vertexIndex, XYZ triangleNormal)
            {
                MeshVertex vertex = vertices[vertexIndex];
                XYZ normal = vertex.GetOffsetNormal(triangleNormal, options.HardEdgeCosLimit);
                if (normal == null || normal.GetLength() <= 1e-9)
                    normal = triangleNormal;
                return vertex.Point + normal.Normalize() * options.ThicknessInternal * sign;
            }

            foreach (MeshTriangleInfo tri in triangles)
            {
                XYZ a = vertices[tri.A].Point;
                XYZ b = vertices[tri.B].Point;
                XYZ c = vertices[tri.C].Point;

                XYZ ao = OffsetPoint(tri.A, tri.Normal);
                XYZ bo = OffsetPoint(tri.B, tri.Normal);
                XYZ co = OffsetPoint(tri.C, tri.Normal);

                if (IsCollapsed(a, ao, shortCurveTolerance) || IsCollapsed(b, bo, shortCurveTolerance) || IsCollapsed(c, co, shortCurveTolerance))
                {
                    diagnostic = "Offset erzeugt kollabierende Geometrie. Wandstärke reduzieren oder Tessellierung erhöhen.";
                    return new List<OutputTriangle>();
                }

                // Originalflächen bilden die Rückseite, daher invertiertes Winding.
                output.Add(new OutputTriangle(c, b, a, tri.MaterialId));
                // Offsetflächen bilden die Vorderseite in Richtung der ausgerichteten Face-Normalen.
                output.Add(new OutputTriangle(ao, bo, co, tri.MaterialId));
            }

            foreach (var pair in edgeMap.Where(x => x.Value.Count == 1))
            {
                EdgeKey edge = pair.Key;
                MeshTriangleInfo tri = triangles[pair.Value[0]];
                DirectedEdge directedEdge = tri.GetDirectedEdge(edge);

                XYZ a = vertices[directedEdge.A].Point;
                XYZ b = vertices[directedEdge.B].Point;
                XYZ ao = OffsetPoint(directedEdge.A, tri.Normal);
                XYZ bo = OffsetPoint(directedEdge.B, tri.Normal);

                // Seitenflächen müssen die ursprüngliche Dreieckskantenrichtung verwenden.
                // EdgeKey ist absichtlich sortiert, damit Randkanten erkannt werden; diese Sortierung darf aber
                // NICHT für das Face-Winding verwendet werden, sonst entstehen bei gekrümmten/zusammengesetzten
                // Flächen invertierte Seiten-Dreiecke und TessellatedShapeBuilder bricht mit einer generischen
                // "failed to build"-Meldung ab.
                output.Add(new OutputTriangle(a, b, bo, tri.MaterialId));
                output.Add(new OutputTriangle(a, bo, ao, tri.MaterialId));
            }

            return output;
        }

        private static bool IsCollapsed(XYZ a, XYZ b, double tolerance)
        {
            return a.DistanceTo(b) <= tolerance;
        }

        private static OutputTriangle Orient(OutputTriangle tri, XYZ center)
        {
            XYZ normal = (tri.B - tri.A).CrossProduct(tri.C - tri.A);
            if (normal.GetLength() <= 1e-12)
                return tri;

            XYZ centroid = (tri.A + tri.B + tri.C) / 3.0;
            XYZ outward = centroid - center;
            if (outward.GetLength() > 1e-9 && normal.DotProduct(outward) < 0)
                return new OutputTriangle(tri.A, tri.C, tri.B, tri.MaterialId);

            return tri;
        }

        private sealed class ShellOffsetOptions
        {
            public double ThicknessCm { get; set; }
            public double ThicknessInternal { get; set; }
            public double TessellationLevel { get; set; }
            public double WeldTolerance { get; set; }
            public double HardEdgeCosLimit { get; set; }
            public bool ReverseDirection { get; set; }
        }

        private sealed class FaceSource
        {
            public FaceSource(Face face, ElementId sourceElementId, ElementId materialId)
            {
                Face = face;
                SourceElementId = sourceElementId;
                MaterialId = materialId;
            }

            public Face Face { get; }
            public ElementId SourceElementId { get; }
            public ElementId MaterialId { get; }
        }

        private sealed class MeshVertex
        {
            private readonly List<(XYZ Normal, double Weight)> _normals = new List<(XYZ Normal, double Weight)>();

            public MeshVertex(XYZ point, XYZ faceNormal)
            {
                Point = point;
                if (faceNormal != null && faceNormal.GetLength() > 1e-9)
                    _normals.Add((faceNormal.Normalize(), 1.0));
            }

            public XYZ Point { get; }

            public void AddNormal(XYZ normal, double weight)
            {
                if (normal != null && normal.GetLength() > 1e-9)
                    _normals.Add((normal.Normalize(), Math.Max(weight, 1e-9)));
            }

            public XYZ GetOffsetNormal(XYZ triangleNormal, double hardEdgeCosLimit)
            {
                if (_normals.Count == 0)
                    return triangleNormal;

                bool hardEdge = _normals.Any(n => triangleNormal.DotProduct(n.Normal) < hardEdgeCosLimit);
                if (hardEdge)
                    return triangleNormal;

                XYZ sum = XYZ.Zero;
                foreach (var item in _normals)
                    sum += item.Normal * item.Weight;

                return sum.GetLength() > 1e-9 ? sum.Normalize() : triangleNormal;
            }
        }

        private readonly struct MeshTriangleInfo
        {
            public MeshTriangleInfo(int a, int b, int c, ElementId sourceFaceId, ElementId materialId, XYZ normal, double areaWeight)
            {
                A = a;
                B = b;
                C = c;
                SourceFaceId = sourceFaceId;
                MaterialId = materialId;
                Normal = normal;
                AreaWeight = areaWeight;
            }

            public int A { get; }
            public int B { get; }
            public int C { get; }
            public ElementId SourceFaceId { get; }
            public ElementId MaterialId { get; }
            public XYZ Normal { get; }
            public double AreaWeight { get; }

            public IEnumerable<EdgeKey> Edges()
            {
                yield return EdgeKey.Create(A, B);
                yield return EdgeKey.Create(B, C);
                yield return EdgeKey.Create(C, A);
            }

            public DirectedEdge GetDirectedEdge(EdgeKey edge)
            {
                if (edge.Equals(EdgeKey.Create(A, B))) return new DirectedEdge(A, B);
                if (edge.Equals(EdgeKey.Create(B, C))) return new DirectedEdge(B, C);
                return new DirectedEdge(C, A);
            }
        }

        private readonly struct DirectedEdge
        {
            public DirectedEdge(int a, int b)
            {
                A = a;
                B = b;
            }

            public int A { get; }
            public int B { get; }
        }

        private readonly struct OutputTriangle
        {
            public OutputTriangle(XYZ a, XYZ b, XYZ c, ElementId materialId)
            {
                A = a;
                B = b;
                C = c;
                MaterialId = materialId;
            }

            public XYZ A { get; }
            public XYZ B { get; }
            public XYZ C { get; }
            public ElementId MaterialId { get; }
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            private EdgeKey(int a, int b)
            {
                A = a;
                B = b;
            }

            public int A { get; }
            public int B { get; }

            public static EdgeKey Create(int a, int b) => a <= b ? new EdgeKey(a, b) : new EdgeKey(b, a);
            public bool Equals(EdgeKey other) => A == other.A && B == other.B;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(A, B);
        }

        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            private VertexKey(long x, long y, long z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            private long X { get; }
            private long Y { get; }
            private long Z { get; }

            public static VertexKey FromPoint(XYZ point, double tolerance)
            {
                return new VertexKey(
                    (long)Math.Round(point.X / tolerance),
                    (long)Math.Round(point.Y / tolerance),
                    (long)Math.Round(point.Z / tolerance));
            }

            public bool Equals(VertexKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        }
    }
}
