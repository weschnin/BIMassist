using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;
using System.IO;

namespace BIMassist.Commands
{
    /// <summary>
    /// Bodenverdrängung: Erstellt einen geschlossenen VOLUMENKÖRPER als Abzugskörper-Familie.
    /// 
    /// ANSATZ:
    /// 1. Original-Solid(s) des Elements werden verwendet
    /// 2. Benutzer wählt Kanten der Öffnungen
    /// 3. Für jede Öffnung wird ein Füllkörper (Extrusion) erstellt
    /// 4. Alle Körper werden per Boolean Union vereinigt
    /// 5. Ergebnis wird als Abzugskörper-Familie erstellt
    /// 6. Quellkörper wird gelöscht
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class BodenverdrängungCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // ===== SCHRITT 1: Element auswählen =====
                TaskDialog.Show("Bodenverdrängung - Schritt 1/2",
                    "Bitte wählen Sie das 3D-Element aus.");

                Element? selectedElement = SelectElement(uidoc, doc);
                if (selectedElement == null)
                {
                    TaskDialog.Show("Bodenverdrängung", "Kein Element ausgewählt.");
                    return Result.Cancelled;
                }

                ElementId sourceElementId = selectedElement.Id;
                string elementName = selectedElement.Name;

                // ===== SCHRITT 2: Solids extrahieren =====
                List<Solid> originalSolids = ExtractSolids(selectedElement);
                if (originalSolids.Count == 0)
                {
                    TaskDialog.Show("Bodenverdrängung", "Keine gültige 3D-Geometrie (Solids) gefunden.");
                    return Result.Failed;
                }

                // Original-Volumen berechnen
                double originalVolume = originalSolids.Sum(s => s.Volume);
                BoundingBoxXYZ? bbox = selectedElement.get_BoundingBox(null);

                // ===== SCHRITT 3: Benutzer wählt Stirnflächen zum Schließen =====
                TaskDialog.Show("Bodenverdrängung - Schritt 2/2",
                    "Wählen Sie Stirnflächen aus, die geschlossen werden sollen.\n\n" +
                    "• Für jede gewählte Fläche wird nur der äußere Rand übernommen\n" +
                    "• Innere Kanten/Loops werden entfernt\n" +
                    "• Der resultierende Körper wird ohne Extrusionen über TessellatedShapeBuilder neu aufgebaut\n" +
                    "• Drücken Sie ESC wenn Sie fertig sind");

                List<SelectedCapFace> selectedCapFaces = SelectCapFaces(uidoc, doc, sourceElementId);

