namespace BIMassist.Services
{
    using Autodesk.Revit.DB;
    using BIMassist.Models;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    public partial class ToposolidLayerService
    {
        private readonly bool _depthIsBelowGround;
        private readonly double _minThicknessM;

        public ToposolidLayerService(bool depthIsBelowGround, double minThicknessMeters = 0.01)
        {
            _depthIsBelowGround = depthIsBelowGround;
            _minThicknessM = Math.Max(1e-4, minThicknessMeters);
        }

        /// <summary>
        /// Baut je Schicht EIN Toposolid mit variabler Dicke:
        /// - Unterkante auf Level (Ebene „Geländeunterkante“)
        /// - Oberfläche aus Höhenpunkten (Bohrpunkte + Randpunkte)
        /// </summary>
        public Dictionary<string, ElementId> BuildToposolidPerLayer_VariableThickness(
            Document doc,
            IList<Borehole> boreholes,
            IList<GeologyLayer> allLayers,
            Level levelBottom,
            ToposolidType baseType,
            CurveLoop userBoundary,
            bool useCodeAsLayerKey,
            int edgeTopMode /* 0=NearestBorehole, 1=FlatAtMean */)
        {

            // 0a) Gleichnamige Schichten je Bohrung zusammenlegen (analog Extrusionen)
            allLayers = MergeLayersPerBorehole(allLayers, useCodeAsLayerKey);

            if (boreholes == null || boreholes.Count < 3) throw new InvalidOperationException("Zu wenige Bohrungen.");
            if (allLayers == null || allLayers.Count == 0) throw new InvalidOperationException("Keine Schichten.");
            if (userBoundary == null) throw new ArgumentNullException(nameof(userBoundary));

            // Typ auf "eine Lage, variable Dicke" bringen
            EnsureVariableSingleLayerType(doc, baseType);

            var (E0, N0) = GetAnchor(doc, boreholes);

            // Boreholes per ID
            var bhById = new Dictionary<string, Borehole>(StringComparer.OrdinalIgnoreCase);
            foreach (var bh in boreholes)
            {
                if (bh == null) continue;
                var id = (bh.LocationID ?? "").Trim();
                if (id.Length > 0 && !bhById.ContainsKey(id)) bhById[id] = bh;
            }

            // Gruppierung nach UI-Wahl (Code/Beschreibung)
            string KeyFor(GeologyLayer gl)
            {
                var raw = useCodeAsLayerKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
                raw = raw.Trim(); if (string.IsNullOrEmpty(raw)) raw = "SCHICHT";
                return Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ");
            }
            var groups = allLayers.GroupBy(KeyFor)
                                  .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // zTop je Bohrung/Schicht
            var zTopByBhAndKey = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groups)
            {
                bool baseIsThickness = LooksLikeThickness(kv.Value);
                foreach (var gl in kv.Value)
                {
                    var lid = (gl.LocationID ?? "").Trim();
                    if (!bhById.TryGetValue(lid, out var bh)) continue;

                    double topD = gl.DepthTop;
                    double zGL = bh.GroundLevel;
                    double zTop = _depthIsBelowGround ? zGL - topD : zGL + topD;

                    if (!zTopByBhAndKey.TryGetValue(lid, out var dict))
                    {
                        dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        zTopByBhAndKey[lid] = dict;
                    }
                    dict[kv.Key] = zTop;
                }
            }

            var boundaryCorners_m = BoundaryVerticesMeters(userBoundary);
            double zLevel_m = UnitUtils.ConvertFromInternalUnits(levelBottom.Elevation, UnitTypeId.Meters);

            var result = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

            using (var tx = new Transaction(doc, "AWESBox – Toposolid je Schicht (variable Dicke)"))
            {
                tx.Start();

                foreach (var kv in groups)
                {
                    string key = kv.Key;

                    // Bohrpunkte innerhalb Boundary (OK der Schicht)
                    var heightPts_m = new List<XYZ>();
                    foreach (var lid in zTopByBhAndKey.Keys)
                    {
                        if (!zTopByBhAndKey[lid].TryGetValue(key, out var zTop)) continue;

                        var bh = bhById[lid];
                        double x = bh.Easting - E0, y = bh.Northing - N0;

                        var pInt = ToInt(new XYZ(x, y, levelBottom.Elevation)); // 2D-Test
                        if (PointInCurveLoop2D(userBoundary, pInt))
                            heightPts_m.Add(new XYZ(x, y, zTop));
                    }

                    if (heightPts_m.Count == 0) continue;

                    // Randpunkte: OK aus nächstgelegenem Bohrpunkt ODER flach (Mittelwert)
                    if (edgeTopMode == 0) // NearestBorehole
                    {
                        foreach (var v in boundaryCorners_m)
                        {
                            double best = double.MaxValue, zBest = heightPts_m[0].Z;
                            foreach (var hp in heightPts_m)
                            {
                                double dx = hp.X - v.X, dy = hp.Y - v.Y;
                                double d2 = dx * dx + dy * dy;
                                if (d2 < best) { best = d2; zBest = hp.Z; }
                            }
                            heightPts_m.Add(new XYZ(v.X, v.Y, zBest));
                        }
                    }
                    else // FlatAtMean
                    {
                        double meanZ = heightPts_m.Average(p => p.Z);
                        foreach (var v in boundaryCorners_m)
                            heightPts_m.Add(new XYZ(v.X, v.Y, meanZ));
                    }

                    // Mindestmächtigkeit grob absichern (OK darf nicht unter Level fallen)
                    for (int i = 0; i < heightPts_m.Count; i++)
                    {
                        var p = heightPts_m[i];
                        if (p.Z < zLevel_m + _minThicknessM)
                            heightPts_m[i] = new XYZ(p.X, p.Y, zLevel_m + _minThicknessM);
                    }

                    // in interne Einheiten umrechnen
                    var heightPts_int = heightPts_m.Select(p => ToInt(p)).ToList();
                    Toposolid topo = null;
                    try
                    {
                        // EIN Toposolid: Unterkante = Level, Oberfläche = heightPts
                        topo = CreateToposolidFlexible(doc,
                        new List<CurveLoop> { userBoundary }, heightPts_int, levelBottom.Id, baseType.Id);
                    }
                    catch
                    {
                        continue;
                    }
                    if (topo != null) result[key] = topo.Id;
                }

                tx.Commit();
            }

            return result;
        }

