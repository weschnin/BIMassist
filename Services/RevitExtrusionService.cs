using Autodesk.Revit.DB;
using BIMassist.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace BIMassist.Services
{
    public class RevitExtrusionService
    {
        private readonly bool _assumeMeters;
        private readonly bool _depthIsBelowGround;
        private readonly double _xOffsetMeters; // lokaler Offset in Meter (Easting-Anker)
        private readonly double _yOffsetMeters; // lokaler Offset in Meter (Northing-Anker)
        private const double DIAMETER_M = 0.30; // 30 cm

        public RevitExtrusionService(bool assumeMeters, bool depthIsBelowGround)
        { _assumeMeters = assumeMeters; _depthIsBelowGround = depthIsBelowGround; }

        public RevitExtrusionService(bool assumeMeters, bool depthIsBelowGround, double xOffsetMeters, double yOffsetMeters)
        { _assumeMeters = assumeMeters; _depthIsBelowGround = depthIsBelowGround; _xOffsetMeters = xOffsetMeters; _yOffsetMeters = yOffsetMeters; }

        private double ToInternalLength(double value)
        {
            if (_assumeMeters)
                return UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Meters);
            // Falls CSV bereits in Fuß – direkt weitergeben
            return value;
        }
        public string CreateExtrusions(Document doc, List<Borehole> holes, List<GeologyLayer> layers, double diameter_m, string materialNameColumn, bool useCodeAsLayerKey = false)
        {
            int created = 0; int skipped = 0;
            // Gleichnamige Schichten je Bohrung zusammenlegen (Code/Beschreibung je nach UI)
            var mergedLayers = MergeLayersPerBorehole(layers, useCodeAsLayerKey);

            using (var t = new Transaction(doc, "Aufschlussbohrungen"))
            {
                t.Start();

                var fops = t.GetFailureHandlingOptions();
                fops = fops.SetFailuresPreprocessor(new WarningSwallower())
                           .SetClearAfterRollback(true)
                           .SetDelayedMiniWarnings(true);
                t.SetFailureHandlingOptions(fops);

                // Gruppiere Schichten pro Bohrung
                mergedLayers = MergeLayersPerBorehole(layers, useCodeAsLayerKey);
                
                var byLoc = mergedLayers
                    .GroupBy(l => (l.LocationID ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DepthTop).ToList());

                foreach (var bh in holes)
                {
                    if (!byLoc.TryGetValue(bh.LocationID, out var list) || list.Count == 0)
                        continue; // keine Layer


                    // neu – lokale Koordinaten nahe am internen Ursprung bilden
                    double x, y;
                    if (_assumeMeters)
                    {
                        // CSV in Meter, Offset kommt in Meter ⇒ in Meter differenzieren, dann in Fuß
                        x = UnitUtils.ConvertToInternalUnits(bh.Easting - _xOffsetMeters, UnitTypeId.Meters);
                        y = UnitUtils.ConvertToInternalUnits(bh.Northing - _yOffsetMeters, UnitTypeId.Meters);
                    }
                    else
                    {
                        // CSV in Fuß (interne Längeneinheiten), Offset liegt in Meter ⇒ Offset zuerst in Fuß, dann differenzieren
                        double xOffFt = UnitUtils.ConvertToInternalUnits(_xOffsetMeters, UnitTypeId.Meters);
                        double yOffFt = UnitUtils.ConvertToInternalUnits(_yOffsetMeters, UnitTypeId.Meters);
                        x = bh.Easting - xOffFt;
                        y = bh.Northing - yOffFt;
                    }

                    foreach (var gl in list)
                    {
                        // Höhe: Oberkante und Unterkante der Schicht
                        double zTop, zBase;
                        if (_depthIsBelowGround)
                        {
                            zTop = bh.GroundLevel - gl.DepthTop;
                            zBase = bh.GroundLevel - gl.DepthBase;
                        }
                        else
                        {
                            // Falls CSV bereits absolute Z enthält (seltener) – hier direkt übernehmen
                            zTop = gl.DepthTop; zBase = gl.DepthBase;
                        }
                        double height = Math.Abs(zTop - zBase);
                        if (height <= 1e-6) { skipped++; continue; }


                        var centerTop = new XYZ(x, y, ToInternalLength(zTop));
                        var radius = ToInternalLength(diameter_m / 2.0);


                        // Kreis-Profil in Ebene Z = zTop (zwei Halbbögen für geschlossenen Loop)
                        var loop = new CurveLoop();
                        var arc1 = Arc.Create(centerTop, radius, 0, Math.PI, XYZ.BasisX, XYZ.BasisY);
                        var arc2 = Arc.Create(centerTop, radius, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY);
                        loop.Append(arc1); loop.Append(arc2);
                        var loops = new List<CurveLoop> { loop };


                        // Extrusion entlang -Z
                        var dir = -XYZ.BasisZ;
                        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, dir, ToInternalLength(height));


                        var catId = new ElementId(BuiltInCategory.OST_GenericModel);
                        var ds = DirectShape.CreateElement(doc, catId);
                        ds.ApplicationId = "BIMassist";
                        ds.ApplicationDataId = Guid.NewGuid().ToString();
                        ds.SetShape(new List<GeometryObject> { solid });

                        // Materialname abhängig von den Layerdaten wählen
                        // (deine aktuelle Logik: zuerst Classification, dann GeologyCode, sonst Description)
                        string rawMatName = PickLayerName(gl, materialNameColumn);

                        // Material holen/erzeugen – NEU: erstellt immer mit Prefix "Bodenschicht - ",
                        // wenn kein passendes Material existiert. Alles wird vorher sanitizet.
                        var mat = FindOrCreateMaterial(doc, rawMatName, "Bodenschicht - ");

                        // dem DirectShape zuweisen (falls Parameter vorhanden)
                        var p = ds.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                        if (p != null && !p.IsReadOnly) p.Set(mat.Id);

                        // Optionale Parameter/Name
                        var rawDsName = $"{bh.LocationID} - {gl.Description}";
                        var safeDsName = SanitizeNameForRevit(rawDsName);
                        try { ds.Name = safeDsName; } catch { /* falls Template/Worksharing blockt */ }

                        created++;
                    }
                }

                t.Commit();
            }

            return $"Erstellt: {created} Extrusion(en). Übersprungen (0 Höhe): {skipped}.";
        }

        // Findet existierende Materialien (mit/ohne Prefix) – legt bei Bedarf
        // ein neues an, DANN immer mit Prefix "Bodenschicht - ".
        private Material FindOrCreateMaterial(Document doc, string rawName, string prefixForNew = "Bodenschicht - ")
        {
            var baseName = SanitizeNameForRevit(rawName);

            // vorhandene Materialien laden (einmal)
            var allMats = new FilteredElementCollector(doc)
                .OfClass(typeof(Material)).Cast<Material>().ToList();

            // 1) Ohne Prefix bereits vorhanden?
            var hit = allMats.FirstOrDefault(m => m.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            // 2) Mit Prefix bereits vorhanden?
            var prefixed = prefixForNew + baseName;
            hit = allMats.FirstOrDefault(m => m.Name.Equals(prefixed, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            // 3) Neu anlegen – IMMER mit Prefix
            var createName = SanitizeNameForRevit(prefixed);

            // Kollisionen durchnummerieren
            string candidate = createName; int i = 2;
            while (allMats.Any(m => m.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                candidate = $"{createName}_{i++}";

            var id = Material.Create(doc, candidate);
            return (Material)doc.GetElement(id);
        }

        // Erlaubt Buchstaben/Ziffern, Leerzeichen, Punkt, Unterstrich, Bindestrich.
        // Alles andere wird zu "_", Mehrfach-Unterstriche werden reduziert.
        // Länge sicherheitshalber begrenzen, leere Namen -> "Unbenannt".
        private static string SanitizeNameForRevit(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Unbenannt";
            var cleaned = Regex.Replace(s, @"[^\p{L}\p{Nd}\s._-]", "_"); // alles Nicht-Erlaubte -> "_"
            cleaned = Regex.Replace(cleaned, @"_+", "_");                 // mehrfach "_" -> "_"
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '_', '.'); // Whitespace normalisieren/trimmen
            if (string.IsNullOrEmpty(cleaned)) cleaned = "Unbenannt";
            if (cleaned.Length > 240) cleaned = cleaned.Substring(0, 240);
            return cleaned;
        }

        private static string NormalizeKey(GeologyLayer gl, bool useCodeKey)
        {
            var raw = useCodeKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
            raw = raw.Trim();
            if (string.IsNullOrEmpty(raw)) raw = "SCHICHT";
            // einheitlicher Key
            return System.Text.RegularExpressions.Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ");
        }

        private static List<GeologyLayer> MergeLayersPerBorehole(IList<GeologyLayer> input, bool useCodeKey)
        {
            if (input == null || input.Count == 0) return new List<GeologyLayer>();

            // Gruppierung: pro Bohrung (LocationID) und Schicht-Key (Code/Beschreibung)
            var groups = input.GroupBy(gl =>
            {
                var loc = (gl.LocationID ?? "").Trim().ToUpperInvariant();
                var key = NormalizeKey(gl, useCodeKey);
                return loc + "|" + key;
            });

            var outList = new List<GeologyLayer>(groups.Count());
            foreach (var g in groups)
            {
                string locationId = null, desc = null, code = null;
                double minTop = double.MaxValue;
                double maxBaseAbs = double.MinValue;

                foreach (var gl in g)
                {
                    if (locationId == null) locationId = gl.LocationID;
                    if (desc == null) desc = gl.Description;
                    if (code == null) code = gl.GeologyCode;

                    // Base als ABSOLUTE Tiefe interpretieren:
                    // Falls DepthBase <= DepthTop => DepthBase war Mächtigkeit -> Top + Thickness
                    double topD = gl.DepthTop;
                    double baseD = gl.DepthBase <= gl.DepthTop ? (gl.DepthTop + gl.DepthBase) : gl.DepthBase;

                    if (topD < minTop) minTop = topD;
                    if (baseD > maxBaseAbs) maxBaseAbs = baseD;
                }

                outList.Add(new GeologyLayer
                {
                    LocationID = locationId,
                    Description = desc,
                    GeologyCode = code,
                    DepthTop = minTop,
                    DepthBase = maxBaseAbs // immer als ABSOLUTE Basis zurückgeben
                });
            }

            return outList;
        }

        private static string PickLayerName(GeologyLayer gl, string column)
        {
            string val = null;
            if (!string.IsNullOrWhiteSpace(column))
            {
                if (column.Equals("Classification", StringComparison.OrdinalIgnoreCase))
                    val = gl?.Classification;
                else if (column.Equals("GeologyCode", StringComparison.OrdinalIgnoreCase))
                    val = gl?.GeologyCode;
                else if (column.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    val = gl?.Description;
            }

            // Fallback, falls die gewählte Spalte leer ist:
            if (string.IsNullOrWhiteSpace(val))
                val = !string.IsNullOrWhiteSpace(gl?.Classification) ? gl.Classification
                    : !string.IsNullOrWhiteSpace(gl?.GeologyCode) ? gl.GeologyCode
                    : gl?.Description;

            if (string.IsNullOrWhiteSpace(val)) val = "SCHICHT";
            return val.Trim();
        }

    }

    class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}