                if (selectedCapFaces.Count == 0)
                {
                    TaskDialogResult confirmResult = TaskDialog.Show("Bodenverdrängung",
                        "Keine Stirnflächen ausgewählt.\n\n" +
                        "Soll nur das Original-Solid als Abzugskörper-Familie erstellt werden?",
                        TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

                    if (confirmResult != TaskDialogResult.Yes)
                        return Result.Cancelled;
                }

                // ===== SCHRITT 4: Vollkörper ohne Extrusionen neu aufbauen =====
                RebuildResult? rebuildResult = RebuildSolidWithTessellatedShapeBuilder(selectedElement, selectedCapFaces);
                if (rebuildResult?.Solid == null)
                {
                    string failureLogPath = WriteDebugLogFile(rebuildResult?.DebugLog ?? "Kein Debug-Log verfügbar.", familyName: null);
                    string failureReason = string.IsNullOrWhiteSpace(rebuildResult?.ErrorMessage)
                        ? "Konnte keinen geschlossenen Vollkörper per TessellatedShapeBuilder erzeugen."
                        : rebuildResult!.ErrorMessage;
                    TaskDialog.Show("Bodenverdrängung",
                        failureReason + "\n\n" +
                        $"Debug-Log: {failureLogPath}");
                    return Result.Failed;
                }

                int filledOpenings = rebuildResult.ClosedCaps;
                double finalVolume = rebuildResult.Solid.Volume;
                string debugLogPath = WriteDebugLogFile(rebuildResult.DebugLog, familyName: null);

                // ===== SCHRITT 5: Abzugskörper-Familie erstellen =====
                // Das Solid behält seine Original-Koordinaten in der Familie.
                // Die Familie wird bei (0,0,0) platziert, sodass das Solid
                // exakt an seiner ursprünglichen Position erscheint.
                string familyName = $"Bodenverdrängung_{elementName}_{DateTime.Now:yyyyMMdd_HHmmss}";

                FamilyInstance? voidFamilyInstance = FamilyGeometryTools.CreateVoidFamily(
                    uiapp, doc, rebuildResult.Solid, familyName);

                if (voidFamilyInstance == null)
                {
                    TaskDialog.Show("Bodenverdrängung", "Abzugskörper-Familie konnte nicht erstellt werden.");
                    return Result.Failed;
                }

                // HINWEIS: Quellkörper wird NICHT gelöscht - der Benutzer kann das manuell tun

                double volumeIncrease = finalVolume - originalVolume;

                TaskDialog.Show("Bodenverdrängung - Erfolg",
                    $"ABZUGSKÖRPER-FAMILIE wurde erstellt!\n\n" +
                    $"Familienname: {familyName}\n\n" +
                    $"Original-Volumen: {originalVolume:F4} cubic feet\n" +
                    $"End-Volumen: {finalVolume:F4} cubic feet\n" +
                    $"Volumenzuwachs: {volumeIncrease:F4} cubic feet\n\n" +
                    $"Geschlossene Stirnflächen: {filledOpenings} von {selectedCapFaces.Count}\n" +
                    $"Entfernte Innenkanten-Gruppen: {rebuildResult.RemovedInnerLoopCount}\n\n" +
                    $"Match-Diagnostik:\n{rebuildResult.MatchDiagnostics}\n\n" +
                    $"Debug-Logdatei:\n{debugLogPath}\n\n" +
                    $"✓ Abzugskörper erstellt\n" +
                    $"✓ 'Beim Laden schneiden' aktiviert\n\n" +
                    $"Hinweis: Der Quellkörper wurde NICHT gelöscht.");

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Fehler", $"{ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        #region ===== SOLID OPERATIONEN =====

        /// <summary>
        /// Vereinigt mehrere Solids zu einem.
        /// </summary>
        private Solid? CombineSolids(List<Solid> solids)
        {
            if (solids.Count == 0) return null;
            if (solids.Count == 1) return solids[0];

            Solid result = solids[0];

            for (int i = 1; i < solids.Count; i++)
            {
                try
                {
                    Solid? union = BooleanOperationsUtils.ExecuteBooleanOperation(
                        result, solids[i], BooleanOperationsType.Union);

                    if (union != null && union.Volume > 0)
                        result = union;
                }
                catch { }
            }

            return result;
        }

        private enum OpeningSelectionKind
        {
            EdgeLoop,
            FaceCapLoop
        }

        private class OpeningProfile
        {
            public required CurveLoop Loop { get; init; }
            public required XYZ Center { get; init; }
            public required XYZ Normal { get; init; }
            public required OpeningSelectionKind Kind { get; init; }
            public double Area { get; init; }
        }

        private class FillCandidate
        {
            public required Solid Solid { get; init; }
            public int ClosedOpenings { get; init; }
        }

        private List<FillCandidate> CreateFillCandidates(List<OpeningProfile> openingProfiles, Solid referenceSolid)
        {
            List<FillCandidate> candidates = new List<FillCandidate>();

            List<OpeningProfile> faceProfiles = openingProfiles
                .Where(p => p.Kind == OpeningSelectionKind.FaceCapLoop)
                .ToList();

            HashSet<int> pairedFaceIndices = new HashSet<int>();

            for (int i = 0; i < faceProfiles.Count; i++)
            {
                if (pairedFaceIndices.Contains(i))
                    continue;

                int bestMatch = FindOppositeFaceProfile(faceProfiles, i, pairedFaceIndices);
                if (bestMatch < 0)
                    continue;

                Solid? pairSolid = CreateFillSolidBetweenProfiles(faceProfiles[i], faceProfiles[bestMatch]);
                if (pairSolid != null && pairSolid.Volume > 0)
                {
                    candidates.Add(new FillCandidate
                    {
                        Solid = pairSolid,
                        ClosedOpenings = 2
                    });

                    pairedFaceIndices.Add(i);
                    pairedFaceIndices.Add(bestMatch);
                }
            }

            foreach (OpeningProfile edgeProfile in openingProfiles.Where(p => p.Kind == OpeningSelectionKind.EdgeLoop))
            {
                Solid? fillSolid = CreateFillSolid(edgeProfile.Loop, referenceSolid);
                if (fillSolid != null && fillSolid.Volume > 0)
                {
                    candidates.Add(new FillCandidate
                    {
                        Solid = fillSolid,
                        ClosedOpenings = 1
                    });
                }
            }

            return candidates;
        }

        private int FindOppositeFaceProfile(List<OpeningProfile> profiles, int index, HashSet<int> excluded)
        {
            OpeningProfile source = profiles[index];
            int bestMatch = -1;
            double bestScore = double.MinValue;
            XYZ sourceNormal = source.Normal.Normalize();

            for (int i = 0; i < profiles.Count; i++)
            {
                if (i == index || excluded.Contains(i))
                    continue;

                OpeningProfile candidate = profiles[i];
                XYZ candidateNormal = candidate.Normal.Normalize();

                double normalDot = sourceNormal.DotProduct(candidateNormal);
                // Bei Revit können die Flächennormalen je nach Referenzlage gleich- oder gegenläufig sein.
                // Für Stirnflächen eines Rohrs reicht es, wenn sie zueinander parallel sind.
                if (Math.Abs(normalDot) < 0.95)
                    continue;

                XYZ between = candidate.Center - source.Center;
                double distance = between.GetLength();
                if (distance < 0.01)
                    continue;

                double axialDistance = Math.Abs(between.DotProduct(sourceNormal));
                if (axialDistance < 0.01)
                    continue;

                XYZ projectedPoint = source.Center + sourceNormal * between.DotProduct(sourceNormal);
                double lateralOffset = candidate.Center.DistanceTo(projectedPoint);

                // Die Stirnflächen eines geraden Hohlkörpers müssen annähernd auf derselben Achse liegen.
                // Ein kleiner lateraler Versatz ist durch numerische Toleranzen erlaubt.
                double maxLateralOffset = Math.Max(0.01, axialDistance * 0.02);
                if (lateralOffset > maxLateralOffset)
                    continue;

                if (!AreasAreComparable(source.Area, candidate.Area))
                    continue;

                double score = axialDistance * 1000.0 - lateralOffset;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = i;
                }
            }

            return bestMatch;
        }

        private bool AreasAreComparable(double area1, double area2)
        {
            if (area1 < 1e-9 || area2 < 1e-9)
                return false;

            double ratio = area1 > area2 ? area1 / area2 : area2 / area1;
            return ratio <= 1.25;
        }

        private Solid? CreateFillSolidBetweenProfiles(OpeningProfile start, OpeningProfile end)
        {
            try
            {
                XYZ startNormal = start.Normal.Normalize();
                XYZ centerVector = end.Center - start.Center;
                double signedHeight = centerVector.DotProduct(startNormal);
                XYZ extrusionDir = signedHeight >= 0 ? startNormal : startNormal.Negate();
                double extrusionHeight = Math.Abs(signedHeight);

                // Fallback: falls die Flächennormale numerisch ungünstig ist, verwende die direkte
                // Verbindung der Profilzentren als letzte Rückfallebene.
                if (extrusionHeight < 0.001)
                {
                    extrusionHeight = centerVector.GetLength();
                    if (extrusionHeight >= 0.001)
                        extrusionDir = centerVector.Normalize();
                }

                if (extrusionHeight < 0.001)
                    return null;

                List<CurveLoop> profile = new List<CurveLoop> { start.Loop };

                return GeometryCreationUtilities.CreateExtrusionGeometry(profile, extrusionDir, extrusionHeight);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateFillSolidBetweenProfiles Fehler: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Erstellt einen Füllkörper für eine Öffnung.
        /// Der Körper wird durch den gesamten Hohlraum extrudiert (nicht nur bis zur nächsten Wand).
        /// Bei einem Becher oder einer einseitig offenen Geometrie wird ein offener Rand geschlossen.
        /// </summary>
        private Solid? CreateFillSolid(CurveLoop loop, Solid referenceSolid)
        {
            try
            {
                // Ebene der Öffnung ermitteln
                Plane? plane = loop.GetPlane();
                if (plane == null) return null;

                XYZ normal = plane.Normal;
                XYZ loopCenter = GetLoopCenter(loop);

                // Teste beide Richtungen und finde die LÄNGERE Distanz
                // Bei einem Rohr wollen wir durch den GESAMTEN Hohlraum extrudieren

                double dist1 = GetMaxDistanceToSolidInDirection(loopCenter, normal, referenceSolid);
                double dist2 = GetMaxDistanceToSolidInDirection(loopCenter, normal.Negate(), referenceSolid);

                XYZ extrusionDir;
                double extrusionHeight;

                // Wähle die Richtung mit der LÄNGEREN Distanz (füllt den gesamten Hohlraum)
                if (dist1 > 0 && dist2 > 0)
                {
                    // Beide Richtungen treffen das Solid - nimm die LÄNGERE (ganzer Hohlraum)
                    if (dist1 >= dist2)
                    {
                        extrusionDir = normal;
                        extrusionHeight = dist1;
                    }
                    else
                    {
                        extrusionDir = normal.Negate();
                        extrusionHeight = dist2;
                    }
                }
                else if (dist1 > 0)
                {
                    extrusionDir = normal;
                    extrusionHeight = dist1;
                }
                else if (dist2 > 0)
                {
                    extrusionDir = normal.Negate();
                    extrusionHeight = dist2;
                }
                else
                {
                    // Fallback: Verwende BoundingBox-Diagonale
                    BoundingBoxXYZ bbox = referenceSolid.GetBoundingBox();
                    extrusionHeight = bbox.Min.DistanceTo(bbox.Max);

                    XYZ solidCenter = GetSolidCenter(referenceSolid);
                    XYZ toCenter = (solidCenter - loopCenter).Normalize();
                    extrusionDir = normal.DotProduct(toCenter) > 0 ? normal : normal.Negate();
                }

                if (extrusionHeight < 0.001) 
                    return null;

                // Solid durch Extrusion erstellen
                List<CurveLoop> profile = new List<CurveLoop> { loop };
                Solid fillSolid = GeometryCreationUtilities.CreateExtrusionGeometry(
                    profile, extrusionDir, extrusionHeight);

                return fillSolid;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateFillSolid Fehler: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Berechnet die MAXIMALE Distanz vom Startpunkt durch das Solid hindurch.
        /// Bei einem Rohr ist das die Distanz zur gegenüberliegenden Außenwand.
        /// </summary>
        private double GetMaxDistanceToSolidInDirection(XYZ start, XYZ direction, Solid solid)
        {
            try
            {
                // Starte etwas versetzt, um nicht auf der aktuellen Fläche zu treffen
                XYZ rayStart = start + direction * 0.01;
                Line ray = Line.CreateBound(rayStart, rayStart + direction * 10000);

                SolidCurveIntersection intersection = solid.IntersectWithCurve(
                    ray, new SolidCurveIntersectionOptions());

                if (intersection.SegmentCount > 0)
                {
                    // Finde den WEITESTEN Schnittpunkt (nicht den nächsten!)
                    // Bei einem Rohr gibt es mehrere Treffer: Innenwand und Außenwand
                    // Wir wollen die Außenwand (größte Distanz)
                    double maxDist = 0;

                    for (int i = 0; i < intersection.SegmentCount; i++)
                    {
                        Curve segment = intersection.GetCurveSegment(i);

                        // Prüfe beide Endpunkte des Segments
                        XYZ hitPoint0 = segment.GetEndPoint(0);
                        XYZ hitPoint1 = segment.GetEndPoint(1);

                        double dist0 = start.DistanceTo(hitPoint0);
                        double dist1 = start.DistanceTo(hitPoint1);

                        if (dist0 > maxDist)
                            maxDist = dist0;
                        if (dist1 > maxDist)
                            maxDist = dist1;
                    }

                    if (maxDist > 0.01)
                        return maxDist;
                }
            }
            catch { }

            return 0; // Kein Treffer
        }

        /// <summary>
        /// Prüft ob ein Punkt innerhalb eines Solids liegt.
        /// </summary>
        private bool IsPointInsideSolid(XYZ point, Solid solid)
        {
            try
            {
                // Verwende SolidCurveIntersection um zu prüfen
                // Ein Punkt ist "innen" wenn ein Ray in jede Richtung das Solid trifft

                foreach (Face face in solid.Faces)
                {
                    IntersectionResult? result = face.Project(point);
                    if (result != null && result.Distance < 0.001)
                    {
                        return false; // Auf der Oberfläche = nicht innen
                    }
                }

                // Alternativer Test: Schieße Rays und zähle Treffer
                int hitCount = 0;
                XYZ[] testDirs = { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ };

                foreach (XYZ dir in testDirs)
                {
                    Line testLine = Line.CreateBound(point, point + dir * 1000);
                    SolidCurveIntersection intersection = solid.IntersectWithCurve(
                        testLine, new SolidCurveIntersectionOptions());

                    if (intersection.SegmentCount > 0)
                        hitCount++;
                }

                // Wenn Rays in alle Richtungen treffen, ist der Punkt wahrscheinlich innen
                return hitCount >= 2;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Berechnet die Extrusionshöhe für den inneren Hohlraum.
        /// </summary>
        private double CalculateInnerExtrusionHeight(CurveLoop loop, Solid solid, XYZ direction)
        {
            try
            {
                XYZ loopCenter = GetLoopCenter(loop);

                // Schieße einen Ray vom Loop-Zentrum in Extrusionsrichtung
                // und finde den ersten Schnittpunkt mit dem Solid
                Line ray = Line.CreateBound(loopCenter, loopCenter + direction * 1000);

                SolidCurveIntersection intersection = solid.IntersectWithCurve(
                    ray, new SolidCurveIntersectionOptions());

                if (intersection.SegmentCount > 0)
                {
                    // Finde die maximale Distanz - das ist die Tiefe des Hohlraums
                    double maxDist = 0;
                    for (int i = 0; i < intersection.SegmentCount; i++)
                    {
                        Curve segment = intersection.GetCurveSegment(i);
                        double dist = loopCenter.DistanceTo(segment.GetEndPoint(1));
                        if (dist > maxDist)
                            maxDist = dist;
                    }

                    return maxDist + 0.01; // Kleiner Puffer
                }

                // Fallback: BoundingBox-Diagonale
                BoundingBoxXYZ bbox = solid.GetBoundingBox();
                return bbox.Min.DistanceTo(bbox.Max);
            }
            catch
            {
                return 1.0;
            }
        }

        /// <summary>
        /// Berechnet das Zentrum eines CurveLoops.
        /// </summary>
        private XYZ GetLoopCenter(CurveLoop loop)
        {
            List<XYZ> points = new List<XYZ>();
            foreach (Curve curve in loop)
            {
                points.Add(curve.GetEndPoint(0));
            }

            XYZ sum = XYZ.Zero;
            foreach (XYZ p in points) sum += p;
            return sum / points.Count;
        }

        /// <summary>
        /// Berechnet das Zentrum eines Solids.
        /// </summary>
        private XYZ GetSolidCenter(Solid solid)
        {
            BoundingBoxXYZ bbox = solid.GetBoundingBox();
            return (bbox.Min + bbox.Max) / 2.0;
        }

        #endregion

        #region ===== KANTEN-AUSWAHL =====

        /// <summary>
        /// Lässt den Benutzer Flächen oder Kanten auswählen, die Öffnungen definieren.
        /// Flächen mit Innenloch liefern direkt die inneren Öffnungs-Loops,
        /// Kanten werden wie bisher zu einem geschlossenen Loop zusammengesetzt.
        /// </summary>
        private List<OpeningProfile> SelectOpeningProfiles(UIDocument uidoc, Document doc, ElementId elementId)
        {
            List<OpeningProfile> profiles = new List<OpeningProfile>();
            List<Curve> currentCurves = new List<Curve>();

            try
            {
                while (true)
                {
                    try
                    {
                        Reference pickedRef = uidoc.Selection.PickObject(
                            ObjectType.PointOnElement,
                            new OpeningSelectionFilter(elementId),
                            $"Fläche oder Kante auswählen ({currentCurves.Count} Kanten, {profiles.Count} Öffnungen). ESC zum Beenden.");

                        Element element = doc.GetElement(pickedRef);
                        if (element == null) continue;

                        GeometryObject geomObj = element.GetGeometryObjectFromReference(pickedRef);

                        if (geomObj is Face face)
                        {
                            List<OpeningProfile> faceProfiles = GetOpeningProfilesFromFace(face);
                            if (faceProfiles.Count == 0)
                            {
                                TaskDialog.Show("Bodenverdrängung",
                                    "Die ausgewählte Fläche liefert keine nutzbare Deckkontur.\n\n" +
                                    "Bitte wählen Sie eine planare Stirnfläche mit Innenloch oder alternativ die inneren Randkanten der Öffnung.");
                                continue;
                            }

                            profiles.AddRange(faceProfiles);
                            currentCurves.Clear();

                            TaskDialog.Show("Bodenverdrängung",
                                $"{faceProfiles.Count} Deckprofil(e) aus Fläche übernommen.\n\n" +
                                $"Öffnungen gesamt: {profiles.Count}\n\n" +
                                "Wählen Sie weitere Flächen/Kanten oder drücken Sie ESC zum Beenden.");
                        }
                        else if (geomObj is Edge edge)
                        {
                            Curve? curve = edge.AsCurve();
                            if (curve != null)
                            {
                                currentCurves.Add(curve);

                                // Versuche geschlossenen Loop zu bilden
                                CurveLoop? loop = TryFormClosedLoop(currentCurves);
                                if (loop != null)
                                {
                                    profiles.Add(CreateEdgeOpeningProfile(loop, element));
                                    currentCurves.Clear();

                                    TaskDialog.Show("Bodenverdrängung",
                                        $"Öffnung erkannt!\n\n" +
                                        $"Öffnungen gesamt: {profiles.Count}\n\n" +
                                        "Wählen Sie weitere Flächen/Kanten für eine andere Öffnung\n" +
                                        "oder drücken Sie ESC zum Beenden.");
                                }
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch { }

            return profiles;
        }

        private class OpeningSelectionFilter : ISelectionFilter
        {
            private readonly ElementId _elementId;
            public OpeningSelectionFilter(ElementId elementId) { _elementId = elementId; }
            public bool AllowElement(Element elem) => elem?.Id == _elementId;
            public bool AllowReference(Reference reference, XYZ position) => reference?.ElementId == _elementId;
        }

        private OpeningProfile CreateEdgeOpeningProfile(CurveLoop loop, Element sourceElement)
        {
            OpeningProfile? faceCapProfile = TryCreateFaceCapProfileFromEdgeLoop(loop, sourceElement);
            if (faceCapProfile != null)
                return faceCapProfile;

            Plane plane = loop.GetPlane();
            XYZ normal = plane.Normal.Normalize();
            XYZ center = GetLoopCenter(loop);
            double area = Math.Abs(ComputePlanarLoopArea(loop, plane.Origin, GetPlaneXAxis(plane.Normal), plane.Normal.CrossProduct(GetPlaneXAxis(plane.Normal)).Normalize()));

            return new OpeningProfile
            {
                Loop = loop,
                Center = center,
                Normal = normal,
                Kind = OpeningSelectionKind.EdgeLoop,
                Area = area
            };
        }

        private OpeningProfile? TryCreateFaceCapProfileFromEdgeLoop(CurveLoop selectedLoop, Element sourceElement)
        {
            try
            {
                Plane selectedPlane = selectedLoop.GetPlane();
                XYZ selectedCenter = GetLoopCenter(selectedLoop);
                XYZ selectedNormal = selectedPlane.Normal.Normalize();
                XYZ selectedXAxis = GetPlaneXAxis(selectedNormal);
                XYZ selectedYAxis = selectedNormal.CrossProduct(selectedXAxis).Normalize();
                double selectedArea = Math.Abs(ComputePlanarLoopArea(selectedLoop, selectedPlane.Origin, selectedXAxis, selectedYAxis));

                foreach (Solid solid in ExtractSolids(sourceElement))
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace planarFace)
                            continue;

                        List<OpeningProfile> faceProfiles = GetOpeningProfilesFromFace(planarFace);
                        if (faceProfiles.Count == 0)
                            continue;

                        IList<CurveLoop> faceLoops = planarFace.GetEdgesAsCurveLoops();
                        if (faceLoops == null || faceLoops.Count <= 1)
                            continue;

                        XYZ xAxis = planarFace.XVector.Normalize();
                        XYZ yAxis = planarFace.YVector.Normalize();
                        List<(CurveLoop Loop, double Area)> orderedLoops = faceLoops
                            .Select(loop => (Loop: loop, Area: Math.Abs(ComputePlanarLoopArea(loop, planarFace.Origin, xAxis, yAxis))))
                            .OrderByDescending(x => x.Area)
                            .ToList();

                        for (int i = 1; i < orderedLoops.Count; i++)
                        {
                            CurveLoop innerLoop = orderedLoops[i].Loop;
                            XYZ innerCenter = GetLoopCenter(innerLoop);
                            double innerArea = orderedLoops[i].Area;
                            XYZ innerNormal = planarFace.FaceNormal.Normalize();

                            if (innerCenter.DistanceTo(selectedCenter) > 0.01)
                                continue;

                            if (Math.Abs(innerNormal.DotProduct(selectedNormal)) < 0.99)
                                continue;

                            if (!AreasAreComparable(innerArea, selectedArea))
                                continue;

                            return faceProfiles.First();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TryCreateFaceCapProfileFromEdgeLoop Fehler: {ex.Message}");
            }

            return null;
        }

        private List<OpeningProfile> GetOpeningProfilesFromFace(Face face)
        {
            List<OpeningProfile> result = new List<OpeningProfile>();

            if (face is not PlanarFace planarFace)
                return result;

            IList<CurveLoop> loops = planarFace.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count == 0)
                return result;

            XYZ xAxis = planarFace.XVector.Normalize();
            XYZ yAxis = planarFace.YVector.Normalize();

            List<(CurveLoop Loop, double Area)> areas = loops
                .Select(loop => (Loop: loop, Area: Math.Abs(ComputePlanarLoopArea(loop, planarFace.Origin, xAxis, yAxis))))
                .OrderByDescending(x => x.Area)
                .ToList();

            if (areas.Count <= 1)
                return result;

            CurveLoop outerLoop = areas[0].Loop;
            result.Add(new OpeningProfile
            {
                Loop = outerLoop,
                Center = GetLoopCenter(outerLoop),
                Normal = planarFace.FaceNormal.Normalize(),
                Kind = OpeningSelectionKind.FaceCapLoop,
                Area = areas[0].Area
            });

            return result;
        }

        private double ComputePlanarLoopArea(CurveLoop loop, XYZ planeOrigin, XYZ xAxis, XYZ yAxis)
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

        private XYZ GetPlaneXAxis(XYZ normal)
        {
            XYZ candidate = Math.Abs(normal.DotProduct(XYZ.BasisZ)) < 0.99 ? XYZ.BasisZ : XYZ.BasisX;
            return normal.CrossProduct(candidate).Normalize();
        }

        private CurveLoop? TryFormClosedLoop(List<Curve> curves)
        {
            if (curves.Count < 1) return null;

            try
            {
                List<Curve> ordered = OrderCurves(curves);
                if (ordered.Count == 0) return null;

                XYZ firstStart = ordered[0].GetEndPoint(0);
                XYZ lastEnd = ordered[ordered.Count - 1].GetEndPoint(1);

                if (firstStart.DistanceTo(lastEnd) < 0.01)
                {
                    CurveLoop loop = new CurveLoop();
                    foreach (Curve c in ordered)
                        loop.Append(c);
                    return loop;
                }
            }
            catch { }

            return null;
        }

        private List<Curve> OrderCurves(List<Curve> curves)
        {
            if (curves.Count == 0) return new List<Curve>();

            List<Curve> remaining = new List<Curve>(curves);
            List<Curve> ordered = new List<Curve> { remaining[0] };
            remaining.RemoveAt(0);

            const double tol = 0.01;

            while (remaining.Count > 0)
            {
                XYZ end = ordered[ordered.Count - 1].GetEndPoint(1);
                bool found = false;

                for (int i = 0; i < remaining.Count; i++)
                {
                    Curve c = remaining[i];
                    if (end.DistanceTo(c.GetEndPoint(0)) < tol)
                    {
                        ordered.Add(c);
                        remaining.RemoveAt(i);
                        found = true;
                        break;
                    }
                    else if (end.DistanceTo(c.GetEndPoint(1)) < tol)
                    {
                        ordered.Add(c.CreateReversed());
                        remaining.RemoveAt(i);
                        found = true;
                        break;
                    }
                }
                if (!found) break;
            }

            return ordered;
        }

        #endregion

        #region ===== HILFSMETHODEN =====

        private Element? SelectElement(UIDocument uidoc, Document doc)
        {
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 1)
            {
                Element element = doc.GetElement(selectedIds.First());
                if (element.get_Geometry(new Options()) != null)
                    return element;
            }

            try
            {
                Reference reference = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    "Wählen Sie das 3D-Element");
                return doc.GetElement(reference);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        private List<Solid> ExtractSolids(Element element)
        {
            List<Solid> solids = new List<Solid>();
            Options options = new Options { DetailLevel = ViewDetailLevel.Fine };
            GeometryElement? geomElement = element.get_Geometry(options);

            if (geomElement == null) return solids;

            foreach (GeometryObject geomObj in geomElement)
            {
                CollectSolids(geomObj, solids);
            }

            return solids;
        }

        private void CollectSolids(GeometryObject geomObj, List<Solid> solids)
        {
            if (geomObj is Solid solid && solid.Volume > 0)
            {
                solids.Add(solid);
            }
            else if (geomObj is GeometryInstance instance)
            {
                GeometryElement? instanceGeom = instance.GetInstanceGeometry();
                if (instanceGeom != null)
                {
                    foreach (GeometryObject instObj in instanceGeom)
                    {
                        CollectSolids(instObj, solids);
                    }
                }
            }
        }

        private class SelectedCapFace
        {
            public required CurveLoop OuterLoop { get; init; }
            public required List<CurveLoop> InnerLoops { get; init; }
            public required List<string> OuterLoopEdgeSignatures { get; init; }
            public XYZ? PickPoint { get; init; }
            public required XYZ Center { get; init; }
            public required XYZ Normal { get; init; }
            public double Area { get; init; }
        }

        private class RebuildResult
        {
            public Solid? Solid { get; init; }
            public int ClosedCaps { get; init; }
            public int RemovedInnerLoopCount { get; init; }
            public string MatchDiagnostics { get; init; } = string.Empty;
            public string DebugLog { get; init; } = string.Empty;
            public string ErrorMessage { get; init; } = string.Empty;
        }

        private class FaceBuildInfo
        {
            public int Id { get; init; }
            public required Face Face { get; init; }
            public PlanarFace? PlanarFace { get; init; }
            public required XYZ Center { get; init; }
            public required XYZ Normal { get; init; }
            public double Area { get; init; }
            public required List<string> EdgeSignatures { get; init; }
            public required List<string> InnerLoopEdgeSignatures { get; init; }
            public CurveLoop? OuterLoop { get; init; }
        }

        private class FaceMatchCandidate
        {
            public required FaceBuildInfo Face { get; init; }
            public double PickDistance { get; init; }
            public int MatchingEdges { get; init; }
            public double CenterDistance { get; init; }
            public double NormalDotAbs { get; init; }
            public bool AreaComparable { get; init; }
        }

        private List<SelectedCapFace> SelectCapFaces(UIDocument uidoc, Document doc, ElementId elementId)
        {
            List<SelectedCapFace> caps = new List<SelectedCapFace>();

            while (true)
            {
                try
                {
                    Reference pickedRef = uidoc.Selection.PickObject(
                        ObjectType.PointOnElement,
                        new OpeningSelectionFilter(elementId),
                        $"Stirnfläche auswählen ({caps.Count} gewählt). ESC zum Beenden.");

                    Element element = doc.GetElement(pickedRef);
                    if (element == null)
                        continue;

                    GeometryObject geomObj = element.GetGeometryObjectFromReference(pickedRef);
                    if (geomObj is not Face face)
                    {
                        TaskDialog.Show("Bodenverdrängung", "Bitte eine Fläche auswählen.");
                        continue;
                    }

                    SelectedCapFace? cap = TryCreateSelectedCapFace(face, pickedRef.GlobalPoint);
                    if (cap == null)
                    {
                        TaskDialog.Show("Bodenverdrängung",
                            "Die ausgewählte Fläche ist nicht nutzbar.\n\n" +
                            "Bitte wählen Sie eine planare Stirnfläche eines Hohlkörpers.");
                        continue;
                    }

                    caps.Add(cap);
                    TaskDialog.Show("Bodenverdrängung",
                        $"Stirnfläche übernommen.\n\n" +
                        $"Ausgewählte Stirnflächen: {caps.Count}\n" +
                        $"Innere Loops auf dieser Fläche: {cap.InnerLoops.Count}");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }
            }

            return caps;
        }

        private SelectedCapFace? TryCreateSelectedCapFace(Face face, XYZ? pickPoint)
        {
            if (face is not PlanarFace planarFace)
                return null;

            IList<CurveLoop> loops = planarFace.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count == 0)
                return null;

            XYZ xAxis = planarFace.XVector.Normalize();
            XYZ yAxis = planarFace.YVector.Normalize();

            List<(CurveLoop Loop, double Area)> orderedLoops = loops
                .Select(loop => (Loop: loop, Area: Math.Abs(ComputePlanarLoopArea(loop, planarFace.Origin, xAxis, yAxis))))
                .OrderByDescending(x => x.Area)
                .ToList();

            if (orderedLoops.Count == 0)
                return null;

            return new SelectedCapFace
            {
                OuterLoop = orderedLoops[0].Loop,
                InnerLoops = orderedLoops.Skip(1).Select(x => x.Loop).ToList(),
                OuterLoopEdgeSignatures = GetCurveLoopEdgeSignatures(orderedLoops[0].Loop),
                PickPoint = pickPoint,
                Center = GetLoopCenter(orderedLoops[0].Loop),
                Normal = planarFace.FaceNormal.Normalize(),
                Area = orderedLoops[0].Area
            };
        }

        private RebuildResult? RebuildSolidWithTessellatedShapeBuilder(Element sourceElement, List<SelectedCapFace> selectedCaps)
        {
            List<string> debugLines = new List<string>();
            List<string> matchDiagnostics = new List<string>();
            try
            {
                debugLines.Add($"Timestamp: {DateTime.Now:O}");
                debugLines.Add($"SourceElementId: {sourceElement.Id}");
                debugLines.Add($"SourceElementName: {sourceElement.Name}");
                debugLines.Add($"SelectedCaps: {selectedCaps.Count}");
                debugLines.Add(string.Empty);

                for (int capIndex = 0; capIndex < selectedCaps.Count; capIndex++)
                    debugLines.Add(DescribeSelectedCap(selectedCaps[capIndex], capIndex));

                List<Solid> solids = ExtractSolids(sourceElement);
                if (solids.Count == 0)
                {
                    debugLines.Add("FEHLER: ExtractSolids lieferte 0 Solids.");
                    return new RebuildResult
                    {
                        DebugLog = string.Join(Environment.NewLine, debugLines),
                        ErrorMessage = "Keine gültigen Solid-Geometrien aus dem Quellobjekt extrahiert."
                    };
                }

                debugLines.Add($"ExtractedSolids: {solids.Count}");
                debugLines.Add($"OriginalSolidVolumeSum: {solids.Sum(s => s.Volume):F6}");

                List<FaceBuildInfo> faces = BuildFaceInfos(solids);
                debugLines.Add($"PlanarCandidateFaces: {faces.Count(f => f.PlanarFace != null && f.OuterLoop != null)}");
                debugLines.Add(string.Empty);

                foreach (FaceBuildInfo face in faces.Where(f => f.PlanarFace != null && f.OuterLoop != null))
                    debugLines.Add(DescribeFaceBuildInfo(face));

                Dictionary<int, SelectedCapFace> matchedCaps = new Dictionary<int, SelectedCapFace>();
                for (int capIndex = 0; capIndex < selectedCaps.Count; capIndex++)
                {
                    SelectedCapFace cap = selectedCaps[capIndex];
                    List<FaceMatchCandidate> candidates = GetFaceMatchCandidates(faces, cap);
                    debugLines.Add(string.Empty);
                    debugLines.Add(DescribeCapCandidates(cap, capIndex, candidates));

                    FaceBuildInfo? match = FindMatchingFace(cap, candidates);
                    if (match != null)
                    {
                        matchedCaps[match.Id] = cap;
                        matchDiagnostics.Add($"Cap {capIndex + 1}: MATCH faceId={match.Id}");
                        debugLines.Add($"Cap {capIndex + 1} matched faceId={match.Id}");
                    }
                    else
                    {
                        matchDiagnostics.Add(DescribeCapMatchFailure(faces, cap, capIndex));
                        debugLines.Add($"Cap {capIndex + 1} produced NO MATCH");
                    }
                }

                HashSet<int> selectedFaceIds = matchedCaps.Keys.ToHashSet();
                HashSet<int> facesToSkip = CollectFacesToSkip(faces, selectedFaceIds);
                debugLines.Add(string.Empty);
                debugLines.Add($"MatchedFaceIds: [{string.Join(", ", selectedFaceIds.OrderBy(x => x))}]");
                debugLines.Add($"FacesToSkip: [{string.Join(", ", facesToSkip.OrderBy(x => x))}]");

                TessellatedShapeBuilder tsb = new TessellatedShapeBuilder
                {
                    Target = TessellatedShapeBuilderTarget.Solid,
                    Fallback = TessellatedShapeBuilderFallback.Abort,
                    GraphicsStyleId = ElementId.InvalidElementId
                };
                tsb.OpenConnectedFaceSet(true);

                int originalFaceTriangleCount = 0;
                int originalFaceCountAdded = 0;

                foreach (FaceBuildInfo faceInfo in faces)
                {
                    if (facesToSkip.Contains(faceInfo.Id))
                        continue;

                    originalFaceTriangleCount += faceInfo.Face.Triangulate().NumTriangles;
                    originalFaceCountAdded++;
                    AddTriangulatedFace(tsb, faceInfo.Face);
                }

                int capTriangleCount = 0;
                int capCountAdded = 0;

                foreach ((int _, SelectedCapFace cap) in matchedCaps)
                {
                    List<XYZ[]> capTriangles = TriangulatePlanarPolygon(SampleCurveLoop(cap.OuterLoop), cap.Normal);
                    capTriangleCount += capTriangles.Count;
                    capCountAdded++;
                    AddCapTriangles(tsb, cap);
                }

                debugLines.Add($"TSB: OriginalFacesAdded={originalFaceCountAdded}");
                debugLines.Add($"TSB: OriginalFaceTriangles={originalFaceTriangleCount}");
                debugLines.Add($"TSB: CapsAdded={capCountAdded}");
                debugLines.Add($"TSB: CapTriangles={capTriangleCount}");

                tsb.CloseConnectedFaceSet();
                debugLines.Add("TSB: ConnectedFaceSet geschlossen.");
                tsb.Build();
                debugLines.Add("TSB: Build() erfolgreich ausgeführt.");

                TessellatedShapeBuilderResult result = tsb.GetBuildResult();
                int geomObjectCount = result.GetGeometricalObjects().Count;
                debugLines.Add($"TSB: GeometricalObjects={geomObjectCount}");
                Solid? rebuiltSolid = result.GetGeometricalObjects().OfType<Solid>().FirstOrDefault(s => s.Volume > 0);
                if (rebuiltSolid == null)
                {
                    debugLines.Add("FEHLER: BuildResult enthält keinen Solid mit Volumen > 0.");

                    RebuildResult? fallbackResult = TryRebuildSolidWithBooleanFallback(
                        sourceElement,
                        selectedCaps,
                        matchedCaps.Count,
                        matchDiagnostics,
                        debugLines,
                        "TSB BuildResult enthielt keinen Solid mit Volumen > 0.");

                    if (fallbackResult != null)
                        return fallbackResult;

                    return new RebuildResult
                    {
                        ClosedCaps = matchedCaps.Count,
                        RemovedInnerLoopCount = matchedCaps.Values.Sum(c => c.InnerLoops.Count),
                        MatchDiagnostics = string.Join("\n", matchDiagnostics),
                        DebugLog = string.Join(Environment.NewLine, debugLines),
                        ErrorMessage = "TessellatedShapeBuilder hat keinen volumetrischen Solid zurückgegeben."
                    };
                }

                debugLines.Add($"RebuiltSolidVolume: {rebuiltSolid.Volume:F6}");
                debugLines.Add($"ClosedCaps: {matchedCaps.Count}");
                debugLines.Add($"RemovedInnerLoopCount: {matchedCaps.Values.Sum(c => c.InnerLoops.Count)}");

                return new RebuildResult
                {
                    Solid = rebuiltSolid,
                    ClosedCaps = matchedCaps.Count,
                    RemovedInnerLoopCount = matchedCaps.Values.Sum(c => c.InnerLoops.Count),
                    MatchDiagnostics = string.Join("\n", matchDiagnostics),
                    DebugLog = string.Join(Environment.NewLine, debugLines)
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RebuildSolidWithTessellatedShapeBuilder Fehler: {ex.Message}");
                debugLines.Add("FEHLER: Ausnahme während RebuildSolidWithTessellatedShapeBuilder.");
                debugLines.Add(ex.ToString());

                RebuildResult? fallbackResult = TryRebuildSolidWithBooleanFallback(
                    sourceElement,
                    selectedCaps,
                    selectedCaps.Count,
                    matchDiagnostics,
                    debugLines,
                    $"TSB-Ausnahme: {ex.Message}");

                if (fallbackResult != null)
                    return fallbackResult;

                return new RebuildResult
                {
                    DebugLog = string.Join(Environment.NewLine, debugLines),
                    ErrorMessage = $"Fehler beim Neuaufbau des Vollkörpers: {ex.Message}"
                };
            }
        }

        private RebuildResult? TryRebuildSolidWithBooleanFallback(
            Element sourceElement,
            List<SelectedCapFace> selectedCaps,
            int closedCaps,
            List<string> matchDiagnostics,
            List<string> debugLines,
            string reason)
        {
            try
            {
                List<string> fallbackLog = new List<string>(debugLines)
                {
                    string.Empty,
                    $"Fallback: Starte Boolean-Union. Grund: {reason}"
                };

                List<Solid> originalSolids = ExtractSolids(sourceElement);
                fallbackLog.Add($"Fallback: OriginalSolids={originalSolids.Count}");
                Solid? referenceSolid = CombineSolids(originalSolids);
                if (referenceSolid == null || referenceSolid.Volume <= 0)
                {
                    fallbackLog.Add("Fallback: Konnte kein Referenz-Solid aus dem Quellobjekt bilden.");
                    debugLines.Clear();
                    debugLines.AddRange(fallbackLog);
                    return null;
                }

                List<OpeningProfile> openingProfiles = selectedCaps
                    .Select(cap => new OpeningProfile
                    {
                        Loop = cap.OuterLoop,
                        Center = cap.Center,
                        Normal = cap.Normal,
                        Kind = OpeningSelectionKind.FaceCapLoop,
                        Area = cap.Area
                    })
                    .ToList();

                if (selectedCaps.Count == 1)
                {
                    SelectedCapFace singleCap = selectedCaps[0];
                    foreach (CurveLoop innerLoop in singleCap.InnerLoops)
                    {
                        openingProfiles.Add(new OpeningProfile
                        {
                            Loop = innerLoop,
                            Center = GetLoopCenter(innerLoop),
                            Normal = singleCap.Normal,
                            Kind = OpeningSelectionKind.EdgeLoop,
                            Area = 0.0
                        });
                    }

                    fallbackLog.Add($"Fallback: AddedInnerLoopProfiles={singleCap.InnerLoops.Count}");
                }

                List<FillCandidate> candidates = CreateFillCandidates(openingProfiles, referenceSolid);
                fallbackLog.Add($"Fallback: FillCandidates={candidates.Count}");
                fallbackLog.Add($"Fallback: CandidateClosedOpenings={candidates.Sum(c => c.ClosedOpenings)}");

                if (candidates.Count == 0)
                {
                    fallbackLog.Add("Fallback: Keine Füllkandidaten erzeugt.");
                    debugLines.Clear();
                    debugLines.AddRange(fallbackLog);
                    return null;
                }

                List<Solid> unionInputs = new List<Solid>(originalSolids);
                foreach (FillCandidate candidate in candidates)
                {
                    fallbackLog.Add($"Fallback: CandidateVolume={candidate.Solid.Volume:F6}, ClosedOpenings={candidate.ClosedOpenings}");
                    unionInputs.Add(candidate.Solid);
                }

                Solid? combinedSolid = CombineSolids(unionInputs);
                if (combinedSolid == null || combinedSolid.Volume <= 0)
                {
                    fallbackLog.Add("Fallback: CombineSolids lieferte kein gültiges Solid.");
                    debugLines.Clear();
                    debugLines.AddRange(fallbackLog);
                    return null;
                }

                double volumeIncrease = combinedSolid.Volume - referenceSolid.Volume;
                fallbackLog.Add($"Fallback: ReferenceVolume={referenceSolid.Volume:F6}");
                fallbackLog.Add($"Fallback: ResultVolume={combinedSolid.Volume:F6}");
                fallbackLog.Add($"Fallback: VolumeIncrease={volumeIncrease:F6}");

                if (volumeIncrease <= 1e-6)
                {
                    fallbackLog.Add("Fallback: Kein relevanter Volumenzuwachs erkannt.");
                    debugLines.Clear();
                    debugLines.AddRange(fallbackLog);
                    return null;
                }

                fallbackLog.Add("Fallback: Boolean-Union erfolgreich.");
                return new RebuildResult
                {
                    Solid = combinedSolid,
                    ClosedCaps = Math.Max(closedCaps, candidates.Sum(c => c.ClosedOpenings)),
                    RemovedInnerLoopCount = selectedCaps.Sum(c => c.InnerLoops.Count),
                    MatchDiagnostics = string.Join("\n", matchDiagnostics),
                    DebugLog = string.Join(Environment.NewLine, fallbackLog)
                };
            }
            catch (Exception fallbackEx)
            {
                debugLines.Add(string.Empty);
                debugLines.Add("Fallback: Boolean-Union ebenfalls fehlgeschlagen.");
                debugLines.Add(fallbackEx.ToString());
                return null;
            }
        }

        private List<FaceBuildInfo> BuildFaceInfos(List<Solid> solids)
        {
            List<FaceBuildInfo> infos = new List<FaceBuildInfo>();
            int faceId = 0;

            foreach (Solid solid in solids)
            {
                foreach (Face face in solid.Faces)
                {
                    List<string> edgeSignatures = GetFaceEdgeSignatures(face);
                    List<string> innerLoopEdgeSignatures = new List<string>();
                    CurveLoop? outerLoop = null;
                    XYZ center = XYZ.Zero;
                    XYZ normal = XYZ.BasisZ;
                    double area = Math.Abs(face.Area);
                    PlanarFace? planarFace = face as PlanarFace;

                    if (planarFace != null)
                    {
                        IList<CurveLoop> loops = planarFace.GetEdgesAsCurveLoops();
                        XYZ xAxis = planarFace.XVector.Normalize();
                        XYZ yAxis = planarFace.YVector.Normalize();

                        List<(CurveLoop Loop, double Area)> orderedLoops = loops
                            .Select(loop => (Loop: loop, Area: Math.Abs(ComputePlanarLoopArea(loop, planarFace.Origin, xAxis, yAxis))))
                            .OrderByDescending(x => x.Area)
                            .ToList();

                        if (orderedLoops.Count > 0)
                        {
                            outerLoop = orderedLoops[0].Loop;
                            center = GetLoopCenter(outerLoop);
                            area = orderedLoops[0].Area;
                        }

                        for (int i = 1; i < orderedLoops.Count; i++)
                        {
                            foreach (string signature in GetCurveLoopEdgeSignatures(orderedLoops[i].Loop))
                                innerLoopEdgeSignatures.Add(signature);
                        }

                        normal = planarFace.FaceNormal.Normalize();
                    }
                    else
                    {
                        BoundingBoxUV bbox = face.GetBoundingBox();
                        UV uv = (bbox.Min + bbox.Max) / 2.0;
                        center = face.Evaluate(uv);
                        normal = face.ComputeNormal(uv).Normalize();
                    }

                    infos.Add(new FaceBuildInfo
                    {
                        Id = faceId++,
                        Face = face,
                        PlanarFace = planarFace,
                        Center = center,
                        Normal = normal,
                        Area = area,
                        EdgeSignatures = edgeSignatures,
                        InnerLoopEdgeSignatures = innerLoopEdgeSignatures,
                        OuterLoop = outerLoop
                    });
                }
            }

            return infos;
        }

        private FaceBuildInfo? FindMatchingFace(SelectedCapFace cap, List<FaceMatchCandidate> candidates)
        {
            int requiredOuterEdgeMatches = cap.OuterLoopEdgeSignatures.Count;

            return candidates
                .Where(x => x.NormalDotAbs > 0.99)
                .Where(x => x.PickDistance < 0.05 || x.MatchingEdges > 0 || x.CenterDistance < 0.5)
                .Where(x => x.AreaComparable ||
                            (x.PickDistance < 0.01 && x.CenterDistance < 0.01 && x.MatchingEdges >= requiredOuterEdgeMatches))
                .Select(x => x.Face)
                .FirstOrDefault();
        }

        private string DescribeCapMatchFailure(List<FaceBuildInfo> faces, SelectedCapFace cap, int capIndex)
        {
            List<FaceMatchCandidate> top = GetFaceMatchCandidates(faces, cap).Take(3).ToList();
            if (top.Count == 0)
                return $"Cap {capIndex + 1}: NO planar candidates";

            return $"Cap {capIndex + 1}: NO MATCH | " + string.Join(" || ", top.Select(c =>
                $"faceId={c.Face.Id}, pick={c.PickDistance:F4}, edges={c.MatchingEdges}, center={c.CenterDistance:F4}, dot={c.NormalDotAbs:F4}, areaOk={c.AreaComparable}"));
        }

        private List<FaceMatchCandidate> GetFaceMatchCandidates(List<FaceBuildInfo> faces, SelectedCapFace cap)
        {
            HashSet<string> selectedEdges = cap.OuterLoopEdgeSignatures.ToHashSet(StringComparer.Ordinal);

            return faces
                .Where(f => f.PlanarFace != null && f.OuterLoop != null)
                .Select(f => new FaceMatchCandidate
                {
                    Face = f,
                    PickDistance = GetPointToFaceDistance(cap.PickPoint, f.Face),
                    MatchingEdges = f.EdgeSignatures.Count(sig => selectedEdges.Contains(sig)),
                    CenterDistance = f.Center.DistanceTo(cap.Center),
                    NormalDotAbs = Math.Abs(f.Normal.DotProduct(cap.Normal)),
                    AreaComparable = AreasAreComparable(f.Area, cap.Area)
                })
                .OrderByDescending(x => x.MatchingEdges)
                .ThenBy(x => x.PickDistance)
                .ThenBy(x => x.CenterDistance)
                .ToList();
        }

        private double GetPointToFaceDistance(XYZ? point, Face face)
        {
            if (point == null)
                return double.MaxValue;

            try
            {
                IntersectionResult? projection = face.Project(point);
                if (projection == null)
                    return double.MaxValue;

                return projection.Distance;
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private string DescribeSelectedCap(SelectedCapFace cap, int capIndex)
        {
            return string.Join(Environment.NewLine, new[]
            {
                $"Cap {capIndex + 1}:",
                $"  PickPoint: {FormatPoint(cap.PickPoint)}",
                $"  Center: {FormatPoint(cap.Center)}",
                $"  Normal: {FormatPoint(cap.Normal)}",
                $"  Area: {cap.Area:F6}",
                $"  OuterEdgeCount: {cap.OuterLoopEdgeSignatures.Count}",
                $"  InnerLoopCount: {cap.InnerLoops.Count}",
                $"  OuterEdgeSignatures: {string.Join(" ; ", cap.OuterLoopEdgeSignatures)}"
            });
        }

        private string DescribeFaceBuildInfo(FaceBuildInfo face)
        {
            return string.Join(Environment.NewLine, new[]
            {
                $"Face {face.Id}:",
                $"  Center: {FormatPoint(face.Center)}",
                $"  Normal: {FormatPoint(face.Normal)}",
                $"  Area: {face.Area:F6}",
                $"  OuterEdgeCount: {(face.OuterLoop != null ? GetCurveLoopEdgeSignatures(face.OuterLoop).Count : 0)}",
                $"  TotalEdgeCount: {face.EdgeSignatures.Count}",
                $"  InnerEdgeCount: {face.InnerLoopEdgeSignatures.Count}",
                $"  EdgeSignatures: {string.Join(" ; ", face.EdgeSignatures)}"
            });
        }

        private string DescribeCapCandidates(SelectedCapFace cap, int capIndex, List<FaceMatchCandidate> candidates)
        {
            List<string> lines = new List<string>
            {
                $"Cap {capIndex + 1} candidates:",
                $"  SelectedCenter: {FormatPoint(cap.Center)}",
                $"  SelectedNormal: {FormatPoint(cap.Normal)}",
                $"  SelectedArea: {cap.Area:F6}",
                $"  SelectedEdges: {string.Join(" ; ", cap.OuterLoopEdgeSignatures)}"
            };

            foreach (FaceMatchCandidate candidate in candidates.Take(5))
            {
                lines.Add($"  Candidate faceId={candidate.Face.Id}: pick={candidate.PickDistance:F6}, center={candidate.CenterDistance:F6}, dot={candidate.NormalDotAbs:F6}, areaOk={candidate.AreaComparable}, matchingEdges={candidate.MatchingEdges}, faceArea={candidate.Face.Area:F6}");
                lines.Add($"    FaceCenter: {FormatPoint(candidate.Face.Center)}");
                lines.Add($"    FaceNormal: {FormatPoint(candidate.Face.Normal)}");
                lines.Add($"    FaceEdges: {string.Join(" ; ", candidate.Face.EdgeSignatures)}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private string FormatPoint(XYZ? point)
        {
            if (point == null)
                return "<null>";

            return $"({point.X:F6}, {point.Y:F6}, {point.Z:F6})";
        }

        private string WriteDebugLogFile(string debugLog, string? familyName)
        {
            string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "BIMassist_DebugLogs");
            Directory.CreateDirectory(logDirectory);

            string safeName = string.IsNullOrWhiteSpace(familyName) ? "Bodenverdrängung" : familyName;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(invalid, '_');

            string logPath = Path.Combine(logDirectory, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.WriteAllText(logPath, debugLog ?? string.Empty);
            return logPath;
        }

        private HashSet<int> CollectFacesToSkip(List<FaceBuildInfo> faces, HashSet<int> selectedFaceIds)
        {
            Dictionary<string, List<int>> edgeToFaces = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (FaceBuildInfo face in faces)
            {
                foreach (string edgeSignature in face.EdgeSignatures)
                {
                    if (!edgeToFaces.TryGetValue(edgeSignature, out List<int>? list))
                    {
                        list = new List<int>();
                        edgeToFaces[edgeSignature] = list;
                    }

                    list.Add(face.Id);
                }
            }

            HashSet<int> skip = new HashSet<int>(selectedFaceIds);
            Queue<int> queue = new Queue<int>();

            foreach (FaceBuildInfo selectedFace in faces.Where(f => selectedFaceIds.Contains(f.Id)))
            {
                foreach (string innerEdge in selectedFace.InnerLoopEdgeSignatures)
                {
                    if (!edgeToFaces.TryGetValue(innerEdge, out List<int>? neighbors))
                        continue;

                    foreach (int neighbor in neighbors)
                    {
                        if (!selectedFaceIds.Contains(neighbor))
                            queue.Enqueue(neighbor);
                    }
                }
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                if (skip.Contains(current))
                    continue;

                skip.Add(current);

                FaceBuildInfo face = faces.First(f => f.Id == current);
                foreach (string edge in face.EdgeSignatures)
                {
                    if (!edgeToFaces.TryGetValue(edge, out List<int>? neighbors))
                        continue;

                    foreach (int neighbor in neighbors)
                    {
                        if (!skip.Contains(neighbor) && !selectedFaceIds.Contains(neighbor))
                            queue.Enqueue(neighbor);
                    }
                }
            }

            return skip;
        }

        private List<string> GetFaceEdgeSignatures(Face face)
        {
            List<string> signatures = new List<string>();
            foreach (EdgeArray edgeArray in face.EdgeLoops)
            {
                foreach (Edge edge in edgeArray)
                    signatures.Add(GetCurveSignature(edge.AsCurve()));
            }

            return signatures;
        }

        private List<string> GetCurveLoopEdgeSignatures(CurveLoop loop)
        {
            List<string> signatures = new List<string>();
            foreach (Curve curve in loop)
                signatures.Add(GetCurveSignature(curve));
            return signatures;
        }

        private string GetCurveSignature(Curve curve)
        {
            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);

            string a = FormatPointKey(p0);
            string b = FormatPointKey(p1);
            return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
        }

        private string FormatPointKey(XYZ point)
        {
            return $"{Math.Round(point.X, 6):F6},{Math.Round(point.Y, 6):F6},{Math.Round(point.Z, 6):F6}";
        }

        private void AddTriangulatedFace(TessellatedShapeBuilder tsb, Face face)
        {
            Mesh mesh = face.Triangulate();
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                AddTriangle(tsb,
                    triangle.get_Vertex(0),
                    triangle.get_Vertex(1),
                    triangle.get_Vertex(2),
                    ElementId.InvalidElementId);
            }
        }

        private void AddCapTriangles(TessellatedShapeBuilder tsb, SelectedCapFace cap)
        {
            List<XYZ> points = SampleCurveLoop(cap.OuterLoop);
            foreach (XYZ[] tri in TriangulatePlanarPolygon(points, cap.Normal))
            {
                AddTriangle(tsb, tri[0], tri[1], tri[2], ElementId.InvalidElementId, cap.Normal);
            }
        }

        private List<XYZ> SampleCurveLoop(CurveLoop loop)
        {
            List<XYZ> points = new List<XYZ>();
            const double tol = 1e-6;

            foreach (Curve curve in loop)
            {
                IList<XYZ> tess = curve.Tessellate();
                for (int i = 0; i < tess.Count; i++)
                {
                    if (points.Count > 0 && points.Last().DistanceTo(tess[i]) < tol)
                        continue;
                    points.Add(tess[i]);
                }
            }

            if (points.Count > 1 && points[0].DistanceTo(points[^1]) < tol)
                points.RemoveAt(points.Count - 1);

            return points;
        }

        private List<XYZ[]> TriangulatePlanarPolygon(List<XYZ> points3D, XYZ normal)
        {
            List<XYZ[]> triangles = new List<XYZ[]>();
            if (points3D.Count < 3)
                return triangles;

            XYZ origin = points3D[0];
            XYZ xAxis = GetPlaneXAxis(normal);
            XYZ yAxis = normal.CrossProduct(xAxis).Normalize();

            List<(double X, double Y)> pts2D = points3D
                .Select(p =>
                {
                    XYZ rel = p - origin;
                    return (rel.DotProduct(xAxis), rel.DotProduct(yAxis));
                })
                .ToList();

            SimplifyPlanarPolygon(points3D, pts2D);
            if (points3D.Count < 3)
                return triangles;

            List<int> indices = Enumerable.Range(0, points3D.Count).ToList();
            double polygonArea = SignedArea2D(pts2D, indices);
            if (Math.Abs(polygonArea) < 1e-9)
                return triangles;

            if (polygonArea < 0)
                indices.Reverse();

            int guard = 0;
            while (indices.Count > 2 && guard < 10000)
            {
                guard++;
                bool earFound = false;

                for (int i = 0; i < indices.Count; i++)
                {
                    int prev = indices[(i - 1 + indices.Count) % indices.Count];
                    int curr = indices[i];
                    int next = indices[(i + 1) % indices.Count];

                    if (!IsEar(prev, curr, next, indices, pts2D))
                        continue;

                    triangles.Add(new[] { points3D[prev], points3D[curr], points3D[next] });
                    indices.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound)
                    break;
            }

            return triangles;
        }

        private void SimplifyPlanarPolygon(List<XYZ> points3D, List<(double X, double Y)> pts2D)
        {
            const double duplicateTol = 1e-6;
            const double collinearTol = 1e-9;

            for (int i = points3D.Count - 1; i > 0; i--)
            {
                if (points3D[i].DistanceTo(points3D[i - 1]) < duplicateTol)
                {
                    points3D.RemoveAt(i);
                    pts2D.RemoveAt(i);
                }
            }

            if (points3D.Count > 1 && points3D[0].DistanceTo(points3D[^1]) < duplicateTol)
            {
                points3D.RemoveAt(points3D.Count - 1);
                pts2D.RemoveAt(pts2D.Count - 1);
            }

            bool changed;
            do
            {
                changed = false;
                if (points3D.Count < 3)
                    break;

                for (int i = 0; i < points3D.Count; i++)
                {
                    int prev = (i - 1 + points3D.Count) % points3D.Count;
                    int next = (i + 1) % points3D.Count;
                    double cross = Cross(pts2D[prev], pts2D[i], pts2D[next]);

                    if (Math.Abs(cross) <= collinearTol)
                    {
                        points3D.RemoveAt(i);
                        pts2D.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            while (changed);
        }

        private double SignedArea2D(List<(double X, double Y)> pts, List<int> indices)
        {
            double area = 0;
            for (int i = 0; i < indices.Count; i++)
            {
                (double X, double Y) a = pts[indices[i]];
                (double X, double Y) b = pts[indices[(i + 1) % indices.Count]];
                area += (a.X * b.Y) - (b.X * a.Y);
            }

            return area * 0.5;
        }

        private bool IsEar(int prev, int curr, int next, List<int> polygon, List<(double X, double Y)> pts)
        {
            if (Cross(pts[prev], pts[curr], pts[next]) <= 1e-9)
                return false;

            for (int i = 0; i < polygon.Count; i++)
            {
                int idx = polygon[i];
                if (idx == prev || idx == curr || idx == next)
                    continue;

                if (PointInTriangle(pts[idx], pts[prev], pts[curr], pts[next]))
                    return false;
            }

            return true;
        }

        private double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private bool PointInTriangle((double X, double Y) p, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            double c1 = Cross(a, b, p);
            double c2 = Cross(b, c, p);
            double c3 = Cross(c, a, p);

            bool hasNeg = c1 < -1e-9 || c2 < -1e-9 || c3 < -1e-9;
            bool hasPos = c1 > 1e-9 || c2 > 1e-9 || c3 > 1e-9;
            return !(hasNeg && hasPos);
        }

        private void AddTriangle(TessellatedShapeBuilder tsb, XYZ a, XYZ b, XYZ c, ElementId materialId, XYZ? preferredNormal = null)
        {
            if (a.IsAlmostEqualTo(b) || b.IsAlmostEqualTo(c) || c.IsAlmostEqualTo(a))
                return;

            List<XYZ> verts = new List<XYZ> { a, b, c };
            if (preferredNormal != null)
            {
                XYZ triNormal = (b - a).CrossProduct(c - a);
                if (triNormal.GetLength() > 1e-9 && triNormal.Normalize().DotProduct(preferredNormal.Normalize()) < 0)
                    verts = new List<XYZ> { a, c, b };
            }

            TessellatedFace face = new TessellatedFace(verts, materialId);
            if (face.IsValidObject)
                tsb.AddFace(face);
        }

        #endregion
    }
}

/*
 * ================================================================================
 * FUNKTIONSWEISE - VOLUMENKÖRPER-ERSTELLUNG
 * ================================================================================
 * 
 * Dieser Befehl erstellt echte VOLUMENKÖRPER (Solids), keine Meshes!
 * 
 * ABLAUF:
 * 1. Das Original-Element wird ausgewählt
 * 2. Die Solids des Elements werden extrahiert
 * 3. Der Benutzer wählt Kanten die Öffnungen umranden
 * 4. Für jede Öffnung wird ein Füllkörper erstellt:
 *    - Der CurveLoop definiert das Profil
 *    - Das Profil wird ins Innere extrudiert
 *    - Die Extrusion wird mit dem Original vereinigt (Boolean Union)
 * 5. Das Ergebnis ist ein echtes Solid mit Volumen
 * 
 * WARUM ECHTE SOLIDS?
 * - Können in Revit für Berechnungen verwendet werden
 * - Haben ein messbares Volumen
 * - Können mit anderen Solids kombiniert werden
 * - Werden korrekt in Schnitten/Ansichten dargestellt
 * 
 * BEISPIEL BECHER:
 * - Original: Hohlkörper mit offenem Rand oben
 * - Öffnung auswählen: Obere Randkante
 * - Füllkörper: Zylinder der die Öffnung füllt
 * - Ergebnis: Massiver Körper (Becher + Füllung)
 * 
 * ================================================================================
 */