        public Dictionary<string, ElementId> BuildToposolidPerLayer_AlongAxis(
            Document doc,
            IList<Borehole> boreholes,
            IList<GeologyLayer> allLayers,
            Level levelBottom,
            ToposolidType baseType,
            CurveLoop userBoundary,
            XYZ axisP0Int, XYZ axisP1Int,
            bool useCodeAsLayerKey,
            bool allowZeroThickness)
        {
            if (boreholes == null || boreholes.Count < 3) throw new InvalidOperationException("Zu wenige Bohrungen.");
            if (allLayers == null || allLayers.Count == 0) throw new InvalidOperationException("Keine Schichten.");
            if (userBoundary == null) throw new ArgumentNullException(nameof(userBoundary));

            EnsureVariableSingleLayerType(doc, baseType); // bestehender Helper

            var (E0, N0) = GetAnchor(doc, boreholes);
            var boundaryCorners_m = BoundaryVerticesMeters(userBoundary);
            if (boundaryCorners_m == null || boundaryCorners_m.Count < 3) throw new InvalidOperationException("Ungültige Boundary.");

            double zLevel_m = UnitUtils.ConvertFromInternalUnits(levelBottom.Elevation, UnitTypeId.Meters);

            // Achse in lokale Meterkoordinaten umrechnen
            XYZ axisP0_m = new XYZ(
                UnitUtils.ConvertFromInternalUnits(axisP0Int.X, UnitTypeId.Meters) - E0,
                UnitUtils.ConvertFromInternalUnits(axisP0Int.Y, UnitTypeId.Meters) - N0, 0);
            XYZ axisP1_m = new XYZ(
                UnitUtils.ConvertFromInternalUnits(axisP1Int.X, UnitTypeId.Meters) - E0,
                UnitUtils.ConvertFromInternalUnits(axisP1Int.Y, UnitTypeId.Meters) - N0, 0);

            XYZ dir = axisP1_m - axisP0_m;
            double len = Math.Max(1e-9, Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y));
            dir = new XYZ(dir.X / len, dir.Y / len, 0);
            XYZ perp = new XYZ(-dir.Y, dir.X, 0);

            // Boreholes per ID sammeln
            var bhById = new Dictionary<string, Borehole>(StringComparer.OrdinalIgnoreCase);
            foreach (var bh in boreholes)
            {
                if (bh == null) continue;
                var id = (bh.LocationID ?? "").Trim();
                if (id.Length > 0 && !bhById.ContainsKey(id)) bhById[id] = bh;
            }

