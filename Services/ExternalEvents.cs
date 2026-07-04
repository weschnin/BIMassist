using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using BIMassist.Core; // FamilyGeometryTools.CreateNewFamily
using Material = Autodesk.Revit.DB.Material;

namespace BIMassist.Services
{
    public class SaveBoreholeDataHandler : IExternalEventHandler
    {
        public List<Borehole> Boreholes { get; set; }
        public List<GeologyLayer> Layers { get; set; }
        public List<GroundModelBoundaryPoint> BoundaryPoints { get; set; }
        public BoreholeManagerSettings Settings { get; set; }
        public GroundModelAxis Axis { get; set; }

        public void Execute(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null) return;
            var storage = new BoreholeProjectStorage(uiDoc.Document);
            storage.Save(Boreholes ?? new List<Borehole>(), Layers ?? new List<GeologyLayer>(), BoundaryPoints ?? new List<GroundModelBoundaryPoint>(), Settings, Axis, true);
        }

        public string GetName() => "BIMassist.SaveBoreholeData";
    }

    public class CreateExtrusionsHandler : IExternalEventHandler
    {
        public List<Borehole> Boreholes { get; set; }
        public List<GeologyLayer> Layers { get; set; }
        public bool AssumeMeters { get; set; }
        public bool DepthIsBelowGround { get; set; }

        private const double FAR_THRESHOLD_M = 1000.0;
        public double BoreholeDiameterMeters { get; set; } = 0.30;
        public string MaterialNameColumn { get; set; } = "Description";
        public bool UseCodeAsLayerKey { get; set; } = false; // vom ViewModel setzen: true = Code, false = Beschreibung
        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc == null) { TaskDialog.Show("BIMassist", "Kein aktives Dokument."); return; }
                var doc = uiDoc.Document;
                if (Boreholes == null || Boreholes.Count == 0) { TaskDialog.Show("BIMassist", "Keine Bohrungen vorhanden."); return; }

                var pl = doc.ActiveProjectLocation;
                var pp = pl.GetProjectPosition(XYZ.Zero);

                double ppEW_m = UnitUtils.ConvertFromInternalUnits(pp.EastWest, UnitTypeId.Meters);
                double ppNS_m = UnitUtils.ConvertFromInternalUnits(pp.NorthSouth, UnitTypeId.Meters);
                double ppEL_m = UnitUtils.ConvertFromInternalUnits(pp.Elevation, UnitTypeId.Meters);

                bool projectHasCoords = Math.Abs(ppEW_m) > 0.01 || Math.Abs(ppNS_m) > 0.01;

                double cx = Boreholes.Average(b => b.Easting);
                double cy = Boreholes.Average(b => b.Northing);
                double cz = Boreholes.Average(b => b.GroundLevel);

                double maxDeltaToProject = projectHasCoords
                    ? Boreholes.Max(b => Math.Max(Math.Abs(b.Easting - ppEW_m), Math.Abs(b.Northing - ppNS_m)))
                    : double.MaxValue;

                bool farFromProject = !projectHasCoords || maxDeltaToProject > FAR_THRESHOLD_M;

                double anchorE_m = farFromProject ? cx : ppEW_m;
                double anchorN_m = farFromProject ? cy : ppNS_m;
                double anchorZ_m = farFromProject ? cz : ppEL_m;

                if (farFromProject)
                {
                    var td = new TaskDialog("BIMassist – Projektkoordinaten")
                    {
                        MainInstruction = "Projektkoordinaten setzen?",
                        MainContent = "CSV-Koordinaten liegen weit von der aktuellen Projektlage. " +
                                      "Soll die Projektlage auf den CSV-Schwerpunkt gesetzt werden?",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.Yes
                    };

                    if (td.Show() == TaskDialogResult.Yes)
                    {
                        using (var tx = new Transaction(doc, "AWESBox – Projektkoordinaten setzen"))
                        {
                            tx.Start();
                            var newPos = new ProjectPosition(
                                UnitUtils.ConvertToInternalUnits(anchorE_m, UnitTypeId.Meters),
                                UnitUtils.ConvertToInternalUnits(anchorN_m, UnitTypeId.Meters),
                                UnitUtils.ConvertToInternalUnits(anchorZ_m, UnitTypeId.Meters),
                                pp.Angle
                            );
                            pl.SetProjectPosition(XYZ.Zero, newPos);
                            tx.Commit();
                        }

                        pp = pl.GetProjectPosition(XYZ.Zero);
                        anchorE_m = UnitUtils.ConvertFromInternalUnits(pp.EastWest, UnitTypeId.Meters);
                        anchorN_m = UnitUtils.ConvertFromInternalUnits(pp.NorthSouth, UnitTypeId.Meters);
                        projectHasCoords = true;
                    }
                }

                if (projectHasCoords)
                {
                    var pp2 = pl.GetProjectPosition(XYZ.Zero);
                    anchorE_m = UnitUtils.ConvertFromInternalUnits(pp2.EastWest, UnitTypeId.Meters);
                    anchorN_m = UnitUtils.ConvertFromInternalUnits(pp2.NorthSouth, UnitTypeId.Meters);
                }

                var service = new RevitExtrusionService(AssumeMeters, DepthIsBelowGround, anchorE_m, anchorN_m);

                var result = service.CreateExtrusions(
                    doc, Boreholes, Layers ?? new List<GeologyLayer>(),
                    BoreholeDiameterMeters, MaterialNameColumn,
                    UseCodeAsLayerKey); // << NEU: zusammengelegte Schichten verwenden

                if (!string.IsNullOrEmpty(result))
                    TaskDialog.Show("BIMassist", result + (projectHasCoords ? "" : "\nHinweis: Shared Coordinates wurden nicht gesetzt."));

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    BIMassist.Commands.BoreholeManagerCommand.ActivateWindow();
                }));
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMassist", "Fehler bei Extrusionen:\n" + ex.Message);
            }
        }

        public string GetName() { return "BIMassist.CreateExtrusions"; }
    }

    public class PickGroundModelBoundaryHandler : IExternalEventHandler
    {
        public List<Borehole> Boreholes { get; set; } = new List<Borehole>();
        public List<GeologyLayer> Layers { get; set; } = new List<GeologyLayer>();
        public Action<List<GroundModelBoundaryPoint>> BoundaryUpdated { get; set; }
        public Action<GroundModelAxis> AxisUpdated { get; set; }

        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc == null) { TaskDialog.Show("BIMassist", "Kein aktives Dokument."); return; }
                var doc = uiDoc.Document;

                var points = PickBoundaryPoints(uiDoc);
                if (points == null || points.Count < 3)
                {
                    TaskDialog.Show("BIMassist", "Kein gültiger Umriss definiert (mind. 3 Punkte).");
                    return;
                }

                var axis = PickAxis(uiDoc);
                if (axis == null || !axis.IsValid())
                {
                    TaskDialog.Show("BIMassist", "Keine gültige Achse definiert. Umriss und Achse wurden nicht gespeichert.");
                    return;
                }

                new BoreholeProjectStorage(doc).Save(
                    Boreholes ?? new List<Borehole>(),
                    Layers ?? new List<GeologyLayer>(),
                    points,
                    null,
                    axis,
                    true);

                BoundaryUpdated?.Invoke(points.Select(p => p.Clone()).ToList());
                AxisUpdated?.Invoke(axis.Clone());
                TaskDialog.Show("BIMassist", $"Umriss und Achse gespeichert.\n\nUmriss: {points.Count} Punkte");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Benutzer hat abgebrochen.
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMassist", "Fehler beim Speichern des Umrisses:\n" + ex.Message);
            }
        }

        private List<GroundModelBoundaryPoint> PickBoundaryPoints(UIDocument uidoc)
        {
            var points = new List<GroundModelBoundaryPoint>();
            TaskDialog.Show("BIMassist", "Umrisspunkte anklicken (Esc beendet, mind. 3 Punkte).\nEin bereits gespeicherter Umriss wird durch diese Eingabe ersetzt.");

            while (true)
            {
                try
                {
                    var p = uidoc.Selection.PickPoint("Umrisspunkt klicken (Esc beendet)");
                    points.Add(new GroundModelBoundaryPoint
                    {
                        X = UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                        Y = UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters)
                    });
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }
            }

            return points;
        }

        private GroundModelAxis PickAxis(UIDocument uidoc)
        {
            TaskDialog.Show("BIMassist", "Achse definieren: zwei Punkte nacheinander anklicken.\nEine bereits gespeicherte Achse wird durch diese Eingabe ersetzt.");
            var a = uidoc.Selection.PickPoint("Achsenanfang klicken");
            var b = uidoc.Selection.PickPoint("Achsenende klicken");

            var axis = new GroundModelAxis
            {
                X0 = UnitUtils.ConvertFromInternalUnits(a.X, UnitTypeId.Meters),
                Y0 = UnitUtils.ConvertFromInternalUnits(a.Y, UnitTypeId.Meters),
                X1 = UnitUtils.ConvertFromInternalUnits(b.X, UnitTypeId.Meters),
                Y1 = UnitUtils.ConvertFromInternalUnits(b.Y, UnitTypeId.Meters)
            };

            return axis.IsValid() ? axis : null;
        }

        public string GetName() => "BIMassist.PickGroundModelBoundary";
    }

    public class CreateGroundModelHandler : IExternalEventHandler
    {
        public List<Borehole> Boreholes { get; set; }
        public List<GeologyLayer> Layers { get; set; }

        // Konfiguration
        public bool AssumeMeters { get; set; } = true;
        public bool DepthIsBelowGround { get; set; } = true;
        public double GridStepMeters { get; set; } = 10.0;          // optionales Raster (Standard 10 m)
        public double MaxInterpDistanceMeters { get; set; } = 0.0;  // 0 = unbegrenzt
        public double MinThicknessMeters { get; set; } = 0.05;
        public bool UseCodeAsLayerKey { get; set; } = false;
        public bool UseFlatBoundaryTop { get; set; } = false;
        public enum EdgeTopMode { NearestBorehole = 0, FlatAtMean = 1 }
        public EdgeTopMode BoundaryTopMode { get; set; } = EdgeTopMode.NearestBorehole;
        public string MaterialNameColumn { get; set; } = "Description"; // PATCH: selected column for naming
        public List<GroundModelBoundaryPoint> BoundaryPoints { get; set; } = new List<GroundModelBoundaryPoint>();
        public GroundModelAxis Axis { get; set; }
        public Action<List<GroundModelBoundaryPoint>> BoundaryUpdated { get; set; }
        public Action<GroundModelAxis> AxisUpdated { get; set; }

        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc == null) { TaskDialog.Show("BIMassist", "Kein aktives Dokument."); return; }
                var doc = uiDoc.Document;

                // Boundary verwenden oder neu zeichnen
                var savedBoundary = BoundaryPoints?.Where(p => p != null).ToList() ?? new List<GroundModelBoundaryPoint>();
                List<GroundModelBoundaryPoint> activeBoundaryPoints = null;
                CurveLoop boundary = null;

                if (savedBoundary.Count >= 3)
                {
                    var tdBoundary = new TaskDialog("BIMassist – Umriss")
                    {
                        MainInstruction = "Gespeicherten Umriss verwenden?",
                        MainContent = "Im Projekt ist bereits ein Umriss für das 3D-Bodenmodell gespeichert.\n\nJa = gespeicherten Umriss verwenden\nNein = Umriss neu eingeben",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                        DefaultButton = TaskDialogResult.Yes
                    };

                    var boundaryChoice = tdBoundary.Show();
                    if (boundaryChoice == TaskDialogResult.Cancel)
                        return;

                    if (boundaryChoice == TaskDialogResult.Yes)
                    {
                        activeBoundaryPoints = savedBoundary.Select(p => p.Clone()).ToList();
                        boundary = BoundaryPointsToCurveLoop(doc, activeBoundaryPoints, null);
                    }
                }

                if (boundary == null)
                {
                    boundary = DrawBoundaryPolygon(uiDoc, null, out activeBoundaryPoints);
                    if (boundary != null && activeBoundaryPoints != null && activeBoundaryPoints.Count >= 3)
                    {
                        BoundaryPoints = activeBoundaryPoints.Select(p => p.Clone()).ToList();
                        BoundaryUpdated?.Invoke(BoundaryPoints.Select(p => p.Clone()).ToList());
                        new BoreholeProjectStorage(doc).Save(
                            Boreholes ?? new List<Borehole>(),
                            Layers ?? new List<GeologyLayer>(),
                            BoundaryPoints);
                    }
                }

                if (boundary == null)
                {
                    TaskDialog.Show("BIMassist", "Keine Boundary definiert (mind. 3 Punkte)." );
                    return;
                }

                // WICHTIG: Nur Bohrungen innerhalb der Boundary verwenden!
                // Dies ermöglicht separate Bodenmodelle für verschiedene Bereiche.
                var filteredBoreholes = FilterBoreholesInsideBoundary(doc, Boreholes, boundary);

                if (filteredBoreholes.Count == 0)
                {
                    TaskDialog.Show("BIMassist", "Keine Bohrungen innerhalb der Boundary gefunden.\n\n" +
                        $"Gesamtzahl Bohrungen: {Boreholes?.Count ?? 0}\n" +
                        "Bitte zeichnen Sie die Boundary um die gewünschten Bohrungen.");
                    return;
                }

                // Nur Schichten der gefilterten Bohrungen verwenden
                var filteredBoreholeIds = new HashSet<string>(
                    filteredBoreholes.Select(b => (b.LocationID ?? "").Trim()),
                    StringComparer.OrdinalIgnoreCase);

                var filteredLayers = Layers
                    .Where(l => filteredBoreholeIds.Contains((l.LocationID ?? "").Trim()))
                    .ToList();

                if (filteredLayers.Count == 0)
                {
                    TaskDialog.Show("BIMassist", "Keine Schichten für die Bohrungen innerhalb der Boundary gefunden.");
                    return;
                }

                // New strategy: build layer volumes via Toposolid (variable thickness) and export each layer to a family.
                // This avoids unstable BRep boolean workflows and matches the existing SolidsToNewFamilyCommand behavior.
                var toposolidSvc = new ToposolidLayerService(DepthIsBelowGround, MinThicknessMeters);

                // Prefer a specific toposolid type for the workflow (fallback to first available)
                const string DefaultToposolidTypeName = "Temporäre Konstruktionsdecke";
                var topoType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ToposolidType))
                    .Cast<ToposolidType>()
                    .FirstOrDefault(t => string.Equals(t.Name, DefaultToposolidTypeName, StringComparison.OrdinalIgnoreCase))
                    ?? new FilteredElementCollector(doc)
                        .OfClass(typeof(ToposolidType))
                        .Cast<ToposolidType>()
                        .FirstOrDefault();

                if (topoType == null)
                {
                    TaskDialog.Show("BIMassist", "Kein Toposolid-Typ im Projekt gefunden.");
                    return;
                }

                // Bottom level: create/update a dedicated "Geländeunterkante" level based on lowest layer base
                // NUR gefilterte Bohrungen/Schichten verwenden!
                var levelBottom = EnsureBottomLevel(doc, filteredBoreholes, filteredLayers, DepthIsBelowGround);

                if (levelBottom == null)
                {
                    TaskDialog.Show("BIMassist", "Kein Level im Projekt gefunden.");
                    return;
                }

                // Achse definieren oder gespeicherte Achse wiederverwenden (für Querprofil-Modus)
                var axis = ResolveAxis(uiDoc, doc);
                if (!axis.HasValue)
                    return;

                // Bereichsbezeichnung abfragen
                string areaName = AskAreaName();
                if (areaName == null)
                    return; // Benutzer hat abgebrochen

                // Strategy update:
                // - Build a base surface for each layer (its Unterkante).
                // - Build the 3D layer solid as a TRUE "slice" by boolean difference between the current base and next base.
                //   This prevents overlaps because each slice occupies a unique vertical interval.
                // - Before creating the family, delete any existing family with the same name (to avoid "Name must be unique").

                // 1) Create base surfaces (Unterkanten) along axis per layer
                // NUR gefilterte Bohrungen/Schichten verwenden!
                var baseTopos = toposolidSvc.BuildBaseSurfacePerLayer_AlongAxis(
                    doc,
                    filteredBoreholes,
                    filteredLayers,
                    levelBottom,
                    topoType,
                    boundary,
                    axis.Value.p0,
                    axis.Value.p1,
                    UseCodeAsLayerKey);

                if (baseTopos == null || baseTopos.Count == 0)
                {
                    TaskDialog.Show("BIMassist", "Keine Modelle erstellt.");
                    return;
                }

                // KRITISCH: Vor der Sortierung die Layer zusammenführen (identisch zu ToposolidLayerService).
                // Damit sind die Keys konsistent und KeyMinDepthTop findet die richtigen Einträge.
                // NUR gefilterte Layer verwenden!
                var mergedLayersForSort = MergeLayersForSort(filteredLayers, UseCodeAsLayerKey);

                // Build a stable top-down order based on borehole data (DepthTop): smallest DepthTop = topmost.
                // This matches the requested stratigraphic processing order and avoids bbox-dependent sorting.
                double KeyMinDepthTop(string layerKey)
                {
                    double best = double.PositiveInfinity;
                    foreach (var gl in mergedLayersForSort)
                    {
                        if (gl == null) continue;
                        var k = UseCodeAsLayerKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
                        k = System.Text.RegularExpressions.Regex.Replace((k ?? string.Empty).Trim().ToUpperInvariant(), @"\s+", " ");
                        if (!string.Equals(k, layerKey, StringComparison.OrdinalIgnoreCase)) continue;
                        if (gl.DepthTop < best) best = gl.DepthTop;
                    }
                    return best;
                }

                var ordered = baseTopos
                    .Select(kv => (Key: kv.Key, Id: kv.Value, Topo: doc.GetElement(kv.Value) as Toposolid, MinTop: KeyMinDepthTop(kv.Key)))
                    .Where(x => x.Topo != null)
                    .ToList();

                ordered.Sort((a, b) =>
                {
                    int cmp = a.MinTop.CompareTo(b.MinTop); // ascending: top (small depth) first
                    if (cmp != 0) return cmp;
                    return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
                });

                int famCreated = 0;
                var toDelete = new List<ElementId>();
                var opt = new Options { View = doc.ActiveView, ComputeReferences = false };

                var dbg = new System.Text.StringBuilder();
                dbg.AppendLine($"Layers ordered: {ordered.Count}");
                dbg.AppendLine(string.Join(" | ", ordered.Select(o => $"{o.Key}({o.MinTop:0.###})")));

                Solid? SolidFromToposolid(Toposolid t)
                {
                    var geos = Core.FamilyGeometryTools.GetAllGeometryObjects(t, opt);
                    var solid = geos?.OfType<Solid>()?.Where(s => s != null && s.Volume > 1e-9)
                        .OrderByDescending(s => s.Volume)
                        .FirstOrDefault();
                    return solid;
                }

                // Familienname: "[Bereich] Bodenschicht [Code]"
                string BuildFamilyName(string code)
                {
                    var sanitizedArea = SanitizeFamilyName(areaName);
                    var sanitizedCode = SanitizeFamilyName(code);
                    return $"{sanitizedArea} Bodenschicht {sanitizedCode}";
                }

                string SanitizeFamilyName(string name)
                {
                    if (string.IsNullOrWhiteSpace(name)) return "Layer";
                    // Remove prohibited characters for Revit family names: \ / : * ? " < > |
                    var prohibited = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
                    var sanitized = name;
                    foreach (var ch in prohibited)
                        sanitized = sanitized.Replace(ch.ToString(), "");
                    // Also remove any other invalid file name characters
                    foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                        sanitized = sanitized.Replace(ch.ToString(), "");
                    sanitized = sanitized.Trim();
                    return string.IsNullOrWhiteSpace(sanitized) ? "Layer" : sanitized;
                }

                void DeleteFamilyIfExists(string familyName)
                {
                    if (string.IsNullOrWhiteSpace(familyName)) return;
                    var fam = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
                    if (fam == null) return;

                    using (var tx = new Transaction(doc, "AWESBox – Bodenschicht-Familie bereinigen"))
                    {
                        tx.Start();
                        try { doc.Delete(fam.Id); } catch { }
                        tx.Commit();
                    }
                }

                // 2) Slice solids top-down:
                //    - Top slice: topSolid - firstBaseSolid (represents the uppermost layer)
                //    - Middle slices: base_i - base_(i+1)
                //    - Last slice: lastBaseSolid (down to levelBottom)

                try
                {
                    // Top surface from layer data (top-of-top-layer per borehole)
                    ElementId topSurfaceId = ElementId.InvalidElementId;
                    try
                    {
                        topSurfaceId = toposolidSvc.BuildTopSurface_AlongAxis_FromLayerData(
                            doc,
                            Boreholes,
                            Layers,
                            levelBottom,
                            topoType,
                            boundary,
                            axis.Value.p0,
                            axis.Value.p1,
                            UseCodeAsLayerKey);
                    }
                    catch (Exception exTop)
                    {
                        dbg.AppendLine($"Top surface creation failed: {exTop.Message}");
                    }

                    bool topLayerCreated = false;
                    if (topSurfaceId != ElementId.InvalidElementId && ordered.Count > 0)
                    {
                        var topTopo = doc.GetElement(topSurfaceId) as Toposolid;
                        var firstBaseSolid = SolidFromToposolid(ordered[0].Topo);
                        var topSolid = topTopo != null ? SolidFromToposolid(topTopo) : null;

                        if (topSolid != null && firstBaseSolid != null)
                        {
                            try
                            {
                                var topSlice = BooleanOperationsUtils.ExecuteBooleanOperation(topSolid, firstBaseSolid, BooleanOperationsType.Difference);
                                if (topSlice != null && topSlice.Volume > 1e-9)
                                {
                                    var topFamilyName = BuildFamilyName(ordered[0].Key);
                                    DeleteFamilyIfExists(topFamilyName);
                                    Core.FamilyGeometryTools.CreateNewFamily(app, doc, new List<GeometryObject> { topSlice }, topFamilyName);
                                    famCreated++;
                                    topLayerCreated = true;
                                    dbg.AppendLine($"Top slice created for '{topFamilyName}' vol={topSlice.Volume:0.###}");
                                }
                            }
                            catch (Exception exTopSlice)
                            {
                                dbg.AppendLine($"Top slice creation failed: {exTopSlice.Message}");
                            }
                        }

                        toDelete.Add(topSurfaceId);
                    }

                    // KRITISCH: Korrekte Schichtbildung nach stratigraphischer Reihenfolge:
                    // - ordered[i] enthält die Unterkante (BaseSurface) von Schicht i
                    // - Für Schicht i brauchen wir: Oberkante_i - Unterkante_i
                    // - Oberkante_i = Unterkante_(i-1) = ordered[i-1].Topo
                    // - Unterkante_i = ordered[i].Topo
                    // Also: Schicht_i = ordered[i-1].Topo - ordered[i].Topo
                    //
                    // Beispiel mit 3 Codes (1, 2, 3):
                    // - Schicht 1 (Code 1): TopSurface - Base[0]  (bereits oben erstellt)
                    // - Schicht 2 (Code 2): Base[0] - Base[1]
                    // - Schicht 3 (Code 3): Base[1] - Base[2]

                    // startIndex = 1 wenn topLayerCreated, sonst 0
                    int startIndex = topLayerCreated ? 1 : 0;

                    // Verarbeite alle Schichten von startIndex bis zur letzten (inklusiv!)
                    for (int i = startIndex; i < ordered.Count; i++)
                    {
                        var cur = ordered[i];  // Aktuelle Schicht mit Key und Unterkante

                        // Die Oberkante dieser Schicht ist die Unterkante der vorherigen Schicht
                        Solid sUpper;  // Oberkante (oberer Abschluss)
                        Solid sLower;  // Unterkante (unterer Abschluss)

                        if (i == 0)
                        {
                            // Erste Schicht: Oberkante = TopSurface (sollte bereits oben behandelt sein)
                            continue;
                        }

                        var prev = ordered[i - 1];
                        sUpper = SolidFromToposolid(prev.Topo);  // Unterkante der vorherigen Schicht = Oberkante dieser Schicht
                        sLower = SolidFromToposolid(cur.Topo);   // Unterkante dieser Schicht

                        if (sUpper == null) { dbg.AppendLine($"Skipped '{cur.Key}': upper surface (base[{i-1}]) has no solid"); continue; }
                        if (sLower == null) { dbg.AppendLine($"Skipped '{cur.Key}': lower surface (base[{i}]) has no solid"); continue; }

                        Solid slice;
                        try
                        {
                            // Schicht = Oberkante - Unterkante
                            slice = BooleanOperationsUtils.ExecuteBooleanOperation(sUpper, sLower, BooleanOperationsType.Difference);
                        }
                        catch (Exception exBool)
                        {
                            dbg.AppendLine($"Skipped '{cur.Key}': boolean failed - {exBool.Message}");
                            continue;
                        }

                        if (slice == null || slice.Volume <= 1e-9) { dbg.AppendLine($"Skipped '{cur.Key}': zero volume"); continue; }

                        try
                        {
                            var familyName = BuildFamilyName(cur.Key);
                            dbg.AppendLine($"Slice '{familyName}' from base[{i-1}] - base[{i}] vol={slice.Volume:0.###}");
                            DeleteFamilyIfExists(familyName);
                            Core.FamilyGeometryTools.CreateNewFamily(app, doc, new List<GeometryObject> { slice }, familyName);
                            famCreated++;
                        }
                        catch (Exception exFam)
                        {
                            dbg.AppendLine($"Failed to create family for '{cur.Key}': {exFam.Message}");
                        }
                    }
                }
                finally
                {                }

                // 3) Cleanup intermediate base toposolids (always runs, even if family creation failed)
                toDelete.AddRange(ordered.Select(x => x.Id));
                if (toDelete.Count > 0)
                {
                    using (var txDel = new Transaction(doc, "AWESBox – Zwischen-Toposolids löschen"))
                    {
                        txDel.Start();
                        foreach (var id in toDelete)
                        {
                            try { doc.Delete(id); } catch { }
                        }
                        txDel.Commit();
                    }
                }

                TaskDialog.Show("BIMassist", famCreated > 0
                    ? $"3D-Bodenmodell erstellt. Familien erzeugt: {famCreated}"
                    : "Keine Familien erzeugt." );
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User cancelled while picking points
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMassist", "Fehler bei 3D-Bodenmodell:\n" + ex.Message);
            }
        }

        public string GetName() => "BIMassist.CreateGroundModel";

        // ---------- Helper ----------

        private (XYZ p0, XYZ p1)? ResolveAxis(UIDocument uidoc, Document doc)
        {
            var savedAxis = Axis;
            if (savedAxis != null && savedAxis.IsValid())
            {
                var tdAxis = new TaskDialog("BIMassist – Achse")
                {
                    MainInstruction = "Gespeicherte Achse verwenden?",
                    MainContent = "Im Projekt ist bereits eine Achse für das 3D-Bodenmodell gespeichert.\n\nJa = gespeicherte Achse verwenden\nNein = Achse neu eingeben und speichern",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Yes
                };

                var axisChoice = tdAxis.Show();
                if (axisChoice == TaskDialogResult.Cancel)
                    return null;

                if (axisChoice == TaskDialogResult.Yes)
                    return AxisToXyz(doc, savedAxis);
            }

            var pickedAxis = AskAxis(uidoc);
            if (!pickedAxis.HasValue)
                return null;

            var storedAxis = AxisFromXyz(pickedAxis.Value.p0, pickedAxis.Value.p1);
            Axis = storedAxis;
            AxisUpdated?.Invoke(storedAxis.Clone());
            new BoreholeProjectStorage(doc).Save(
                Boreholes ?? new List<Borehole>(),
                Layers ?? new List<GeologyLayer>(),
                BoundaryPoints ?? new List<GroundModelBoundaryPoint>(),
                null,
                storedAxis,
                true);

            return pickedAxis;
        }

        private static GroundModelAxis AxisFromXyz(XYZ p0, XYZ p1)
        {
            return new GroundModelAxis
            {
                X0 = UnitUtils.ConvertFromInternalUnits(p0.X, UnitTypeId.Meters),
                Y0 = UnitUtils.ConvertFromInternalUnits(p0.Y, UnitTypeId.Meters),
                X1 = UnitUtils.ConvertFromInternalUnits(p1.X, UnitTypeId.Meters),
                Y1 = UnitUtils.ConvertFromInternalUnits(p1.Y, UnitTypeId.Meters)
            };
        }

        private static (XYZ p0, XYZ p1)? AxisToXyz(Document doc, GroundModelAxis axis)
        {
            if (axis == null || !axis.IsValid()) return null;
            double z = 0.0;
            var vp = doc?.ActiveView as ViewPlan;
            if (vp?.GenLevel != null)
            {
                try { z = vp.GenLevel.Elevation; } catch { z = 0.0; }
            }

            var p0 = new XYZ(
                UnitUtils.ConvertToInternalUnits(axis.X0, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(axis.Y0, UnitTypeId.Meters),
                z);
            var p1 = new XYZ(
                UnitUtils.ConvertToInternalUnits(axis.X1, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(axis.Y1, UnitTypeId.Meters),
                z);
            return (p0, p1);
        }

        private (XYZ p0, XYZ p1)? AskAxis(UIDocument uidoc, Level onLevel)
        {
            TaskDialog.Show("AWESBox", "Achse definieren: zwei Punkte nacheinander anklicken (Esc bricht ab).");
            try
            {
                var doc = uidoc.Document;

                // robustes Level/Elevation-Handling (analog zu DrawBoundaryPolygon)
                Level level = onLevel;
                if (level == null)
                {
                    var vp = doc.ActiveView as ViewPlan;
                    if (vp != null && vp.GenLevel != null)
                    {
                        try { level = doc.GetElement(vp.GenLevel.Id) as Level; } catch { level = null; }
                    }
                }

                var a = uidoc.Selection.PickPoint("Achsenanfang klicken");
                var b = uidoc.Selection.PickPoint("Achsenende klicken");

                // Wenn kein Level verfügbar ist, Z der ersten Auswahl verwenden
                var z = (level != null) ? level.Elevation : a.Z;
                return (new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
        }

        // Overload ohne Level-Parameter (ruft die Version mit Level=null auf)
        private (XYZ p0, XYZ p1)? AskAxis(UIDocument uidoc)
        {
            return AskAxis(uidoc, null);
        }

        /// <summary>
        /// Fragt den Benutzer nach einer Bezeichnung für den Bereich.
        /// Diese Bezeichnung wird in den Familiennamen verwendet.
        /// </summary>
        private string AskAreaName()
        {
            // WPF InputDialog für Bereichsbezeichnung
            var dialog = new System.Windows.Window
            {
                Title = "Bereichsbezeichnung",
                Width = 400,
                Height = 150,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                ResizeMode = System.Windows.ResizeMode.NoResize
            };

            var stackPanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(15) };

            var label = new System.Windows.Controls.Label 
            { 
                Content = "Bezeichnung für diesen Bereich eingeben:",
                Margin = new System.Windows.Thickness(0, 0, 0, 5)
            };

            var textBox = new System.Windows.Controls.TextBox 
            { 
                Text = "Bereich",
                Margin = new System.Windows.Thickness(0, 0, 0, 15)
            };
            textBox.SelectAll();
            textBox.Focus();

            var buttonPanel = new System.Windows.Controls.StackPanel 
            { 
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var okButton = new System.Windows.Controls.Button 
            { 
                Content = "OK", 
                Width = 75, 
                Margin = new System.Windows.Thickness(0, 0, 10, 0),
                IsDefault = true
            };

            var cancelButton = new System.Windows.Controls.Button 
            { 
                Content = "Abbrechen", 
                Width = 75,
                IsCancel = true
            };

            string result = null;
            okButton.Click += (s, e) => 
            { 
                result = textBox.Text; 
                dialog.DialogResult = true; 
                dialog.Close(); 
            };
            cancelButton.Click += (s, e) => 
            { 
                dialog.DialogResult = false; 
                dialog.Close(); 
            };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            stackPanel.Children.Add(label);
            stackPanel.Children.Add(textBox);
            stackPanel.Children.Add(buttonPanel);

            dialog.Content = stackPanel;

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(result))
            {
                return result.Trim();
            }

            return null;
        }

        private FamilyInstance FindLatestInstanceByFamilyName(Document doc, string familyName)
        {
            var fam = new FilteredElementCollector(doc)
                .OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
            if (fam == null) return null;

            var fi = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(x => x.Symbol != null && x.Symbol.Family != null && x.Symbol.Family.Id == fam.Id)
                .OrderBy(x => x.Id.Value)
                .LastOrDefault();

            return fi;
        }

        private Level EnsureBottomLevel(Document doc,
            IList<Borehole> boreholes,
            IList<GeologyLayer> layers,
            bool depthIsBelowGround)
        {
            const string LevelName = "Geländeunterkante";
            double minZ = double.MaxValue;
            foreach (var l in layers)
            {
                var bh = boreholes.FirstOrDefault(b => string.Equals((b.LocationID ?? "").Trim(), (l.LocationID ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (bh == null) continue;
                double zGL = bh.GroundLevel;
                double topD = l.DepthTop;
                double baseD = l.DepthBase <= l.DepthTop ? (l.DepthTop + l.DepthBase) : l.DepthBase;
                double zBase = depthIsBelowGround ? zGL - baseD : zGL + baseD;
                if (zBase < minZ) minZ = zBase;
            }
            if (minZ == double.MaxValue)
                throw new InvalidOperationException("Konnte tiefste Bohrung (zBase) nicht bestimmen.");

            double targetZ_m = minZ - 1.0;
            double targetZ_internal = UnitUtils.ConvertToInternalUnits(targetZ_m, UnitTypeId.Meters);

            using (var tx = new Transaction(doc, "AWESBox – Ebene Geländeunterkante"))
            {
                tx.Start();
                var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                         .FirstOrDefault(l => string.Equals(l.Name, LevelName, StringComparison.OrdinalIgnoreCase));

                if (lvl == null)
                {
                    lvl = Level.Create(doc, targetZ_internal);
                    try { lvl.Name = LevelName; } catch { }
                }
                else
                {
                    if (Math.Abs(lvl.Elevation - targetZ_internal) > 1e-6)
                        lvl.Elevation = targetZ_internal;
                }
                tx.Commit();
                return lvl;
            }
        }

        private ToposolidType EnsureBaseToposolidType(Document doc, string typeName)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType))
                            .Cast<ToposolidType>().FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var any = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType))
                       .Cast<ToposolidType>().FirstOrDefault();
            if (any == null) throw new InvalidOperationException("Kein ToposolidType im Projekt vorhanden.");

            using (var tx = new Transaction(doc, "AWESBox – Typ 'Bodenschicht'"))
            {
                tx.Start();
                var dup = any.Duplicate(typeName) as ToposolidType;
                // Dicke egal – Service markiert den Typ gleich als „variable Lage“
                tx.Commit();
                return dup;
            }
        }

        private CurveLoop DrawBoundaryPolygon(UIDocument uidoc, Level onLevel, out List<GroundModelBoundaryPoint> storedPoints)
        {
            storedPoints = new List<GroundModelBoundaryPoint>();
            var doc = uidoc.Document;
            // robustes Level/Elevation-Handling
            Level level = onLevel;
            if (level == null)
            {
                var vp = doc.ActiveView as ViewPlan;
                if (vp != null && vp.GenLevel != null)
                {
                    try { level = doc.GetElement(vp.GenLevel.Id) as Level; } catch { level = null; }
                }
            }

            var pts = new System.Collections.Generic.List<XYZ>();
            TaskDialog.Show("AWESBox", "Umrisspunkte anklicken (Esc beendet, mind. 3 Punkte).");

            double? zConst = null;
            while (true)
            {
                try
                {
                    var p = uidoc.Selection.PickPoint("Punkt klicken (Esc beendet)");
                    if (!zConst.HasValue)
                        zConst = (level != null) ? level.Elevation : p.Z;

                    pts.Add(new XYZ(p.X, p.Y, zConst.Value));
                    storedPoints.Add(new GroundModelBoundaryPoint
                    {
                        X = UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                        Y = UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters)
                    });
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }
            }

            if (pts.Count < 3) return null;

            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                loop.Append(Line.CreateBound(a, b));
            }

            // sicherstellen: CCW
            if (!loop.IsCounterclockwise(XYZ.BasisZ))
            {
                var flipped = new CurveLoop();
                var temp = new System.Collections.Generic.List<Curve>();
                foreach (var c in loop) temp.Insert(0, c.CreateReversed());
                foreach (var c in temp) flipped.Append(c);
                loop = flipped;
            }

            return loop;
        }

        private CurveLoop BoundaryPointsToCurveLoop(Document doc, IList<GroundModelBoundaryPoint> points, Level onLevel)
        {
            if (points == null || points.Count < 3) return null;

            Level level = onLevel;
            if (level == null)
            {
                var vp = doc.ActiveView as ViewPlan;
                if (vp != null && vp.GenLevel != null)
                {
                    try { level = doc.GetElement(vp.GenLevel.Id) as Level; } catch { level = null; }
                }
            }

            double z = level != null ? level.Elevation : 0.0;
            var loop = new CurveLoop();
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                var pa = new XYZ(
                    UnitUtils.ConvertToInternalUnits(a.X, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(a.Y, UnitTypeId.Meters),
                    z);
                var pb = new XYZ(
                    UnitUtils.ConvertToInternalUnits(b.X, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(b.Y, UnitTypeId.Meters),
                    z);
                if (pa.DistanceTo(pb) > 1e-9)
                    loop.Append(Line.CreateBound(pa, pb));
            }

            if (loop.IsOpen()) return null;

            if (!loop.IsCounterclockwise(XYZ.BasisZ))
            {
                var flipped = new CurveLoop();
                var temp = new System.Collections.Generic.List<Curve>();
                foreach (var c in loop) temp.Insert(0, c.CreateReversed());
                foreach (var c in temp) flipped.Append(c);
                loop = flipped;
            }

            return loop;
        }

        private static string GetLayerDisplayName(
    IList<GeologyLayer> all, string key, bool useCodeKey, string materialColumn)
        {
            // denselben Normalizer wie für die Gruppierung verwenden,
            // damit wir das passende Layer-Objekt zuverlässig finden:
            string NormalizeKey(GeologyLayer gl)
            {
                var raw = useCodeKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
                raw = raw.Trim(); if (string.IsNullOrEmpty(raw)) raw = "SCHICHT";
                return System.Text.RegularExpressions.Regex.Replace(raw.ToUpperInvariant(), @"\\s+", " ");
            }

            var match = all.FirstOrDefault(gl => NormalizeKey(gl) == key);
            if (match == null) return key;

            // Name aus Benutzer-Auswahl holen
            string pick(string s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
            string name;
            switch ((materialColumn ?? "").Trim())
            {
                case "GeologyCode": name = pick(match.GeologyCode); break;
                case "Classification": name = pick(match.Classification); break;
                default: name = pick(match.Description); break; // "Description"
            }

            // Fallbacks, falls leer
            if (string.IsNullOrEmpty(name))
                name = pick(match.Description) ?? pick(match.GeologyCode) ?? pick(match.Classification) ?? key;

            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(ch.ToString(), "");

            return name;
        }

        /// <summary>
        /// Zusammenführen von Layern pro Bohrung nach GeologyCode/Description.
        /// Identisch zur Logik in ToposolidLayerService.MergeLayersPerBorehole.
        /// </summary>
        private static List<GeologyLayer> MergeLayersForSort(IList<GeologyLayer> input, bool useCodeKey)
        {
            if (input == null || input.Count == 0) return new List<GeologyLayer>();

            string NormalizeKey(GeologyLayer gl)
            {
                var raw = useCodeKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
                raw = raw.Trim();
                if (string.IsNullOrEmpty(raw)) raw = "SCHICHT";
                return System.Text.RegularExpressions.Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ");
            }

            var groups = input.GroupBy(gl =>
            {
                var loc = (gl.LocationID ?? "").Trim().ToUpperInvariant();
                var key = NormalizeKey(gl);
                return loc + "|" + key;
            });

            var outList = new List<GeologyLayer>();
            foreach (var g in groups)
            {
                string firstLoc = null, firstDesc = null, firstCode = null;
                double minTop = double.MaxValue;
                double maxBaseAbs = double.MinValue;

                foreach (var gl in g)
                {
                    if (firstLoc == null) firstLoc = gl.LocationID;
                    if (firstDesc == null) firstDesc = gl.Description;
                    if (firstCode == null) firstCode = gl.GeologyCode;

                    double topD = gl.DepthTop;
                    double baseD = gl.DepthBase <= gl.DepthTop ? (gl.DepthTop + gl.DepthBase) : gl.DepthBase;

                    if (topD < minTop) minTop = topD;
                    if (baseD > maxBaseAbs) maxBaseAbs = baseD;
                }

                outList.Add(new GeologyLayer
                {
                    LocationID = firstLoc,
                    Description = firstDesc,
                    GeologyCode = firstCode,
                    DepthTop = minTop,
                    DepthBase = maxBaseAbs
                });
            }
            return outList;
        }

        /// <summary>
        /// Filtert Bohrungen, die innerhalb der gezeichneten Boundary liegen.
        /// Ermöglicht separate Bodenmodelle für verschiedene Bereiche.
        /// 
        /// WICHTIG: Die Boundary ist in Revit-internen Koordinaten (relativ zum Projektbasispunkt).
        /// Die Bohrungen sind in absoluten Koordinaten (z.B. UTM).
        /// Wir müssen beide in dasselbe Koordinatensystem bringen.
        /// </summary>
        private List<Borehole> FilterBoreholesInsideBoundary(Document doc, IList<Borehole> allBoreholes, CurveLoop boundary)
        {
            if (allBoreholes == null || allBoreholes.Count == 0 || boundary == null)
                return new List<Borehole>();

            // Projektbasispunkt bestimmen (Shared Coordinates)
            var pl = doc.ActiveProjectLocation;
            var pp = pl?.GetProjectPosition(XYZ.Zero);

            double projEast_m = 0, projNorth_m = 0;
            if (pp != null)
            {
                projEast_m = UnitUtils.ConvertFromInternalUnits(pp.EastWest, UnitTypeId.Meters);
                projNorth_m = UnitUtils.ConvertFromInternalUnits(pp.NorthSouth, UnitTypeId.Meters);
            }

            // Boundary-Punkte in Meter konvertieren (Revit-interne → Meter)
            // Diese sind relativ zum Revit-Ursprung (0,0), nicht zu Shared Coordinates!
            var boundaryPts_internal = new List<XYZ>();
            foreach (var c in boundary)
            {
                var p = c.GetEndPoint(0);
                boundaryPts_internal.Add(new XYZ(
                    UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters),
                    0));
            }

            // Bohrungskoordinaten in Revit-interne Koordinaten umrechnen:
            // Bohrung_intern = Bohrung_absolut - Projektbasispunkt
            // Dann prüfen ob innerhalb der Boundary

            var result = new List<Borehole>();
            foreach (var bh in allBoreholes)
            {
                if (bh == null) continue;

                // Bohrungsposition: von absolut (UTM) zu Revit-intern
                double bhX_intern = bh.Easting - projEast_m;
                double bhY_intern = bh.Northing - projNorth_m;

                // Prüfen ob Punkt innerhalb des Polygons liegt
                if (PointInPolygon2D(bhX_intern, bhY_intern, boundaryPts_internal))
                {
                    result.Add(bh);
                }
            }

            return result;
        }

        /// <summary>
        /// Prüft ob ein 2D-Punkt innerhalb eines Polygons liegt (Ray-Casting-Algorithmus).
        /// </summary>
        private static bool PointInPolygon2D(double px, double py, List<XYZ> polygon)
        {
            if (polygon == null || polygon.Count < 3) return false;

            bool inside = false;
            int n = polygon.Count;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];

                // Ray-Casting: Strahl von Punkt nach rechts, zähle Schnitte mit Kanten
                bool intersect = ((pi.Y > py) != (pj.Y > py)) &&
                                 (px < (pj.X - pi.X) * (py - pi.Y) / (pj.Y - pi.Y + 1e-12) + pi.X);
                if (intersect) inside = !inside;
            }

            // Zusätzlich: Punkt auf Kante?
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (PointOnSegment2D(px, py, polygon[j], polygon[i]))
                    return true;
            }

            return inside;
        }

        /// <summary>
        /// Prüft ob ein Punkt auf einem Liniensegment liegt.
        /// </summary>
        private static bool PointOnSegment2D(double px, double py, XYZ a, XYZ b)
        {
            double cross = (py - a.Y) * (b.X - a.X) - (px - a.X) * (b.Y - a.Y);
            const double EPS = 0.1; // 10 cm Toleranz
            if (Math.Abs(cross) > EPS) return false;

            double dot = (px - a.X) * (b.X - a.X) + (py - a.Y) * (b.Y - a.Y);
            if (dot < -EPS) return false;

            double len2 = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
            if (dot > len2 + EPS) return false;

            return true;
        }
    }

    public class PickPointOnViewHandler : IExternalEventHandler
    {
        public string Prompt { get; set; }
        public System.Threading.Tasks.TaskCompletionSource<(double EastingM, double NorthingM)?> Completion { get; set; }

        public void Execute(UIApplication app)
        {
            var tcs = Completion;
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) { tcs?.SetResult(null); return; }

                var pt = uidoc.Selection.PickPoint(ObjectSnapTypes.None,
                    string.IsNullOrWhiteSpace(Prompt) ? "Punkt wählen" : Prompt);
                if (pt == null) { tcs?.SetResult(null); return; }

                // Revit-Intern → Meter
                double x_m = UnitUtils.ConvertFromInternalUnits(pt.X, UnitTypeId.Meters);
                double y_m = UnitUtils.ConvertFromInternalUnits(pt.Y, UnitTypeId.Meters);

                // Projektlage (EastWest/NorthSouth) addieren → absolute E/N wie CSV
                var pl = uidoc.Document.ActiveProjectLocation;
                var pp = pl.GetProjectPosition(XYZ.Zero);
                double ew_m = UnitUtils.ConvertFromInternalUnits(pp.EastWest, UnitTypeId.Meters);
                double ns_m = UnitUtils.ConvertFromInternalUnits(pp.NorthSouth, UnitTypeId.Meters);

                tcs?.SetResult((ew_m + x_m, ns_m + y_m));
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                tcs?.SetResult(null);
            }
            catch
            {
                tcs?.SetResult(null);
            }
            finally
            {
                Completion = null;
                Prompt = null;
            }
        }

        public string GetName() => "AWESBox.PickPointOnActiveView";
    }

}
