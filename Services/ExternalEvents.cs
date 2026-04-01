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

        public void Execute(UIApplication app)
        {
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null) return;
            var storage = new BoreholeProjectStorage(uiDoc.Document);
            storage.Save(Boreholes ?? new List<Borehole>(), Layers ?? new List<GeologyLayer>());
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

        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc == null) { TaskDialog.Show("BIMassist", "Kein aktives Dokument."); return; }
                var doc = uiDoc.Document;

                // Boundary zeichnen
                var boundary = DrawBoundaryPolygon(uiDoc, null);
                if (boundary == null)
                {
                    TaskDialog.Show("BIMassist", "Keine Boundary definiert (mind. 3 Punkte)." );
                    return;
                }

                var builder = new GroundModelBuilder(AssumeMeters, DepthIsBelowGround)
                {
                    GridStepMeters = GridStepMeters,
                    MaxInterpDistanceMeters = MaxInterpDistanceMeters,
                    MinThicknessMeters = MinThicknessMeters,
                    ClampTopToTopo = !UseFlatBoundaryTop
                };

                // Achse definieren (für Querprofil-Modus)
                var axis = AskAxis(uiDoc);
                if (!axis.HasValue)
                    return;

                // Querprofile entlang Achse -> Keilmodelle
                var error = builder.BuildGroundModel_SectionsBRep(
                    doc,
                    Boreholes,
                    Layers,
                    boundary,
                    axis.Value.p0,
                    axis.Value.p1,
                    UseCodeAsLayerKey,
                    (int)BoundaryTopMode);
                if (!string.IsNullOrEmpty(error))
                    TaskDialog.Show("BIMassist", error);
                else
                    TaskDialog.Show("BIMassist", "3D-Bodenmodell erstellt.");
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

        private CurveLoop DrawBoundaryPolygon(UIDocument uidoc, Level onLevel)
        {
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