            // Gruppierung (Code oder Beschreibung normalisiert)
            Func<GeologyLayer, string> keyFor = gl =>
            {
                var raw = useCodeAsLayerKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
                raw = (raw ?? "").Trim(); if (raw.Length == 0) raw = "SCHICHT";
                return System.Text.RegularExpressions.Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ");
            };
            var groups = allLayers.GroupBy(keyFor)
                                  .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);


            // PATCH: Reihenfolge der Schicht-Keys (oben -> unten) per typischem DepthTop (Median) bestimmen
            var orderTopDown = ComputeOrderTopDown(groups);
            // Bestimme pro Bohrung die oberste Schicht (kleinstes DepthTop) und merke ihren Key
            var topKeyPerBh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in allLayers.GroupBy(gl => (gl.LocationID ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
            {
                GeologyLayer top = null;
                foreach (var gl in grp)
                {
                    if (top == null || gl.DepthTop < top.DepthTop) top = gl;
                }
                if (top != null)
                {
                    var k = keyFor(top); // dieselbe Key-Funktion wie bisher
                    topKeyPerBh[grp.Key] = k;
                }
            }

            // zTop je Bohrung/Schichtschlüssel
            var zTopByBhAndKey = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groups)
            {
                foreach (var gl in kv.Value)
                {
                    var lid = (gl.LocationID ?? "").Trim();
                    Borehole bh;
                    if (!bhById.TryGetValue(lid, out bh)) continue;

                    double topD = gl.DepthTop;
                    double zGL = bh.GroundLevel;
                    double zTop = _depthIsBelowGround ? zGL - topD : zGL + topD;

                    Dictionary<string, double> dictZ;
                    if (!zTopByBhAndKey.TryGetValue(lid, out dictZ))
                    {
                        dictZ = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        zTopByBhAndKey[lid] = dictZ;
                    }
                    dictZ[kv.Key] = zTop;
                }
            }

            // Stationen entlang der Achse (nur Bohrungen innerhalb der Boundary)
            var stations = new List<Tuple<double, string, double, double>>(); // (s, lid, x, y)
            foreach (var kv in bhById)
            {
                var bh = kv.Value;
                double x = bh.Easting - E0, y = bh.Northing - N0;

                var pInt = ToInt(new XYZ(x, y, levelBottom.Elevation));
                if (!PointInCurveLoop2D(userBoundary, pInt)) continue;

                double s = (x - axisP0_m.X) * dir.X + (y - axisP0_m.Y) * dir.Y;
                stations.Add(Tuple.Create(s, kv.Key, x, y));
            }
            stations.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            if (stations.Count == 0) throw new InvalidOperationException("Keine Bohrungen entlang der Achse innerhalb der Boundary.");

            var result = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

            using (var tx = new Transaction(doc, "AWESBox – Toposolid je Schicht (Achse/Querschnitte)"))
            {
                tx.Start();

                foreach (var kv in groups)
                {
                    string layerKey = kv.Key;
                    var heightPts_m = new List<XYZ>();
                    var stationZ = new List<Tuple<double, double>>(); // (s, z) je Station für diese Schicht

                    for (int i = 0; i < stations.Count; i++)
                    {
                        var st = stations[i];
                        const double T = 1e6;
                        XYZ s0 = new XYZ(st.Item3 - perp.X * T, st.Item4 - perp.Y * T, 0);
                        XYZ s1 = new XYZ(st.Item3 + perp.X * T, st.Item4 + perp.Y * T, 0);

                        var sec = IntersectInfiniteLineWithPolygon(s0, s1, boundaryCorners_m); // Klassenhelper (s. unten)
                        if (sec.Count < 2) continue;

                        // Z an der Station bestimmen
                        double zStation;
                        string topKey;
                        Dictionary<string, double> dict;
                        double zTop;
                        if (topKeyPerBh.TryGetValue(st.Item2, out topKey) &&
                            string.Equals(topKey, layerKey, StringComparison.OrdinalIgnoreCase))
                        {
                            var bhTop = bhById[st.Item2];
                            zStation = bhTop.GroundLevel; // exakt Gelände
                        }
                        else if (zTopByBhAndKey.TryGetValue(st.Item2, out dict) && dict.TryGetValue(layerKey, out zTop))
                        {
                            zStation = zTop; // normale Schicht-OK aus den Daten
                        }

                        else
                        {
                            // PATCH: Schicht fehlt an dieser Station -> OK auf höchste OK aller tieferen Schichten an DIESER Station setzen,
                            // damit die UNTERKANTE bis zum nächsten Profil hochläuft und die Mächtigkeit dort auf 0 geht.
                            double baseZ = MaxLowerZAtStation(st.Item2, layerKey, orderTopDown, zTopByBhAndKey, zLevel_m);
                            zStation = baseZ; // kein vorzeitiges Auslaufen auf Level
                        }


                        stationZ.Add(Tuple.Create(st.Item1, zStation));

                        // Dreier-Punkte quer: links – Mitte (Bohrung) – rechts
                        AddHeightUniqueXY(heightPts_m, new XYZ(sec[0].X, sec[0].Y, zStation));
                        AddHeightUniqueXY(heightPts_m, new XYZ(st.Item3, st.Item4, zStation));
                        AddHeightUniqueXY(heightPts_m, new XYZ(sec[1].X, sec[1].Y, zStation));
                    }

                    if (heightPts_m.Count == 0) continue;

                    // Boundary-Punkte zweiseitig an Querprofile anpassen (Interpolation/Clamping entlang der Achse)
                    if (stationZ.Count >= 1)
                    {
                        stationZ.Sort((a, b) => a.Item1.CompareTo(b.Item1)); // sort by s
                        for (int i = 0; i < boundaryCorners_m.Count; i++)
                        {
                            var vtx = boundaryCorners_m[i];
                            double sv = (vtx.X - axisP0_m.X) * dir.X + (vtx.Y - axisP0_m.Y) * dir.Y;
                            double zv = InterpZ_NoEarlyRunout(sv, stationZ, zLevel_m); // kein vorzeitiges Auslaufen
                            AddHeightUniqueXY(heightPts_m, new XYZ(vtx.X, vtx.Y, zv));
                        }
                    }

                    double epsZero = allowZeroThickness ? 1e-5 : _minThicknessM;
                    for (int i = 0; i < heightPts_m.Count; i++)
                    {
                        var p = heightPts_m[i];
                        if (p.Z < zLevel_m + epsZero)
                            heightPts_m[i] = new XYZ(p.X, p.Y, zLevel_m + epsZero);
                    }

                    var heightPts_int = heightPts_m.Select(p => ToInt(p)).ToList();
                    Toposolid topo = null;
                    try
                    {
                        topo = CreateToposolidFlexible(doc, new List<CurveLoop> { userBoundary }, heightPts_int, levelBottom.Id, baseType.Id);
                    }
                    catch { }
                    if (topo != null) result[layerKey] = topo.Id;
                }

                tx.Commit();
            }

            return result;
        }

        public Dictionary<string, ElementId> BuildBaseSurfacePerLayer_AlongAxis(
    Document doc,
    IList<Borehole> boreholes,
    IList<GeologyLayer> allLayers,
    Level levelBottom,
    ToposolidType baseType,
    CurveLoop userBoundary,
    XYZ axisP0Int, XYZ axisP1Int,
    bool useCodeAsLayerKey)
        {
            if (boreholes == null || boreholes.Count < 3) throw new InvalidOperationException("Zu wenige Bohrungen.");
            if (allLayers == null || allLayers.Count == 0) throw new InvalidOperationException("Keine Schichten.");
            if (userBoundary == null) throw new ArgumentNullException(nameof(userBoundary));

            EnsureVariableSingleLayerType(doc, baseType);

            var (E0, N0) = GetAnchor(doc, boreholes);
            var boundaryCorners_m = BoundaryVerticesMeters(userBoundary);
            if (boundaryCorners_m == null || boundaryCorners_m.Count < 3) throw new InvalidOperationException("Ungültige Boundary.");

            double zLevel_m = UnitUtils.ConvertFromInternalUnits(levelBottom.Elevation, UnitTypeId.Meters);

            // Achse in Meter
            XYZ axisP0_m = new XYZ(
                UnitUtils.ConvertFromInternalUnits(axisP0Int.X, UnitTypeId.Meters) - E0,
                UnitUtils.ConvertFromInternalUnits(axisP0Int.Y, UnitTypeId.Meters) - N0, 0);
            XYZ axisP1_m = new XYZ(
                UnitUtils.ConvertFromInternalUnits(axisP1Int.X, UnitTypeId.Meters) - E0,
                UnitUtils.ConvertFromInternalUnits(axisP1Int.Y, UnitTypeId.Meters) - N0, 0);
            XYZ dir = axisP1_m - axisP0_m;
            double len = Math.Max(1e-9, Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y));
            dir = new XYZ(dir.X / len, dir.Y / len, 0);
            XYZ perp = new XYZ(-dir.Y, dir.X, 0);

            // Bohrungen per ID
            var bhById = new Dictionary<string, Borehole>(StringComparer.OrdinalIgnoreCase);
            foreach (var bh in boreholes)
            {
                if (bh == null) continue;
                var id = (bh.LocationID ?? "").Trim();
                if (id.Length > 0 && !bhById.ContainsKey(id)) bhById[id] = bh;
            }

            // PATCH: Key-Funktion exakt wie in AlongAxis (deine aktuelle Version verwenden!)
            Func<GeologyLayer, string> keyFor = gl =>
            {
                var raw = useCodeAsLayerKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
                raw = (raw ?? "").Trim(); if (raw.Length == 0) raw = "SCHICHT";
                return System.Text.RegularExpressions.Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ");
            };
            var groups = allLayers.GroupBy(keyFor)
                                  .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // zBase je Bohrung/Schichtschlüssel (an DepthBase)
            var zBaseByBhAndKey = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groups)
            {
                foreach (var gl in kv.Value)
                {
                    var lid = (gl.LocationID ?? "").Trim();
                    Borehole bh;
                    if (!bhById.TryGetValue(lid, out bh)) continue;

                    // DepthBase => absolute Z der Basissurface
                    double baseD = gl.DepthBase;
                    double zGL = bh.GroundLevel;
                    double zBase = _depthIsBelowGround ? zGL - baseD : zGL + baseD;

                    Dictionary<string, double> dictZ;
                    if (!zBaseByBhAndKey.TryGetValue(lid, out dictZ))
                    {
                        dictZ = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        zBaseByBhAndKey[lid] = dictZ;
                    }
                    dictZ[kv.Key] = zBase;
                }
            }

            // Stationen entlang Achse (Bohrungen in Boundary)
            var stations = new List<Tuple<double, string, double, double>>(); // (s, lid, x, y)
            foreach (var kv in bhById)
            {
                var bh = kv.Value;
                double x = bh.Easting - E0, y = bh.Northing - N0;
                var pInt = ToInt(new XYZ(x, y, levelBottom.Elevation));
                if (!PointInCurveLoop2D(userBoundary, pInt)) continue;

                double s = (x - axisP0_m.X) * dir.X + (y - axisP0_m.Y) * dir.Y;
                stations.Add(Tuple.Create(s, kv.Key, x, y));
            }
            stations.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            if (stations.Count == 0) throw new InvalidOperationException("Keine Bohrungen entlang der Achse innerhalb der Boundary.");

            var result = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

            using (var tx = new Transaction(doc, "AWESBox – Basissurface je Schicht (Achse)"))
            {
                tx.Start();

                foreach (var kv in groups)
                {
                    string layerKey = kv.Key;
                    var heightPts_m = new List<XYZ>();
                    var stationZ = new List<Tuple<double, double>>(); // (s, zBase) je Station

                    for (int i = 0; i < stations.Count; i++)
                    {
                        var st = stations[i];
                        const double T = 1e6;
                        XYZ s0 = new XYZ(st.Item3 - perp.X * T, st.Item4 - perp.Y * T, 0);
                        XYZ s1 = new XYZ(st.Item3 + perp.X * T, st.Item4 + perp.Y * T, 0);

                        var sec = IntersectInfiniteLineWithPolygon(s0, s1, boundaryCorners_m);
                        if (sec.Count < 2) continue;

                        double zBaseStation;
                        Dictionary<string, double> dict;
                        double zBase;
                        if (zBaseByBhAndKey.TryGetValue(st.Item2, out dict) && dict.TryGetValue(layerKey, out zBase))
                        {
                            zBaseStation = zBase; // Basishöhe vorhanden
                        }
                        else
                        {
                            // PATCH: Schicht fehlt hier -> Base = zLevel (kein zusätzlicher Schnitt an der Stelle)
                            zBaseStation = zLevel_m;
                        }

                        stationZ.Add(Tuple.Create(st.Item1, zBaseStation));

                        // Dreierpunkte quer (links – Mitte – rechts) auf zBaseStation
                        AddHeightUniqueXY(heightPts_m, new XYZ(sec[0].X, sec[0].Y, zBaseStation));
                        AddHeightUniqueXY(heightPts_m, new XYZ(st.Item3, st.Item4, zBaseStation));
                        AddHeightUniqueXY(heightPts_m, new XYZ(sec[1].X, sec[1].Y, zBaseStation));
                    }

                    if (heightPts_m.Count == 0) continue;

                    // Boundarypunkte: Interpolation entlang Achse – kein vorzeitiges Auslaufen
                    if (stationZ.Count >= 1)
                    {
                        stationZ.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                        for (int i = 0; i < boundaryCorners_m.Count; i++)
                        {
                            var vtx = boundaryCorners_m[i];
                            double sv = (vtx.X - axisP0_m.X) * dir.X + (vtx.Y - axisP0_m.Y) * dir.Y;
                            double zv = InterpZ_NoEarlyRunout(sv, stationZ, zLevel_m);
                            AddHeightUniqueXY(heightPts_m, new XYZ(vtx.X, vtx.Y, zv));
                        }
                    }

                    // OK darf nie UNTER Level fallen
                    for (int i = 0; i < heightPts_m.Count; i++)
                    {
                        var p = heightPts_m[i];
                        if (p.Z < zLevel_m) heightPts_m[i] = new XYZ(p.X, p.Y, zLevel_m);
                    }

                    var heightPts_int = heightPts_m.Select(p => ToInt(p)).ToList();
                    Toposolid topo = null;
                    try
                    {
                        topo = CreateToposolidFlexible(doc, new List<CurveLoop> { userBoundary }, heightPts_int, levelBottom.Id, baseType.Id);
                    }
                    catch { }
                    if (topo != null) result[layerKey] = topo.Id;
                }

                tx.Commit();
            }

            return result;
        }

        // PATCH-HELPER: Schichtreihenfolge bestimmen (oben -> unten) anhand Median(DepthTop)
        private static List<string> ComputeOrderTopDown(Dictionary<string, List<GeologyLayer>> groups)
        {
            var pairs = new List<Tuple<string, double>>();
            foreach (var kv in groups)
            {
                var list = kv.Value.Select(gl => gl.DepthTop).OrderBy(d => d).ToList();
                if (list.Count == 0) { pairs.Add(Tuple.Create(kv.Key, double.MaxValue)); continue; }
                double med;
                int n = list.Count;
                if (n % 2 == 1) med = list[n / 2];
                else med = 0.5 * (list[n / 2 - 1] + list[n / 2]);
                pairs.Add(Tuple.Create(kv.Key, med));
            }
            // kleiner DepthTop => weiter oben
            pairs.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return pairs.Select(t => t.Item1).ToList();
        }

        // PATCH-HELPER: Höchste OK aller tieferen Schichten an einer Station (Bohrloch) ermitteln
        private static double MaxLowerZAtStation(
            string boreholeId,
            string layerKey,
            List<string> orderTopDown,
            Dictionary<string, Dictionary<string, double>> zTopByBhAndKey,
            double zLevel_m)
        {
            double baseZ = zLevel_m;
            int idx = -1;
            for (int i = 0; i < orderTopDown.Count; i++)
            {
                if (string.Equals(orderTopDown[i], layerKey, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            }
            if (idx >= 0)
            {
                for (int j = idx + 1; j < orderTopDown.Count; j++)
                {
                    var lowerKey = orderTopDown[j];
                    Dictionary<string, double> dict;
                    double zLower;
                    if (zTopByBhAndKey.TryGetValue(boreholeId, out dict) && dict != null
                        && dict.TryGetValue(lowerKey, out zLower))
                    {
                        if (zLower > baseZ) baseZ = zLower;
                    }
                }
            }
            return baseZ;
        }
        // ---------- Helper ----------

        /// <summary>
        /// Interpolation entlang der Achse ohne vorzeitiges Auslaufen:
        /// - Präsenz->Absenz (zA > Level, zB ~ Level): bis zum nächsten Profil konstant zA; erst am Profil fällt es auf 0.
        /// - Absenz->Präsenz (zA ~ Level, zB > Level): linearer Anstieg von 0 auf zB.
        /// - Sonst: normale lineare Interpolation.
        /// </summary>
        private static double InterpZ_NoEarlyRunout(double s, List<Tuple<double, double>> samples, double zLevel)
        {
            if (samples == null || samples.Count == 0) return zLevel;
            if (samples.Count == 1) return samples[0].Item2;

            // Randfälle: außerhalb des Stationsbereichs
            if (s <= samples[0].Item1) return samples[0].Item2;
            if (s >= samples[samples.Count - 1].Item1) return samples[samples.Count - 1].Item2;

            const double eps = 1e-6;

            for (int i = 0; i < samples.Count - 1; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];
                if (s >= a.Item1 && s <= b.Item1)
                {
                    // exakt am rechten Profil
                    if (Math.Abs(s - b.Item1) < 1e-9) return b.Item2;

                    double zA = a.Item2, zB = b.Item2;
                    bool aPresent = zA > zLevel + eps;
                    bool bPresent = zB > zLevel + eps;

                    if (aPresent && !bPresent)
                    {
                        // Bis zum nächsten Profil nicht vorzeitig auslaufen: konstant zA
                        return zA;
                    }
                    else if (!aPresent && bPresent)
                    {
                        // Von 0 auf zB ansteigen (linear)
                        double t = (Math.Abs(b.Item1 - a.Item1) < 1e-12) ? 0.0 : (s - a.Item1) / (b.Item1 - a.Item1);
                        return zA * (1.0 - t) + zB * t;
                    }
                    else
                    {
                        // Normal linear
                        double t = (Math.Abs(b.Item1 - a.Item1) < 1e-12) ? 0.0 : (s - a.Item1) / (b.Item1 - a.Item1);
                        return zA * (1.0 - t) + zB * t;
                    }
                }
            }
            return samples[samples.Count - 1].Item2; // Fallback
        }

        // Linie (a-b, unendlich) gegen Polygon (2D, Meter) schneiden -> äußerste 2 Schnittpunkte
        private static List<XYZ> IntersectInfiniteLineWithPolygon(XYZ a, XYZ b, List<XYZ> poly)
        {
            var hits = new List<Tuple<double, XYZ>>();
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = poly[i];
                var p1 = poly[(i + 1) % n];
                double t; XYZ p;
                if (TryIntersectLineSeg(a, b, p0, p1, out t, out p))
                    hits.Add(Tuple.Create(t, p));
            }
            hits.Sort((u, v) => u.Item1.CompareTo(v.Item1));
            var outPts = new List<XYZ>();
            if (hits.Count >= 2)
            {
                outPts.Add(hits[0].Item2);
                outPts.Add(hits[hits.Count - 1].Item2);
            }
            return outPts;
        }

        private static bool TryIntersectLineSeg(XYZ a, XYZ b, XYZ c, XYZ d, out double t, out XYZ p)
        {
            double ax = a.X, ay = a.Y, bx = b.X, by = b.Y;
            double cx = c.X, cy = c.Y, dx = d.X, dy = d.Y;
            double rdx = bx - ax, rdy = by - ay;
            double sdx = dx - cx, sdy = dy - cy;

            double det = rdx * (-sdy) - rdy * (-sdx);
            if (Math.Abs(det) < 1e-12) { t = 0; p = XYZ.Zero; return false; }

            double rhsx = cx - ax, rhsy = cy - ay;
            t = (rhsx * (-sdy) - rhsy * (-sdx)) / det;
            double u = (rdx * rhsy - rdy * rhsx) / det;
            if (u < -1e-9 || u > 1 + 1e-9) { p = XYZ.Zero; return false; }

            p = new XYZ(ax + t * rdx, ay + t * rdy, 0);
            return true;
        }

        private static double InterpZ_Clamped(double s, List<Tuple<double, double>> samples)
        {
            if (samples == null || samples.Count == 0) return 0.0;
            if (samples.Count == 1) return samples[0].Item2;

            if (s <= samples[0].Item1) return samples[0].Item2;
            if (s >= samples[samples.Count - 1].Item1) return samples[samples.Count - 1].Item2;

            for (int i = 0; i < samples.Count - 1; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];
                if (s >= a.Item1 && s <= b.Item1)
                {
                    double t = (Math.Abs(b.Item1 - a.Item1) < 1e-12) ? 0.0 : (s - a.Item1) / (b.Item1 - a.Item1);
                    return a.Item2 * (1.0 - t) + b.Item2 * t;
                }
            }
            return samples[samples.Count - 1].Item2; // fallback
        }

        private static void AddHeightUniqueXY(List<XYZ> list, XYZ p)
        {
            const double tol = 1e-6; // Meter, XY-Vergleich
            for (int i = 0; i < list.Count; i++)
            {
                var q = list[i];
                if (Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y) < tol)
                {
                    // bereits ein Punkt an derselben XY-Position vorhanden -> ersetze durch den „richtigen“ Z
                    list[i] = new XYZ(q.X, q.Y, p.Z);
                    return;
                }
            }
            list.Add(p);
        }

        private static string NormalizeKey(GeologyLayer gl, bool useCodeKey)
        {
            var raw = useCodeKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
            raw = raw.Trim();
            if (string.IsNullOrEmpty(raw)) raw = "SCHICHT";
            return System.Text.RegularExpressions.Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ");
        }

        private static List<GeologyLayer> MergeLayersPerBorehole(IList<GeologyLayer> input, bool useCodeKey)
        {
            if (input == null || input.Count == 0) return new List<GeologyLayer>();
            var groups = input.GroupBy(gl =>
            {
                var loc = (gl.LocationID ?? "").Trim().ToUpperInvariant();
                var key = NormalizeKey(gl, useCodeKey);
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

        private static (double E, double N) GetAnchor(Document doc, IList<Borehole> holes)
        {
            var pl = doc.ActiveProjectLocation; var pp = pl?.GetProjectPosition(XYZ.Zero);
            if (pp != null && (Math.Abs(pp.EastWest) > 1e-6 || Math.Abs(pp.NorthSouth) > 1e-6))
                return (UnitUtils.ConvertFromInternalUnits(pp.EastWest, UnitTypeId.Meters),
                        UnitUtils.ConvertFromInternalUnits(pp.NorthSouth, UnitTypeId.Meters));
            var f = holes.First(); return (f.Easting, f.Northing);
        }

        private static bool LooksLikeThickness(List<GeologyLayer> layers)
        {
            if (layers == null || layers.Count < 3) return false;
            int nonInc = layers.Count(l => l.DepthBase <= l.DepthTop + 1e-6);
            return nonInc > (layers.Count - nonInc);
        }

        private static bool PointInCurveLoop2D(CurveLoop loop, XYZ pInt)
        {
            var pts = new List<XYZ>();
            foreach (var c in loop) pts.Add(c.GetEndPoint(0));
            if (pts.Count == 0) return false;
            pts.Add(pts[0]);

            int wn = 0;
            double x = pInt.X, y = pInt.Y;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                bool up = a.Y <= y && b.Y > y;
                bool down = a.Y > y && b.Y <= y;
                if (up || down)
                {
                    double xint = (y - a.Y) * (b.X - a.X) / (b.Y - a.Y + 1e-12) + a.X;
                    if (xint > x) wn += up ? 1 : -1;
                }
            }
            return wn != 0;
        }

        private static List<XYZ> BoundaryVerticesMeters(CurveLoop boundaryInt)
        {
            var outList = new List<XYZ>();
            foreach (var c in boundaryInt)
            {
                var p = c.GetEndPoint(0);
                outList.Add(new XYZ(
                    UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(p.Z, UnitTypeId.Meters)
                ));
            }
            return outList;
        }

        private static XYZ ToInt(XYZ m) => new XYZ(
            UnitUtils.ConvertToInternalUnits(m.X, UnitTypeId.Meters),
            UnitUtils.ConvertToInternalUnits(m.Y, UnitTypeId.Meters),
            UnitUtils.ConvertToInternalUnits(m.Z, UnitTypeId.Meters));

        private static Toposolid CreateToposolidFlexible(Document doc, IList<CurveLoop> loops, IList<XYZ> points,
                                                         ElementId levelId, ElementId typeId)
        {
            try { return Toposolid.Create(doc, loops, points, typeId, levelId); }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            { return Toposolid.Create(doc, loops, points, levelId, typeId); }
        }

        /// <summary>Stellt sicher: genau eine Lage und diese Lage "variable".</summary>
        private static void EnsureVariableSingleLayerType(Document doc, ToposolidType typed)
        {
            var cs = typed.GetCompoundStructure();
            if (cs == null || cs.LayerCount == 0) return;

            // dickste Lage als variable wählen
            int idx = 0;
            double maxW = cs.GetLayerWidth(0);
            for (int i = 1; i < cs.LayerCount; ++i)
            {
                double w = cs.GetLayerWidth(i);
                if (w > maxW) { maxW = w; idx = i; }
            }

            var t = cs.GetType();
            var prop = t.GetProperty("VariableLayerIndex");
            if (prop != null && prop.CanWrite) prop.SetValue(cs, idx);
            else
            {
                var mi = t.GetMethod("SetVariableLayerIndex");
                if (mi != null) mi.Invoke(cs, new object[] { idx });
            }

            typed.SetCompoundStructure(cs);
        }
    }
}
