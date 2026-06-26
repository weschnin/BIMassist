using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    internal class ConstructVolumeFromFacesCommand : IExternalCommand
    {
        private const double DistanceTolerance = 1e-4;
        private const double VolumeTolerance = 1e-6;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            List<SelectedFaceInfo> selectedFaces = SelectFaces(uidoc, doc);
            if (selectedFaces.Count == 0)
                return Result.Cancelled;

            if (!TryBuildSolidFromSelection(selectedFaces, doc.Application.ShortCurveTolerance, out Solid? solid, out string diagnostic))
            {
                string fullDiagnostic = AppendSelectionDebugLog(selectedFaces, diagnostic);
                TaskDialog.Show("BIMassist - Volumenkörper konstruieren", fullDiagnostic);
                return Result.Failed;
            }

            using Transaction tx = new Transaction(doc, "BIMassist Volumenkörper konstruieren");
            tx.Start();

            DirectShape ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
            ds.Name = "BIMassist Constructed Volume";
            ds.ApplicationId = "BIMassist";
            ds.ApplicationDataId = Guid.NewGuid().ToString("N");
            ds.SetShape(new GeometryObject[] { solid! });

            tx.Commit();
            return Result.Succeeded;
        }

        private static List<SelectedFaceInfo> SelectFaces(UIDocument uidoc, Document doc)
        {
            List<SelectedFaceInfo> faces = new List<SelectedFaceInfo>();
            ISelectionFilter filter = new FaceSelectionFilter();

            TaskDialog.Show(
                "BIMassist - Volumenkörper konstruieren",
                "Wählen Sie die Flächen des gewünschten Volumenkörpers.\n" +
                "Mit ESC beenden Sie die Auswahl und starten die Konstruktion.");

            while (true)
            {
                try
                {
                    Reference pickedRef = uidoc.Selection.PickObject(ObjectType.Face, filter, "Fläche wählen, ESC zum Fertigstellen");
                    if (pickedRef == null)
                        break;

                    Element? element = doc.GetElement(pickedRef);
                    GeometryObject? geometryObject = element?.GetGeometryObjectFromReference(pickedRef);
                    if (geometryObject is not Face face)
                        continue;

                    string stableId = string.Empty;
                    try
                    {
                        stableId = pickedRef.ConvertToStableRepresentation(doc);
                    }
                    catch
                    {
                        stableId = $"Element {element?.Id}";
                    }

                    faces.Add(CreateSelectedFaceInfo(face, stableId));
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }
            }

            return faces;
        }

        private static SelectedFaceInfo CreateSelectedFaceInfo(Face face, string stableId)
        {
            (XYZ point, XYZ normal) = GetRepresentativePointAndNormal(face);
            return new SelectedFaceInfo
            {
                Face = face,
                StableId = stableId,
                RepresentativePoint = point,
                RepresentativeNormal = normal
            };
        }

        private static (XYZ Point, XYZ Normal) GetRepresentativePointAndNormal(Face face)
        {
            Mesh mesh = face.Triangulate();
            if (mesh != null && mesh.NumTriangles > 0)
            {
                MeshTriangle tri = mesh.get_Triangle(0);
                XYZ p0 = tri.get_Vertex(0);
                XYZ p1 = tri.get_Vertex(1);
                XYZ p2 = tri.get_Vertex(2);
                XYZ point = (p0 + p1 + p2) / 3.0;
                XYZ normal = (p1 - p0).CrossProduct(p2 - p0);
                if (normal.GetLength() > DistanceTolerance)
                    return (point, normal.Normalize());
            }

            BoundingBoxUV box = face.GetBoundingBox();
            UV uv = (box.Min + box.Max) / 2.0;
            XYZ eval = face.Evaluate(uv);
            XYZ n = face.ComputeNormal(uv).Normalize();
            return (eval, n);
        }

        private static bool TryBuildSolidFromSelection(List<SelectedFaceInfo> selectedFaces, double shortCurveTolerance, out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = string.Empty;

            List<SelectedFaceInfo> planarFaces = selectedFaces.Where(x => x.Face is PlanarFace).ToList();
            List<SelectedFaceInfo> cylindricalFaces = selectedFaces.Where(x => x.Face is CylindricalFace).ToList();

            if (cylindricalFaces.Count >= 2 && planarFaces.Count >= 1 &&
                TryBuildCoaxialHollowCylindricalSolid(cylindricalFaces, planarFaces, out solid, out diagnostic))
            {
                return true;
            }

            if (cylindricalFaces.Count >= 1 && planarFaces.Count >= 1 &&
                TryBuildCylindricalSolid(cylindricalFaces[0], planarFaces, out solid, out diagnostic))
            {
                return true;
            }

            if (planarFaces.Count >= 4 && TryBuildConvexSolidFromPlanes(planarFaces, out solid, out diagnostic))
            {
                return true;
            }

            return TryBuildTriangulatedSolid(selectedFaces, shortCurveTolerance, out solid, out diagnostic);
        }

        private static bool TryBuildTriangulatedSolid(List<SelectedFaceInfo> selectedFaces, double shortCurveTolerance, out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = string.Empty;

            try
            {
                List<Mesh> meshes = selectedFaces
                    .Select(x => x.Face.Triangulate())
                    .Where(x => x != null && x.NumTriangles > 0)
                    .ToList();

                if (meshes.Count == 0)
                {
                    diagnostic = "Die ausgewählten Flächen konnten nicht trianguliert werden.";
                    return false;
                }

                if (!FamilyGeometryTools.TryConvertMeshesToSolid(meshes, shortCurveTolerance, out Solid triangulatedSolid, out string meshDiagnostic))
                {
                    diagnostic =
                        "Die ausgewählten Flächen konnten auch trianguliert nicht zu einem geschlossenen Volumenkörper aufgebaut werden.\n\n" +
                        "Mögliche Ursachen:\n" +
                        "- die Flächen bilden keine vollständig geschlossene Hülle\n" +
                        "- an gemeinsamen Kanten entstehen kleine Spalte/Toleranzfehler\n" +
                        "- innere/äußere Flächen sind topologisch nicht sauber verbunden\n\n" +
                        meshDiagnostic;
                    return false;
                }

                solid = triangulatedSolid;
                diagnostic =
                    $"Volumenkörper über triangulierten Fallback aus {selectedFaces.Count} Flächen erzeugt.\n" +
                    $"Triangulierte Teilflächen: {meshes.Count}";
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = "Fehler beim triangulierten Volumenkörper-Fallback: " + ex.Message;
                return false;
            }
        }

        private static bool TryBuildConvexSolidFromPlanes(List<SelectedFaceInfo> planarFaces, out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = string.Empty;

            try
            {
                List<XYZ> points = planarFaces.Select(x => x.RepresentativePoint).ToList();
                solid = CreateSeedBox(points);

                foreach (SelectedFaceInfo faceInfo in planarFaces)
                {
                    Plane cutPlane = CreateInteriorHalfSpacePlane(faceInfo, points);
                    solid = BooleanOperationsUtils.CutWithHalfSpace(solid, cutPlane);
                    if (solid == null || solid.Volume <= VolumeTolerance)
                    {
                        diagnostic =
                            "Die gewählten Ebenen schließen keinen gültigen konvexen Körper ein.\n\n" +
                            "Mögliche Ursachen:\n" +
                            "- Flächen begrenzen keinen geschlossenen Raum\n" +
                            "- Flächen sind widersprüchlich orientiert\n" +
                            "- der resultierende Körper wäre degeneriert oder leer";
                        return false;
                    }
                }

                diagnostic = $"Planarer Körper aus {planarFaces.Count} Begrenzungsflächen erzeugt.";
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = "Fehler beim Konstruieren des planaren Volumenkörpers: " + ex.Message;
                return false;
            }
        }

        private static bool TryBuildCylindricalSolid(SelectedFaceInfo cylindricalFaceInfo, List<SelectedFaceInfo> planarFaces, out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = string.Empty;

            try
            {
                if (cylindricalFaceInfo.Face is not CylindricalFace cylindricalFace)
                {
                    diagnostic = "Die ausgewählte Mantelfläche ist keine Zylinderfläche.";
                    return false;
                }

                XYZ axisOrigin = cylindricalFace.Origin;
                XYZ axisDirection = cylindricalFace.Axis.Normalize();
                double radius = TryGetCylinderRadius(cylindricalFace);
                if (radius <= DistanceTolerance)
                {
                    diagnostic = "Der Radius der gewählten Zylinderfläche konnte nicht bestimmt werden.";
                    return false;
                }

                List<double> axisPositions = GetAxisPositionsFromFaceLoops(cylindricalFace, axisOrigin, axisDirection);
                axisPositions.AddRange(planarFaces.Select(x => (x.RepresentativePoint - axisOrigin).DotProduct(axisDirection)));
                if (axisPositions.Count < 2)
                {
                    diagnostic = "Es konnten nicht genügend Begrenzungen entlang der Zylinderachse bestimmt werden.";
                    return false;
                }

                double min = axisPositions.Min();
                double max = axisPositions.Max();
                double margin = Math.Max(radius * 2.0, 1.0);
                double start = min - margin;
                double height = Math.Max((max - min) + (2.0 * margin), margin * 2.0);

                CurveLoop circleLoop = CreateCircleLoop(axisOrigin + axisDirection * start, axisDirection, radius);
                solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { circleLoop }, axisDirection, height);

                List<XYZ> orientationPoints = planarFaces.Select(x => x.RepresentativePoint).Append(cylindricalFaceInfo.RepresentativePoint).ToList();
                foreach (SelectedFaceInfo faceInfo in planarFaces)
                {
                    Plane cutPlane = CreateInteriorHalfSpacePlane(faceInfo, orientationPoints);
                    solid = BooleanOperationsUtils.CutWithHalfSpace(solid, cutPlane);
                    if (solid == null || solid.Volume <= VolumeTolerance)
                    {
                        diagnostic = "Die planaren Begrenzungsflächen schneiden den Zylinder nicht zu einem gültigen Volumenkörper.";
                        return false;
                    }
                }

                diagnostic = $"Zylindrischer Volumenkörper aus 1 Mantelfläche und {planarFaces.Count} planaren Begrenzungen erzeugt.";
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = "Fehler beim Konstruieren des zylindrischen Volumenkörpers: " + ex.Message;
                return false;
            }
        }

        private static bool TryBuildCoaxialHollowCylindricalSolid(List<SelectedFaceInfo> cylindricalFaces, List<SelectedFaceInfo> planarFaces, out Solid? solid, out string diagnostic)
        {
            solid = null;
            diagnostic = string.Empty;

            try
            {
                if (cylindricalFaces.Count < 2 ||
                    cylindricalFaces[0].Face is not CylindricalFace cylinderA ||
                    cylindricalFaces[1].Face is not CylindricalFace cylinderB)
                {
                    diagnostic = "Für den Rohr-/Hohlkörperpfad wurden zwei Zylinderflächen benötigt.";
                    return false;
                }

                if (!TryGetSharedCylinderAxis(cylinderA, cylinderB, out XYZ axisOrigin, out XYZ axisDirection, out diagnostic))
                    return false;

                double radiusA = TryGetCylinderRadius(cylinderA);
                double radiusB = TryGetCylinderRadius(cylinderB);
                if (radiusA <= DistanceTolerance || radiusB <= DistanceTolerance)
                {
                    diagnostic = "Mindestens einer der beiden Zylinderradien konnte nicht bestimmt werden.";
                    return false;
                }

                CylindricalFace outerCylinder = radiusA >= radiusB ? cylinderA : cylinderB;
                CylindricalFace innerCylinder = radiusA >= radiusB ? cylinderB : cylinderA;
                double outerRadius = Math.Max(radiusA, radiusB);
                double innerRadius = Math.Min(radiusA, radiusB);

                if (outerRadius - innerRadius <= DistanceTolerance)
                {
                    if (!TryInferPipeRadiiFromPlanarFaces(planarFaces, axisOrigin, axisDirection, outerRadius, out outerRadius, out innerRadius, out string inferredRadiusDiagnostic))
                    {
                        diagnostic =
                            "Die beiden Zylinderflächen haben praktisch den gleichen Radius; daraus kann kein Hohlkörper aufgebaut werden.\n\n" +
                            inferredRadiusDiagnostic;
                        return false;
                    }

                    diagnostic = inferredRadiusDiagnostic;
                }

                List<double> axisPositions = new List<double>();
                axisPositions.AddRange(GetAxisPositionsFromFaceLoops(outerCylinder, axisOrigin, axisDirection));
                axisPositions.AddRange(GetAxisPositionsFromFaceLoops(innerCylinder, axisOrigin, axisDirection));
                axisPositions.AddRange(planarFaces.Select(x => (x.RepresentativePoint - axisOrigin).DotProduct(axisDirection)));
                if (axisPositions.Count < 2)
                {
                    diagnostic = "Es konnten nicht genügend Begrenzungen entlang der gemeinsamen Zylinderachse abgeleitet werden.";
                    return false;
                }

                double min = axisPositions.Min();
                double max = axisPositions.Max();
                double margin = Math.Max(outerRadius * 2.0, 1.0);
                double start = min - margin;
                double height = Math.Max((max - min) + (2.0 * margin), margin * 2.0);

                CurveLoop outerLoop = CreateCircleLoop(axisOrigin + axisDirection * start, axisDirection, outerRadius);
                CurveLoop innerLoop = CreateCircleLoop(axisOrigin + axisDirection * start, axisDirection, innerRadius);

                Solid outerSolid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { outerLoop }, axisDirection, height);
                Solid innerSolid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { innerLoop }, axisDirection, height);

                List<XYZ> orientationPoints = planarFaces.Select(x => x.RepresentativePoint)
                    .Append(cylindricalFaces[0].RepresentativePoint)
                    .Append(cylindricalFaces[1].RepresentativePoint)
                    .ToList();

                foreach (SelectedFaceInfo faceInfo in planarFaces)
                {
                    Plane cutPlane = CreateInteriorHalfSpacePlane(faceInfo, orientationPoints);
                    outerSolid = BooleanOperationsUtils.CutWithHalfSpace(outerSolid, cutPlane);
                    innerSolid = BooleanOperationsUtils.CutWithHalfSpace(innerSolid, cutPlane);

                    if (outerSolid == null || outerSolid.Volume <= VolumeTolerance)
                    {
                        diagnostic = "Die planaren Begrenzungsflächen schneiden den äußeren Zylinder nicht zu einem gültigen Volumenkörper.";
                        return false;
                    }
                }

                solid = BooleanOperationsUtils.ExecuteBooleanOperation(outerSolid, innerSolid, BooleanOperationsType.Difference);
                if (solid == null || solid.Volume <= VolumeTolerance)
                {
                    diagnostic = "Der innere Zylinder konnte nicht sauber vom äußeren Zylinder abgezogen werden.";
                    return false;
                }

                string prefix = string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : diagnostic + "\n\n";
                diagnostic = prefix +
                    $"Hohlzylinder/rohrförmiger Körper aus 2 koaxialen Zylinderflächen und {planarFaces.Count} planaren Begrenzungen erzeugt.\n" +
                    $"Außenradius: {outerRadius:F4}, Innenradius: {innerRadius:F4}";
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = "Fehler beim Konstruieren des hohlzylindrischen Körpers: " + ex.Message;
                return false;
            }
        }

        private static bool TryGetSharedCylinderAxis(CylindricalFace cylinderA, CylindricalFace cylinderB, out XYZ axisOrigin, out XYZ axisDirection, out string diagnostic)
        {
            axisOrigin = cylinderA.Origin;
            axisDirection = cylinderA.Axis.Normalize();
            diagnostic = string.Empty;

            XYZ otherDirection = cylinderB.Axis.Normalize();
            double dot = axisDirection.DotProduct(otherDirection);
            if (Math.Abs(Math.Abs(dot) - 1.0) > 1e-4)
            {
                diagnostic = "Die beiden Zylinderflächen sind nicht parallel und können daher nicht als gemeinsamer Rohrkörper interpretiert werden.";
                return false;
            }

            XYZ delta = cylinderB.Origin - axisOrigin;
            XYZ perpendicular = delta - axisDirection.Multiply(delta.DotProduct(axisDirection));
            double axisOffset = perpendicular.GetLength();
            if (axisOffset > 1e-4)
            {
                diagnostic = $"Die beiden Zylinderflächen sind nicht koaxial (Achsversatz {axisOffset:F6}).";
                return false;
            }

            return true;
        }

        private static bool TryInferPipeRadiiFromPlanarFaces(List<SelectedFaceInfo> planarFaces, XYZ axisOrigin, XYZ axisDirection, double selectedCylinderRadius, out double outerRadius, out double innerRadius, out string diagnostic)
        {
            outerRadius = selectedCylinderRadius;
            innerRadius = 0;
            diagnostic = string.Empty;

            List<double> radii = new List<double>();
            foreach (SelectedFaceInfo faceInfo in planarFaces)
            {
                if (faceInfo.Face is not PlanarFace planarFace)
                    continue;

                foreach (CurveLoop loop in OrderPlanarLoopsByArea(planarFace))
                {
                    if (TryGetAverageLoopRadius(loop, axisOrigin, axisDirection, out double radius))
                        radii.Add(radius);
                }
            }

            List<double> distinct = CollapseDistinctValues(radii);
            if (!distinct.Any(r => Math.Abs(r - selectedCylinderRadius) <= 1e-4))
                distinct.Add(selectedCylinderRadius);

            distinct = distinct.OrderBy(x => x).ToList();
            if (distinct.Count < 2)
            {
                diagnostic =
                    "Auch aus den planaren Stirnflächen konnten keine zwei unterschiedlichen konzentrischen Radien abgeleitet werden.\n" +
                    "Bitte wählen Sie möglichst Innen- und Außenmantel sowie beide Stirnflächen des Rohres.";
                return false;
            }

            innerRadius = distinct.First();
            outerRadius = distinct.Last();
            if (outerRadius - innerRadius <= DistanceTolerance)
            {
                diagnostic = "Aus den planaren Stirnflächen wurden zwar Ring-Loops erkannt, aber kein belastbarer Innen-/Außenradius.";
                return false;
            }

            diagnostic =
                "Die beiden ausgewählten Zylinderflächen liegen auf demselben Radius. " +
                "Der fehlende zweite Radius wurde deshalb aus den Ring-Loops der planaren Stirnflächen abgeleitet.";
            return true;
        }

        private static Plane CreateInteriorHalfSpacePlane(SelectedFaceInfo faceInfo, List<XYZ> orientationPoints)
        {
            XYZ normal = faceInfo.RepresentativeNormal.Normalize();
            XYZ origin = faceInfo.RepresentativePoint;

            List<XYZ> otherPoints = orientationPoints.Where(p => p.DistanceTo(origin) > 1e-6).ToList();
            if (otherPoints.Count > 0)
            {
                double avg = otherPoints.Average(p => (p - origin).DotProduct(normal));
                if (avg > 0)
                    normal = normal.Negate();
            }

            return Plane.CreateByNormalAndOrigin(normal, origin);
        }

        private static Solid CreateSeedBox(List<XYZ> points)
        {
            double minX = points.Min(p => p.X);
            double minY = points.Min(p => p.Y);
            double minZ = points.Min(p => p.Z);
            double maxX = points.Max(p => p.X);
            double maxY = points.Max(p => p.Y);
            double maxZ = points.Max(p => p.Z);

            double span = Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ - minZ);
            double margin = Math.Max(span, 1.0);

            XYZ p0 = new XYZ(minX - margin, minY - margin, minZ - margin);
            XYZ p1 = new XYZ(maxX + margin, minY - margin, minZ - margin);
            XYZ p2 = new XYZ(maxX + margin, maxY + margin, minZ - margin);
            XYZ p3 = new XYZ(minX - margin, maxY + margin, minZ - margin);

            CurveLoop baseLoop = new CurveLoop();
            baseLoop.Append(Line.CreateBound(p0, p1));
            baseLoop.Append(Line.CreateBound(p1, p2));
            baseLoop.Append(Line.CreateBound(p2, p3));
            baseLoop.Append(Line.CreateBound(p3, p0));

            double height = (maxZ - minZ) + (2.0 * margin);
            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { baseLoop }, XYZ.BasisZ, height);
        }

        private static CurveLoop CreateCircleLoop(XYZ center, XYZ axisDirection, double radius)
        {
            XYZ xAxis = GetPerpendicularAxis(axisDirection);
            XYZ yAxis = axisDirection.CrossProduct(xAxis).Normalize();

            Arc arc1 = Arc.Create(center, radius, 0, Math.PI, xAxis, yAxis);
            Arc arc2 = Arc.Create(center, radius, Math.PI, Math.PI * 2.0, xAxis, yAxis);

            CurveLoop loop = new CurveLoop();
            loop.Append(arc1);
            loop.Append(arc2);
            return loop;
        }

        private static XYZ GetPerpendicularAxis(XYZ axisDirection)
        {
            XYZ candidate = Math.Abs(axisDirection.DotProduct(XYZ.BasisZ)) < 0.99 ? XYZ.BasisZ : XYZ.BasisX;
            return axisDirection.CrossProduct(candidate).Normalize();
        }

        private static List<double> GetAxisPositionsFromFaceLoops(Face face, XYZ axisOrigin, XYZ axisDirection)
        {
            List<double> positions = new List<double>();
            foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
            {
                foreach (XYZ point in SampleCurveLoop(loop))
                {
                    positions.Add((point - axisOrigin).DotProduct(axisDirection));
                }
            }

            return positions;
        }

        private static double TryGetCylinderRadius(CylindricalFace cylindricalFace)
        {
            CurveLoop? firstLoop = cylindricalFace.GetEdgesAsCurveLoops().FirstOrDefault();
            if (firstLoop == null)
                return 0;

            XYZ center = cylindricalFace.Origin;
            XYZ axis = cylindricalFace.Axis.Normalize();
            List<double> distances = SampleCurveLoop(firstLoop)
                .Select(p =>
                {
                    XYZ delta = p - center;
                    XYZ perpendicular = delta - axis.Multiply(delta.DotProduct(axis));
                    return perpendicular.GetLength();
                })
                .Where(d => d > DistanceTolerance)
                .ToList();

            return distances.Count == 0 ? 0 : distances.Average();
        }

        private static List<CurveLoop> OrderPlanarLoopsByArea(PlanarFace planarFace)
        {
            XYZ xAxis = planarFace.XVector.Normalize();
            XYZ yAxis = planarFace.YVector.Normalize();
            return planarFace.GetEdgesAsCurveLoops()
                .Select(loop => new { Loop = loop, Area = Math.Abs(ComputePlanarLoopArea(loop, planarFace.Origin, xAxis, yAxis)) })
                .OrderByDescending(x => x.Area)
                .Select(x => x.Loop)
                .ToList();
        }

        private static bool TryGetAverageLoopRadius(CurveLoop loop, XYZ axisOrigin, XYZ axisDirection, out double radius)
        {
            radius = 0;
            List<double> distances = SampleCurveLoop(loop)
                .Select(point => GetDistanceToAxis(point, axisOrigin, axisDirection))
                .Where(distance => distance > DistanceTolerance)
                .ToList();

            if (distances.Count < 3)
                return false;

            double average = distances.Average();
            double maxDeviation = distances.Max(d => Math.Abs(d - average));
            double allowedDeviation = Math.Max(1e-4, average * 1e-3);
            if (maxDeviation > allowedDeviation)
                return false;

            radius = average;
            return true;
        }

        private static double GetDistanceToAxis(XYZ point, XYZ axisOrigin, XYZ axisDirection)
        {
            XYZ delta = point - axisOrigin;
            XYZ perpendicular = delta - axisDirection.Multiply(delta.DotProduct(axisDirection));
            return perpendicular.GetLength();
        }

        private static List<double> CollapseDistinctValues(IEnumerable<double> values)
        {
            List<double> ordered = values.OrderBy(x => x).ToList();
            List<double> collapsed = new List<double>();
            foreach (double value in ordered)
            {
                if (collapsed.Count == 0 || Math.Abs(collapsed[^1] - value) > 1e-4)
                    collapsed.Add(value);
            }

            return collapsed;
        }

        private static double ComputePlanarLoopArea(CurveLoop loop, XYZ planeOrigin, XYZ xAxis, XYZ yAxis)
        {
            List<XYZ> points = SampleCurveLoop(loop);
            if (points.Count < 3)
                return 0;

            double area = 0;
            for (int i = 0; i < points.Count; i++)
            {
                XYZ current = points[i] - planeOrigin;
                XYZ next = points[(i + 1) % points.Count] - planeOrigin;
                double x1 = current.DotProduct(xAxis);
                double y1 = current.DotProduct(yAxis);
                double x2 = next.DotProduct(xAxis);
                double y2 = next.DotProduct(yAxis);
                area += (x1 * y2) - (x2 * y1);
            }

            return area * 0.5;
        }

        private static string AppendSelectionDebugLog(List<SelectedFaceInfo> selectedFaces, string diagnostic)
        {
            try
            {
                string logPath = WriteSelectionDebugLog(selectedFaces, diagnostic);
                return diagnostic + "\n\nDetailliertes Debug-Log: " + logPath;
            }
            catch (Exception ex)
            {
                return diagnostic + "\n\nZusätzlich konnte kein Debug-Log geschrieben werden: " + ex.Message;
            }
        }

        private static string WriteSelectionDebugLog(List<SelectedFaceInfo> selectedFaces, string diagnostic)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string logDir = Path.Combine(desktop, "BIMassist_DebugLogs");
            Directory.CreateDirectory(logDir);

            string logPath = Path.Combine(logDir, $"ConstructVolume_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("BIMassist - Volumenkörper konstruieren Debug-Log");
            sb.AppendLine($"Zeit: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("Fehlermeldung:");
            sb.AppendLine(diagnostic ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine($"Ausgewählte Flächen: {selectedFaces.Count}");

            List<SelectedFaceInfo> cylindricalFaces = selectedFaces.Where(x => x.Face is CylindricalFace).ToList();
            List<SelectedFaceInfo> planarFaces = selectedFaces.Where(x => x.Face is PlanarFace).ToList();
            sb.AppendLine($"Planar: {planarFaces.Count}");
            sb.AppendLine($"Zylindrisch: {cylindricalFaces.Count}");
            sb.AppendLine();

            for (int i = 0; i < selectedFaces.Count; i++)
            {
                SelectedFaceInfo info = selectedFaces[i];
                sb.AppendLine($"[{i + 1}] StableId: {info.StableId}");
                sb.AppendLine($"    Typ: {info.Face.GetType().Name}");
                sb.AppendLine($"    Fläche: {SafeFormat(() => info.Face.Area)}");
                sb.AppendLine($"    Repräsentativer Punkt: {FormatXyz(info.RepresentativePoint)}");
                sb.AppendLine($"    Repräsentative Normale: {FormatXyz(info.RepresentativeNormal)}");
                sb.AppendLine($"    Dreiecke: {SafeFormat(() => info.Face.Triangulate().NumTriangles)}");

                if (info.Face is PlanarFace planarFace)
                {
                    sb.AppendLine($"    Planare FaceOrigin: {FormatXyz(planarFace.Origin)}");
                    sb.AppendLine($"    Planare FaceNormal: {FormatXyz(planarFace.FaceNormal)}");
                    sb.AppendLine($"    Rand-Loops: {SafeFormat(() => planarFace.GetEdgesAsCurveLoops().Count)}");

                    List<CurveLoop> orderedLoops = OrderPlanarLoopsByArea(planarFace);
                    XYZ xAxis = planarFace.XVector.Normalize();
                    XYZ yAxis = planarFace.YVector.Normalize();
                    for (int loopIndex = 0; loopIndex < orderedLoops.Count; loopIndex++)
                    {
                        CurveLoop loop = orderedLoops[loopIndex];
                        double loopArea = Math.Abs(ComputePlanarLoopArea(loop, planarFace.Origin, xAxis, yAxis));
                        sb.AppendLine($"    Loop {loopIndex + 1} Fläche: {loopArea:F9}");
                    }
                }
                else if (info.Face is CylindricalFace cylindricalFace)
                {
                    double radius = TryGetCylinderRadius(cylindricalFace);
                    XYZ axisDirection = cylindricalFace.Axis.Normalize();
                    List<double> axisPositions = GetAxisPositionsFromFaceLoops(cylindricalFace, cylindricalFace.Origin, axisDirection);
                    sb.AppendLine($"    Zylinder-Origin: {FormatXyz(cylindricalFace.Origin)}");
                    sb.AppendLine($"    Zylinder-Achse: {FormatXyz(axisDirection)}");
                    sb.AppendLine($"    Radius: {radius:F9}");
                    sb.AppendLine($"    Rand-Loops: {SafeFormat(() => cylindricalFace.GetEdgesAsCurveLoops().Count)}");
                    if (axisPositions.Count > 0)
                        sb.AppendLine($"    Achsposition min/max: {axisPositions.Min():F9} / {axisPositions.Max():F9}");
                }

                sb.AppendLine();
            }

            if (cylindricalFaces.Count >= 2 && cylindricalFaces[0].Face is CylindricalFace cylinderA && cylindricalFaces[1].Face is CylindricalFace cylinderB)
            {
                sb.AppendLine("Zylindervergleich:");
                sb.AppendLine($"    Radius A: {TryGetCylinderRadius(cylinderA):F9}");
                sb.AppendLine($"    Radius B: {TryGetCylinderRadius(cylinderB):F9}");
                sb.AppendLine($"    Radius-Differenz: {Math.Abs(TryGetCylinderRadius(cylinderA) - TryGetCylinderRadius(cylinderB)):F9}");
                sb.AppendLine($"    Achsen-Dotprodukt: {cylinderA.Axis.Normalize().DotProduct(cylinderB.Axis.Normalize()):F9}");
                XYZ axisDelta = cylinderB.Origin - cylinderA.Origin;
                XYZ axisDirection = cylinderA.Axis.Normalize();
                XYZ axisPerp = axisDelta - axisDirection.Multiply(axisDelta.DotProduct(axisDirection));
                sb.AppendLine($"    Achsversatz: {axisPerp.GetLength():F9}");
            }

            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
            return logPath;
        }

        private static string FormatXyz(XYZ xyz)
        {
            return $"({xyz.X:F9}, {xyz.Y:F9}, {xyz.Z:F9})";
        }

        private static string SafeFormat(Func<double> getter)
        {
            try
            {
                return getter().ToString("F9");
            }
            catch (Exception ex)
            {
                return $"<Fehler: {ex.Message}>";
            }
        }

        private static List<XYZ> SampleCurveLoop(CurveLoop loop)
        {
            List<XYZ> points = new List<XYZ>();
            foreach (Curve curve in loop)
            {
                IList<XYZ> tess = curve.Tessellate();
                if (tess.Count == 0)
                    continue;

                if (points.Count > 0 && points[^1].DistanceTo(tess[0]) <= 1e-9)
                {
                    for (int i = 1; i < tess.Count; i++)
                        points.Add(tess[i]);
                }
                else
                {
                    points.AddRange(tess);
                }
            }

            if (points.Count > 1 && points[0].DistanceTo(points[^1]) <= 1e-9)
                points.RemoveAt(points.Count - 1);

            return points;
        }

        private sealed class FaceSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => true;
            public bool AllowReference(Reference reference, XYZ position) => reference.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_SURFACE;
        }

        private sealed class SelectedFaceInfo
        {
            public required Face Face { get; init; }
            public required string StableId { get; init; }
            public required XYZ RepresentativePoint { get; init; }
            public required XYZ RepresentativeNormal { get; init; }
        }
    }
}
