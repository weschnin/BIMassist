using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;

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

                // ===== SCHRITT 3: Benutzer wählt Kanten an Öffnungen =====
                TaskDialog.Show("Bodenverdrängung - Schritt 2/2",
                    "Wählen Sie die KANTEN aus, die Öffnungen umranden.\n\n" +
                    "• Klicken Sie auf die Kanten am Rand der Öffnung\n" +
                    "• z.B. bei einem Becher: die obere Randkante\n" +
                    "• Bei geschlossenem Loop wird die Öffnung erkannt\n" +
                    "• Drücken Sie ESC wenn Sie fertig sind\n\n" +
                    "Für jede Öffnung wird ein Füllkörper erstellt und mit dem Original vereinigt.");

                List<CurveLoop> openingLoops = SelectOpeningEdges(uidoc, doc, sourceElementId);

                if (openingLoops.Count == 0)
                {
                    TaskDialogResult confirmResult = TaskDialog.Show("Bodenverdrängung",
                        "Keine Öffnungen ausgewählt.\n\n" +
                        "Soll nur das Original-Solid als Abzugskörper-Familie erstellt werden?",
                        TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

                    if (confirmResult != TaskDialogResult.Yes)
                        return Result.Cancelled;
                }

                // ===== SCHRITT 4: Solids vereinigen =====
                Solid? combinedSolid = CombineSolids(originalSolids);
                if (combinedSolid == null)
                {
                    TaskDialog.Show("Bodenverdrängung", "Konnte Original-Solids nicht vereinigen.");
                    return Result.Failed;
                }

                // ===== SCHRITT 5: Füllkörper für Öffnungen erstellen und vereinigen =====
                int filledOpenings = 0;
                foreach (CurveLoop loop in openingLoops)
                {
                    Solid? fillSolid = CreateFillSolid(loop, combinedSolid);
                    if (fillSolid != null && fillSolid.Volume > 0)
                    {
                        try
                        {
                            Solid? unionResult = BooleanOperationsUtils.ExecuteBooleanOperation(
                                combinedSolid, fillSolid, BooleanOperationsType.Union);

                            if (unionResult != null && unionResult.Volume > 0)
                            {
                                combinedSolid = unionResult;
                                filledOpenings++;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Boolean Union fehlgeschlagen: {ex.Message}");
                        }
                    }
                }

                double finalVolume = combinedSolid.Volume;

                // ===== SCHRITT 6: Abzugskörper-Familie erstellen =====
                // Das Solid behält seine Original-Koordinaten in der Familie.
                // Die Familie wird bei (0,0,0) platziert, sodass das Solid
                // exakt an seiner ursprünglichen Position erscheint.
                string familyName = $"Bodenverdrängung_{elementName}_{DateTime.Now:yyyyMMdd_HHmmss}";

                FamilyInstance? voidFamilyInstance = FamilyGeometryTools.CreateVoidFamily(
                    uiapp, doc, combinedSolid, familyName);

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
                    $"Geschlossene Öffnungen: {filledOpenings} von {openingLoops.Count}\n\n" +
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

        /// <summary>
        /// Erstellt einen Füllkörper für eine Öffnung.
        /// Der Körper wird durch den gesamten Hohlraum extrudiert (nicht nur bis zur nächsten Wand).
        /// Bei einem Rohr: Füllt den gesamten inneren Hohlraum → geschlossener Zylinder.
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
        /// Lässt den Benutzer Kanten auswählen die Öffnungen definieren.
        /// </summary>
        private List<CurveLoop> SelectOpeningEdges(UIDocument uidoc, Document doc, ElementId elementId)
        {
            List<CurveLoop> loops = new List<CurveLoop>();
            List<Curve> currentCurves = new List<Curve>();

            try
            {
                while (true)
                {
                    try
                    {
                        Reference edgeRef = uidoc.Selection.PickObject(
                            ObjectType.Edge,
                            new EdgeFilter(elementId),
                            $"Kante auswählen ({currentCurves.Count} Kanten, {loops.Count} Öffnungen). ESC zum Beenden.");

                        Element element = doc.GetElement(edgeRef);
                        if (element == null) continue;

                        GeometryObject geomObj = element.GetGeometryObjectFromReference(edgeRef);
                        if (geomObj is Edge edge)
                        {
                            Curve? curve = edge.AsCurve();
                            if (curve != null)
                            {
                                currentCurves.Add(curve);

                                // Versuche geschlossenen Loop zu bilden
                                CurveLoop? loop = TryFormClosedLoop(currentCurves);
                                if (loop != null)
                                {
                                    loops.Add(loop);
                                    currentCurves.Clear();

                                    TaskDialog.Show("Bodenverdrängung",
                                        $"Öffnung erkannt!\n\n" +
                                        $"Öffnungen gesamt: {loops.Count}\n\n" +
                                        "Wählen Sie weitere Kanten für eine andere Öffnung\n" +
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

            return loops;
        }

        private class EdgeFilter : ISelectionFilter
        {
            private readonly ElementId _elementId;
            public EdgeFilter(ElementId elementId) { _elementId = elementId; }
            public bool AllowElement(Element elem) => elem?.Id == _elementId;
            public bool AllowReference(Reference reference, XYZ position) => reference?.ElementId == _elementId;
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
