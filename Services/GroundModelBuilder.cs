using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIMassist.Models;
using System.Text.RegularExpressions;

namespace BIMassist.Services
{
    /// <summary>
    /// Civil-3D-ähnlicher Bodenmodell-Builder:
    /// 1) Einheitliches Grid-TIN innerhalb der Topo-Boundary.
    /// 2) Je Horizont Z-Werte (Top/Base) an denselben Knoten per IDW (mit Distanz-Cutoff).
    /// 3) Volumen: Keile je Triangle (Top & Base teilen dieselbe TIN) → robust gegen „Internal error“.
    /// </summary>
    public class GroundModelBuilder
    {
        private readonly bool _assumeMeters;
        private readonly bool _depthIsBelowGround;

        // Tuning-Parameter
        public double GridStepMeters { get; set; } = 10.0;
        public double MaxInterpDistanceMeters { get; set; } = 50.0;
        public double MinThicknessMeters { get; set; } = 0.05;
        public bool ClampTopToTopo { get; set; } = true;

        public GroundModelBuilder(bool assumeMeters, bool depthIsBelowGround)
        {
            _assumeMeters = assumeMeters;
            _depthIsBelowGround = depthIsBelowGround;
        }
        // ===== Querprofil-Datenstruktur (für BRep-Variante) =====
        private class Section
        {
            public string Kind;   // "boundary" | "bore"
            public double T;
            public XY L;
            public XY R;
            public double GL;
            public string BoreId;
        }


        // =====================
        // PUNKTWOLKEN-MODUS
        // =====================
        public string BuildSamplesOnly(
            Document doc,
            IList<Borehole> boreholes,
            IList<GeologyLayer> allLayers)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (boreholes == null || boreholes.Count < 3) return "Zu wenige Bohrungen (min. 3).";
            if (allLayers == null || allLayers.Count == 0) return "Keine Schichten vorhanden.";

            // 0) Anker nahe Ursprung (wie gehabt)
            var pl = doc.ActiveProjectLocation;
            var pp = pl?.GetProjectPosition(XYZ.Zero);
            double anchorE_m = 0, anchorN_m = 0;
            bool hasProj = false;
            if (pp != null)
            {
                anchorE_m = UnitUtils.ConvertFromInternalUnits(pp.EastWest, UnitTypeId.Meters);
                anchorN_m = UnitUtils.ConvertFromInternalUnits(pp.NorthSouth, UnitTypeId.Meters);
                hasProj = Math.Abs(anchorE_m) > 1e-3 || Math.Abs(anchorN_m) > 1e-3;
            }
            var first = boreholes.FirstOrDefault(b => b != null);
            if (!hasProj && first != null) { anchorE_m = first.Easting; anchorN_m = first.Northing; }

            // 1) XY-Punkte (nur Boreholes) + robustes ID-Mapping
            var pts = new List<XY>();
            var ids = new List<string>();
            var bhById = new Dictionary<string, Borehole>(StringComparer.OrdinalIgnoreCase);
            foreach (var bh in boreholes)
            {
                if (bh == null) continue;
                var key = (bh.LocationID ?? "").Trim();
                if (key.Length == 0) continue;
                if (!bhById.ContainsKey(key))
                {
                    bhById[key] = bh;
                    pts.Add(new XY(bh.Easting - anchorE_m, bh.Northing - anchorN_m));
                    ids.Add(key);
                }
            }
            int n = pts.Count;
            if (n < 3) return "Zu wenige eindeutige Bohrungen nach ID-Bereinigung.";

            // 2) Delaunay-Triangulation nur auf diesen Messpunkten (kleines n)
            var tris = DelaunaySmall(pts); // List<Tri2D> mit Indizes in [0..n-1]
            if (tris.Count == 0)
            {
                // Fallback: trianguliere konvexe Hülle als Fächer zum Schwerpunkts-Punkt
                tris = FanTriangulateConvexHull(pts);
                if (tris.Count == 0) return "Triangulation fehlgeschlagen (evtl. Kollinearität).";
            }

            // 3) Schicht-Gruppen und Z-Werte
            var groups = allLayers.GroupBy(gl => NormalizeKey(gl, false)) // Annahme: useCodeAsLayerKey = false, passe an
                                  .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // zTop und zBase je Bohrung/Schicht
            var zTopByBhAndKey = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            var zBaseByBhAndKey = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groups)
            {
                foreach (var gl in kv.Value)
                {
                    var lid = (gl.LocationID ?? "").Trim();
                    if (!bhById.TryGetValue(lid, out var bh)) continue;

                    double topD = gl.DepthTop;
                    double baseD = gl.DepthBase > topD ? gl.DepthBase : topD + gl.DepthBase; // Handle Thickness
                    double zGL = bh.GroundLevel;
                    double zTop = _depthIsBelowGround ? zGL - topD : zGL + topD;
                    double zBase = _depthIsBelowGround ? zGL - baseD : zGL + baseD;

                    if (!zTopByBhAndKey.TryGetValue(lid, out var topDict))
                    {
                        topDict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        zTopByBhAndKey[lid] = topDict;
                    }
                    topDict[kv.Key] = zTop;

                    if (!zBaseByBhAndKey.TryGetValue(lid, out var baseDict))
                    {
                        baseDict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        zBaseByBhAndKey[lid] = baseDict;
                    }
                    baseDict[kv.Key] = zBase;
                }
            }

            // Baue Modelle für SamplesOnly
            int created = 0;
            using (var tx = new Transaction(doc, "AWESBox – Bodenmodell (SamplesOnly)"))
            {
                tx.Start();

                foreach (var kv in groups)
                {
                    var key = kv.Key;
                    if (!zTopByBhAndKey.Values.Any(d => d.ContainsKey(key)) || !zBaseByBhAndKey.Values.Any(d => d.ContainsKey(key))) continue;

                    // Material
                    var mat = CreateOrGetMaterial(doc, FirstNonEmpty(kv.Value, l => l.Description));

                    // Baue Keile
                    var tsb = new TessellatedShapeBuilder
                    {
                        Target = TessellatedShapeBuilderTarget.Solid,
                        Fallback = TessellatedShapeBuilderFallback.Abort,
                        GraphicsStyleId = ElementId.InvalidElementId
                    };
                    tsb.OpenConnectedFaceSet(true);

                    for (int i = 0; i < tris.Count; i++)
                    {
                        var tri = tris[i];
                        var lidA = ids[tri.A];
                        var lidB = ids[tri.B];
                        var lidC = ids[tri.C];

                        double zTopA = zTopByBhAndKey[lidA].TryGetValue(key, out var ta) ? ta : 0;
                        double zTopB = zTopByBhAndKey[lidB].TryGetValue(key, out var tb) ? tb : 0;
                        double zTopC = zTopByBhAndKey[lidC].TryGetValue(key, out var tc) ? tc : 0;

                        double zBaseA = zBaseByBhAndKey[lidA].TryGetValue(key, out var ba) ? ba : 0;
                        double zBaseB = zBaseByBhAndKey[lidB].TryGetValue(key, out var bb) ? bb : 0;
                        double zBaseC = zBaseByBhAndKey[lidC].TryGetValue(key, out var bc) ? bc : 0;

                        var topA = ToInt(pts[tri.A], zTopA);
                        var topB = ToInt(pts[tri.B], zTopB);
                        var topC = ToInt(pts[tri.C], zTopC);

                        var baseA = ToInt(pts[tri.A], zBaseA);
                        var baseB = ToInt(pts[tri.B], zBaseB);
                        var baseC = ToInt(pts[tri.C], zBaseC);

                        AddTri(tsb, new[] { topA, topB, topC }, mat.Id);
                        AddTri(tsb, new[] { baseA, baseC, baseB }, mat.Id); // Umgedreht für Normalen

                        AddQuadAsTwoTris(tsb, topA, topB, baseB, baseA, mat.Id);
                        AddQuadAsTwoTris(tsb, topB, topC, baseC, baseB, mat.Id);
                        AddQuadAsTwoTris(tsb, topC, topA, baseA, baseC, mat.Id);
                    }

                    tsb.CloseConnectedFaceSet();
                    tsb.Build();

                    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.SetShape(tsb.GetBuildResult().GetGeometricalObjects());
                    ds.Name = SanitizeNameForRevit(key);
                    created++;
                }

                tx.Commit();
            }

            return created > 0 ? null : "Keine Modelle erstellt (SamplesOnly).";
        }

        // =====================
        // GRID-MODUS
        // =====================
        public string BuildGroundModel(
            Document doc,
            IList<Borehole> boreholes,
            IList<GeologyLayer> allLayers,
            CurveLoop boundary,
            bool useCodeAsLayerKey)
        {
            if (boreholes == null || boreholes.Count < 3) return "Zu wenige Bohrungen (min. 3).";
            if (allLayers == null || allLayers.Count == 0) return "Keine Schichten vorhanden.";
            if (boundary == null) return "Keine Boundary definiert.";

            // 0) Anker
            var (anchorE_m, anchorN_m) = GetAnchor(doc, boreholes);

            // 1) Borehole-Punkte
            var bhPts = new List<XY>();
            var bhById = new Dictionary<string, Borehole>(StringComparer.OrdinalIgnoreCase);
            var ids = new List<string>();
            foreach (var bh in boreholes)
            {
                var key = (bh.LocationID ?? "").Trim();
                if (key.Length > 0 && !bhById.ContainsKey(key))
                {
                    bhById[key] = bh;
                    bhPts.Add(new XY(bh.Easting - anchorE_m, bh.Northing - anchorN_m));
                    ids.Add(key);
                }
            }
            if (bhPts.Count < 3) return "Zu wenige eindeutige Bohrungen.";

            // 2) Grid-TIN erstellen
            var tin = CreateGridTin(boundary, GridStepMeters);
            if (tin.Tris.Count == 0) return "Grid-Triangulation fehlgeschlagen.";

            // 3) Schicht-Gruppen
            var groups = allLayers.GroupBy(gl => NormalizeKey(gl, useCodeAsLayerKey))
                                  .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // 4) zTop und zBase pro Borehole-Index
            var zTopByGroup = new Dictionary<string, Dictionary<int, double>>(StringComparer.OrdinalIgnoreCase);
            var zBaseByGroup = new Dictionary<string, Dictionary<int, double>>(StringComparer.OrdinalIgnoreCase);

            int bhIndex = 0;
            foreach (var bh in bhById.Values)
            {
                var layersForBh = allLayers.Where(l => string.Equals(l.LocationID, bh.LocationID, StringComparison.OrdinalIgnoreCase))
                                           .OrderBy(l => l.DepthTop).ToList();
                double prevBase = 0;
                foreach (var gl in layersForBh)
                {
                    var key = NormalizeKey(gl, useCodeAsLayerKey);
                    double topD = gl.DepthTop;
                    double baseD = gl.DepthBase > topD ? gl.DepthBase : topD + gl.DepthBase;

                    double zTop = _depthIsBelowGround ? bh.GroundLevel - topD : bh.GroundLevel + topD;
                    double zBase = _depthIsBelowGround ? bh.GroundLevel - baseD : bh.GroundLevel + baseD;

                    // Clamp Dicke
                    if (Math.Abs(zTop - zBase) < MinThicknessMeters)
                    {
                        zBase = zTop - MinThicknessMeters; // Annahme abwärts
                    }

                    if (!zTopByGroup.TryGetValue(key, out var topDict))
                    {
                        topDict = new Dictionary<int, double>();
                        zTopByGroup[key] = topDict;
                    }
                    topDict[bhIndex] = zTop;

                    if (!zBaseByGroup.TryGetValue(key, out var baseDict))
                    {
                        baseDict = new Dictionary<int, double>();
                        zBaseByGroup[key] = baseDict;
                    }
                    baseDict[bhIndex] = zBase;

                    prevBase = baseD;
                }
                bhIndex++;
            }

            // 5) Interpolation und Modellbau
            int created = 0;
            using (var tx = new Transaction(doc, "AWESBox – 3D Bodenmodell (Grid)"))
            {
                tx.Start();

                foreach (var kv in groups)
                {
                    var key = kv.Key;
                    if (!zTopByGroup.TryGetValue(key, out var tops) || !zBaseByGroup.TryGetValue(key, out var bases)) continue;

                    // Interpoliere auf Grid-Nodes
                    var gridZTops = new double[tin.Nodes.Count];
                    var gridZBases = new double[tin.Nodes.Count];
                    bool valid = true;
                    for (int i = 0; i < tin.Nodes.Count; i++)
                    {
                        var node = tin.Nodes[i];
                        gridZTops[i] = InterpolateIDW(node.X, node.Y, bhPts, tops, MaxInterpDistanceMeters);
                        gridZBases[i] = InterpolateIDW(node.X, node.Y, bhPts, bases, MaxInterpDistanceMeters);

                        if (double.IsNaN(gridZTops[i]) || double.IsNaN(gridZBases[i]))
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (!valid) continue;

                    // Material
                    var mat = CreateOrGetMaterial(doc, FirstNonEmpty(kv.Value, l => l.Description));

                    // Baue Keile
                    var tsb = new TessellatedShapeBuilder
                    {
                        Target = TessellatedShapeBuilderTarget.Solid,
                        Fallback = TessellatedShapeBuilderFallback.Abort,
                        GraphicsStyleId = ElementId.InvalidElementId
                    };
                    tsb.OpenConnectedFaceSet(true);

                    foreach (var tri in tin.Tris)
                    {
                        var topA = ToInt(tin.Nodes[tri.A], gridZTops[tri.A]);
                        var topB = ToInt(tin.Nodes[tri.B], gridZTops[tri.B]);
                        var topC = ToInt(tin.Nodes[tri.C], gridZTops[tri.C]);

                        var baseA = ToInt(tin.Nodes[tri.A], gridZBases[tri.A]);
                        var baseB = ToInt(tin.Nodes[tri.B], gridZBases[tri.B]);
                        var baseC = ToInt(tin.Nodes[tri.C], gridZBases[tri.C]);

                        AddTri(tsb, new[] { topA, topB, topC }, mat.Id);
                        AddTri(tsb, new[] { baseA, baseC, baseB }, mat.Id);

                        AddQuadAsTwoTris(tsb, topA, topB, baseB, baseA, mat.Id);
                        AddQuadAsTwoTris(tsb, topB, topC, baseC, baseB, mat.Id);
                        AddQuadAsTwoTris(tsb, topC, topA, baseA, baseC, mat.Id);
                    }

                    tsb.CloseConnectedFaceSet();
                    tsb.Build();

                    var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                    ds.SetShape(tsb.GetBuildResult().GetGeometricalObjects());
                    ds.Name = SanitizeNameForRevit(key);
                    created++;
                }

                tx.Commit();
            }

            return created > 0 ? null : "Keine Modelle erstellt. Hinweis: Falls die Boundary in einem anderen Koordinatensystem als die Bohrungen liegt, Boundary in gleicher Lage/Koordinatenbasis zeichnen oder Shared Coordinates/Projektlage abgleichen.";
        }

        // Hilfsfunktionen

        private static double DistM(XY a, XY b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Gerade (c + s*perp) schneidet Segment AB?
        private static bool LineSegIntersect(XY c, XYZ perp, XY a, XY b, out double s, out XY hit)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = perp.X, wy = perp.Y;
            double denom = wx * (-vy) - wy * (-vx);
            if (Math.Abs(denom) < 1e-12) { s = 0; hit = default(XY); return false; }
            double rhsx = a.X - c.X, rhsy = a.Y - c.Y;
            s = (rhsx * (-vy) - rhsy * (-vx)) / denom;
            double r = (wx * rhsy - wy * rhsx) / denom;
            if (r < -1e-9 || r > 1 + 1e-9) { hit = default(XY); return false; }
            hit = new XY(c.X + s * wx, c.Y + s * wy);
            return true;
        }

        // Punkt liegt (mit Toleranz) auf der Boundary?
        private static bool PointOnBoundary(XY p, List<XY> poly, double tol)
        {
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                if (DistancePointToSegment(p, a, b) <= tol) return true;
            }
            return false;
        }

        private static double DistancePointToSegment(XY p, XY a, XY b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;
            double c1 = vx * wx + vy * wy;
            if (c1 <= 0) return Math.Sqrt(wx * wx + wy * wy);
            double c2 = vx * vx + vy * vy;
            if (c2 <= c1) { double dx = p.X - b.X, dy = p.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }
            double t = c1 / c2;
            double px = a.X + t * vx, py = a.Y + t * vy;
            double dx2 = p.X - px, dy2 = p.Y - py;
            return Math.Sqrt(dx2 * dx2 + dy2 * dy2);
        }

        // --------- BRep-Keil zwischen zwei Querschnitten ----------
        private Solid BuildWedgeBRep(
            XYZ TL0, XYZ TR0, XYZ TR1, XYZ TL1,
            XYZ BL0, XYZ BR0, XYZ BR1, XYZ BL1)
        {
            double eps = 1e-9;
            if (TL0.DistanceTo(TR0) < eps || TR0.DistanceTo(TR1) < eps || TL1.DistanceTo(TL0) < eps) return null;
            if (BL0.DistanceTo(BR0) < eps || BR0.DistanceTo(BR1) < eps || BL1.DistanceTo(BL0) < eps) return null;

            var bb = new BRepBuilder(BRepType.Solid);

            XYZ[] P = new XYZ[] { TL0, TR0, TR1, TL1, BL0, BR0, BR1, BL1 };
            var edges = new Dictionary<string, BRepBuilderGeometryId>();

            Func<int, int, string> keyEdge = (a, b) => (a < b) ? (a + "_" + b) : (b + "_" + a);

            Func<int, int, BRepBuilderGeometryId> edge = (a, b) =>
            {
                string k = keyEdge(a, b);
                BRepBuilderGeometryId id;
                if (!edges.TryGetValue(k, out id))
                {
                    var eg = BRepBuilderEdgeGeometry.Create(Line.CreateBound(P[a], P[b]));
                    id = bb.AddEdge(eg); // Revit 2024-API: nur Geom übergeben
                    edges[k] = id;
                }
                return id;
            };

            Action<BRepBuilderGeometryId, int, int> addCo = (loop, a, b) =>
            {
                int s = Math.Min(a, b), t = Math.Max(a, b);
                bool reversed = !(a == s && b == t); // Edge in min->max angelegt
                bb.AddCoEdge(loop, edge(a, b), reversed);
            };

            var UV = new BoundingBoxUV(-1e6, -1e6, 1e6, 1e6);

            // Kanten einmalig anlegen
            edge(0, 1); edge(1, 2); edge(2, 3); edge(3, 0); // Top-Rand
            edge(4, 5); edge(5, 6); edge(6, 7); edge(7, 4); // Bottom-Rand
            edge(0, 4); edge(1, 5); edge(2, 6); edge(3, 7); // Vertikal
            edge(0, 2); edge(4, 6);                         // Diagonalen (für Top/Bottom-Triangulation)

            // TOP: zwei Dreiecke
            var fTop1 = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[0], P[1], P[2]), UV), true);
            var lTop1 = bb.AddLoop(fTop1);
            addCo(lTop1, 0, 1); addCo(lTop1, 1, 2); addCo(lTop1, 2, 0);
            bb.FinishLoop(lTop1); bb.FinishFace(fTop1);

            var fTop2 = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[0], P[2], P[3]), UV), true);
            var lTop2 = bb.AddLoop(fTop2);
            addCo(lTop2, 0, 2); addCo(lTop2, 2, 3); addCo(lTop2, 3, 0);
            bb.FinishLoop(lTop2); bb.FinishFace(fTop2);

            // BOTTOM: zwei Dreiecke (umgekehrt!)
            var fBot1 = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[4], P[5], P[6]), UV), false);
            var lBot1 = bb.AddLoop(fBot1);
            addCo(lBot1, 4, 5); addCo(lBot1, 5, 6); addCo(lBot1, 6, 4);
            bb.FinishLoop(lBot1); bb.FinishFace(fBot1);

            var fBot2 = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[4], P[6], P[7]), UV), false);
            var lBot2 = bb.AddLoop(fBot2);
            addCo(lBot2, 4, 6); addCo(lBot2, 6, 7); addCo(lBot2, 7, 4);
            bb.FinishLoop(lBot2); bb.FinishFace(fBot2);

            // Seiten/Front/Back (planare Quads)
            var fL = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[0], P[3], P[7]), UV), true);
            var lL = bb.AddLoop(fL);
            addCo(lL, 0, 3); addCo(lL, 3, 7); addCo(lL, 7, 4); addCo(lL, 4, 0);
            bb.FinishLoop(lL); bb.FinishFace(fL);

            var fR = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[1], P[2], P[6]), UV), true);
            var lR = bb.AddLoop(fR);
            addCo(lR, 1, 2); addCo(lR, 2, 6); addCo(lR, 6, 5); addCo(lR, 5, 1);
            bb.FinishLoop(lR); bb.FinishFace(fR);

            var fF = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[0], P[1], P[5]), UV), true);
            var lF = bb.AddLoop(fF);
            addCo(lF, 0, 1); addCo(lF, 1, 5); addCo(lF, 5, 4); addCo(lF, 4, 0);
            bb.FinishLoop(lF); bb.FinishFace(fF);

            var fB = bb.AddFace(BRepBuilderSurfaceGeometry.Create(Plane.CreateByThreePoints(P[3], P[2], P[6]), UV), true);
            var lB = bb.AddLoop(fB);
            addCo(lB, 3, 2); addCo(lB, 2, 6); addCo(lB, 6, 7); addCo(lB, 7, 3);
            bb.FinishLoop(lB); bb.FinishFace(fB);

            var outcome = bb.Finish();
            if (outcome != BRepBuilderOutcome.Success) return null;
            return bb.GetResult();
        }

        private Tin CreateGridTin(CurveLoop boundary, double stepM)
        {
            var boundaryM = BoundaryVerticesMeters(boundary);

            double minX = boundaryM.Min(p => p.X), maxX = boundaryM.Max(p => p.X);
            double minY = boundaryM.Min(p => p.Y), maxY = boundaryM.Max(p => p.Y);

            var nodes = new List<XY>();
            for (double x = minX; x <= maxX; x += stepM)
            {
                for (double y = minY; y <= maxY; y += stepM)
                {
                    var p = new XY(x, y, 0);
                    if (PointInPolygon(p, boundaryM))
                        nodes.Add(p);
                }
            }

            var tris = DelaunaySmall(nodes);
            if (tris.Count == 0) tris = FanTriangulateConvexHull(nodes);

            return new Tin { Nodes = nodes, Tris = tris };
        }

        private double InterpolateIDW(double x, double y, List<XY> bhPts, Dictionary<int, double> zValues, double maxDist)
        {
            // 0 => unbegrenzt
            if (maxDist <= 0) maxDist = double.PositiveInfinity;

            double sumW = 0, sumWZ = 0;
            for (int i = 0; i < bhPts.Count; i++)
            {
                if (!zValues.TryGetValue(i, out double z)) continue;
                double dist = Math.Sqrt(Math.Pow(x - bhPts[i].X, 2) + Math.Pow(y - bhPts[i].Y, 2));
                if (dist < 1e-9) return z; // exakt auf Messpunkt
                if (dist > maxDist) continue;
                double w = 1.0 / (dist * dist);
                sumW += w;
                sumWZ += w * z;
            }
            return sumW > 0 ? sumWZ / sumW : double.NaN;
        }

        private static bool IsTriangleOrientedClockwise(XY p1, XY p2, XY p3)
        {
            double determinant = p1.X * p2.Y + p3.X * p1.Y + p2.X * p3.Y - p1.X * p3.Y - p3.X * p2.Y - p2.X * p1.Y;

            return determinant > 0;
        }

        private static bool IsQuadrilateralConvex(XY a, XY b, XY c, XY d)
        {
            bool abc = IsTriangleOrientedClockwise(a, b, c);
            bool abd = IsTriangleOrientedClockwise(a, b, d);
            bool bcd = IsTriangleOrientedClockwise(b, c, d);
            bool cad = IsTriangleOrientedClockwise(c, a, d);

            if (abc && abd && bcd && !cad) return true;
            if (abc && abd && !bcd && cad) return true;
            if (abc && !abd && bcd && cad) return true;
            if (!abc && !abd && !bcd && cad) return true;
            if (!abc && !abd && bcd && !cad) return true;
            if (!abc && abd && !bcd && !cad) return true;

            return false;
        }

        private static void OrientTrianglesClockwise(List<Tri2D> triangles, List<XY> pts)
        {
            for (int i = 0; i < triangles.Count; i++)
            {
                var tri = triangles[i];
                var v1 = pts[tri.A];
                var v2 = pts[tri.B];
                var v3 = pts[tri.C];

                if (!IsTriangleOrientedClockwise(v1, v2, v3))
                {
                    // Swap B and C
                    triangles[i] = new Tri2D(tri.A, tri.C, tri.B);
                }
            }
        }

        private List<Tri2D> DelaunaySmall(List<XY> pts)
        {
            if (pts.Count < 3) return new List<Tri2D>();

            // Verwende Bowyer-Watson
            var vertices = pts.Select(p => new Vertex(p.X, p.Y)).ToList();
            var delaunay = new Delaunay();
            var triangles = delaunay.Triangulate(vertices);

            var tris = new List<Tri2D>();
            foreach (var t in triangles)
            {
                int a = pts.FindIndex(p => Math.Abs(p.X - t.V0.X) < 1e-6 && Math.Abs(p.Y - t.V0.Y) < 1e-6);
                int b = pts.FindIndex(p => Math.Abs(p.X - t.V1.X) < 1e-6 && Math.Abs(p.Y - t.V1.Y) < 1e-6);
                int c = pts.FindIndex(p => Math.Abs(p.X - t.V2.X) < 1e-6 && Math.Abs(p.Y - t.V2.Y) < 1e-6);
                if (a >= 0 && b >= 0 && c >= 0)
                {
                    tris.Add(new Tri2D(a, b, c));
                }
            }

            OrientTrianglesClockwise(tris, pts);

            return tris;
        }

        private List<Tri2D> FanTriangulateConvexHull(List<XY> pts)
        {
            if (pts.Count < 3) return new List<Tri2D>();

            // Einfacher Fächer: Finde Mittelpunkt, sortiere Punkte angular
            double cx = pts.Average(p => p.X);
            double cy = pts.Average(p => p.Y);

            var sortedPts = pts.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToList();

            var tris = new List<Tri2D>();
            for (int i = 1; i < sortedPts.Count - 1; i++)
            {
                int a = pts.FindIndex(p => p.Equals(sortedPts[0]));
                int b = pts.FindIndex(p => p.Equals(sortedPts[i]));
                int c = pts.FindIndex(p => p.Equals(sortedPts[i + 1]));
                tris.Add(new Tri2D(a, b, c));
            }

            return tris;
        }

        private static bool PointInPolygon(XY p, List<XY> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var pi = poly[i]; var pj = poly[j];
                bool intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                                 (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-9) + pi.X);
                if (intersect) inside = !inside;
            }
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if (PointOnSegment(p, poly[j], poly[i])) return true;
            return inside;
        }

        private static bool PointOnSegment(XY p, XY a, XY b)
        {
            double cross = (p.Y - a.Y) * (b.X - a.X) - (p.X - a.X) * (b.Y - a.Y);
            // Tolerance in meters; be lenient to avoid "all points on edge" issues with noisy coordinates.
            const double EPS = 0.05; // 5 cm
            if (Math.Abs(cross) > EPS) return false;
            double dot = (p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y);
            if (dot < 0) return false;
            double len2 = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
            if (dot > len2) return false;
            return true;
        }

        private static bool Barycentric2D(double px, double py,
            double ax, double ay, double bx, double by, double cx, double cy,
            out double u, out double v, out double w)
        {
            double v0x = bx - ax, v0y = by - ay;
            double v1x = cx - ax, v1y = cy - ay;
            double v2x = px - ax, v2y = py - ay;

            double d00 = v0x * v0x + v0y * v0y;
            double d01 = v0x * v1x + v0y * v1y;
            double d11 = v1x * v1x + v1y * v1y;
            double d20 = v2x * v0x + v2y * v0y;
            double d21 = v2x * v1x + v2y * v1y;

            double denom = d00 * d11 - d01 * d01;
            if (Math.Abs(denom) < 1e-12) { u = v = w = 0; return false; }
            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = 1.0 - v - w;
            return true;
        }

        private static void AddTri(TessellatedShapeBuilder tsb, XYZ[] verts, ElementId matId)
        {
            if (verts[0].IsAlmostEqualTo(verts[1]) || verts[1].IsAlmostEqualTo(verts[2]) || verts[2].IsAlmostEqualTo(verts[0]))
                return;
            var face = new TessellatedFace(verts.ToList(), matId);
            if (face.IsValidObject) tsb.AddFace(face);
        }

        private static void AddQuadAsTwoTris(TessellatedShapeBuilder tsb, XYZ a, XYZ b, XYZ c, XYZ d, ElementId matId)
        {
            AddTri(tsb, new[] { a, b, c }, matId);
            AddTri(tsb, new[] { a, c, d }, matId);
        }

        private static string FirstNonEmpty(IEnumerable<GeologyLayer> layers, Func<GeologyLayer, string> sel)
        {
            foreach (var l in layers) { var s = sel(l); if (!string.IsNullOrWhiteSpace(s)) return s; }
            return null;
        }

        private static string NormalizeKey(GeologyLayer gl, bool useCodeKey)
        {
            var raw = useCodeKey ? (gl.GeologyCode ?? "") : (gl.Description ?? "");
            raw = raw.Trim();
            if (string.IsNullOrEmpty(raw)) raw = "SCHICHT";
            return Regex.Replace(raw.ToUpperInvariant(), @"\s+", " ");
        }

        private static (double, double) GetAnchor(Document doc, IList<Borehole> holes)
        {
            var pl = doc.ActiveProjectLocation; var pp = pl?.GetProjectPosition(XYZ.Zero);
            if (pp != null && (Math.Abs(pp.EastWest) > 1e-6 || Math.Abs(pp.NorthSouth) > 1e-6))
                return (UnitUtils.ConvertFromInternalUnits(pp.EastWest, UnitTypeId.Meters),
                        UnitUtils.ConvertFromInternalUnits(pp.NorthSouth, UnitTypeId.Meters));
            var f = holes.First(); return (f.Easting, f.Northing);
        }

        private static List<XY> BoundaryVerticesMeters(CurveLoop boundaryInt)
        {
            var outList = new List<XY>();
            foreach (var c in boundaryInt)
            {
                var p = c.GetEndPoint(0);
                outList.Add(new XY(
                    UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters)
                ));
            }
            return outList;
        }

        private static XYZ ToInt(XY m, double z)
        {
            return new XYZ(
                UnitUtils.ConvertToInternalUnits(m.X, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(m.Y, UnitTypeId.Meters),
                UnitUtils.ConvertToInternalUnits(z, UnitTypeId.Meters));
        }

        // Strukturen

        public string BuildGroundModel_SectionsBRep(
            Document doc,
            IList<Borehole> boreholes,
            IList<GeologyLayer> allLayers,
            CurveLoop userBoundary,
            XYZ axisP0Int, XYZ axisP1Int,
            bool useCodeAsLayerKey,
            int edgeTopMode /* 0=NearestBorehole, 1=FlatAtMean */)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (boreholes == null || boreholes.Count < 1) return "Keine Bohrungen.";
            if (allLayers == null || allLayers.Count == 0) return "Keine Schichten.";
            if (userBoundary == null) return "Keine Boundary definiert.";

            var anchor = GetAnchor(doc, boreholes);
            var boundaryM = BoundaryVerticesMeters(userBoundary);
            if (boundaryM == null || boundaryM.Count < 3) return "Ungültige Boundary.";
            for (int i = 0; i < boundaryM.Count; i++)
                boundaryM[i] = new XY(boundaryM[i].X - anchor.Item1, boundaryM[i].Y - anchor.Item2);

            XYZ axisP0_m = new XYZ(
                UnitUtils.ConvertFromInternalUnits(axisP0Int.X, UnitTypeId.Meters) - anchor.Item1,
                UnitUtils.ConvertFromInternalUnits(axisP0Int.Y, UnitTypeId.Meters) - anchor.Item2, 0);
            XYZ axisP1_m = new XYZ(
                UnitUtils.ConvertFromInternalUnits(axisP1Int.X, UnitTypeId.Meters) - anchor.Item1,
                UnitUtils.ConvertFromInternalUnits(axisP1Int.Y, UnitTypeId.Meters) - anchor.Item2, 0);

            XYZ dir = axisP1_m - axisP0_m;
            double len = System.Math.Max(1e-9, System.Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y));
            dir = new XYZ(dir.X / len, dir.Y / len, 0);
            XYZ perp = new XYZ(-dir.Y, dir.X, 0);

            // Querlinie: Schnitt mit Boundary
            (XY left, XY right) IntersectAtT(double tVal)
            {
                var c = new XY(axisP0_m.X + tVal * dir.X, axisP0_m.Y + tVal * dir.Y);
                var hits = new System.Collections.Generic.List<(double s, XY pt)>();
                for (int i = 0; i < boundaryM.Count; i++)
                {
                    var a = boundaryM[i];
                    var b = boundaryM[(i + 1) % boundaryM.Count];
                    double s; XY pt;
                    if (LineSegIntersect(c, perp, a, b, out s, out pt)) hits.Add((s, pt));
                }
                if (hits.Count < 2) return (c, c);
                hits.Sort((u, v) => u.s.CompareTo(v.s));
                return (hits[0].pt, hits[hits.Count - 1].pt);
            }

            // Bohrungen einsammeln
            var bhById = new Dictionary<string, (Borehole bh, XY p, double t)>(StringComparer.OrdinalIgnoreCase);
            var bhKeyByRef = new Dictionary<Borehole, string>();
            foreach (var bh in boreholes)
            {
                if (bh == null) continue;
                var p = new XY(bh.Easting - anchor.Item1, bh.Northing - anchor.Item2);
                // Tolerance in meters; picking noise and coordinate rounding can put points slightly outside.
                const double EDGE_TOL_M = 0.20; // 20 cm
                if (!(PointInPolygon(p, boundaryM) || PointOnBoundary(p, boundaryM, EDGE_TOL_M)))
                    continue;

                double t = (p.X - axisP0_m.X) * dir.X + (p.Y - axisP0_m.Y) * dir.Y;
                string bhId = (bh.LocationID ?? "").Trim();
                if (bhId.Length == 0)
                    bhId = $"BH_{System.Math.Round(p.X, 3)}_{System.Math.Round(p.Y, 3)}";

                // Ensure uniqueness but keep a stable mapping per Borehole reference.
                var uniqueId = bhId;
                int suffix = 1;
                while (bhById.ContainsKey(uniqueId))
                    uniqueId = bhId + "_" + (suffix++);

                bhById[uniqueId] = (bh, p, t);
                bhKeyByRef[bh] = uniqueId;
            }
            if (bhById.Count < 1)
            {
                // Fallback: boundary filtering failed (often caused by coordinate basis mismatch between
                // picked boundary points and imported borehole coordinates). Continue with all boreholes
                // so the user is not blocked.
                foreach (var bh in boreholes)
                {
                    if (bh == null) continue;
                    var p = new XY(bh.Easting - anchor.Item1, bh.Northing - anchor.Item2);
                    double t = (p.X - axisP0_m.X) * dir.X + (p.Y - axisP0_m.Y) * dir.Y;
                    string bhId = (bh.LocationID ?? "").Trim();
                    if (bhId.Length == 0)
                        bhId = $"BH_{System.Math.Round(p.X, 3)}_{System.Math.Round(p.Y, 3)}";

                    var uniqueId = bhId;
                    int suffix = 1;
                    while (bhById.ContainsKey(uniqueId))
                        uniqueId = bhId + "_" + (suffix++);

                    bhById[uniqueId] = (bh, p, t);
                    bhKeyByRef[bh] = uniqueId;
                }
            }

            // Gruppen
            string KeyFor(GeologyLayer gl) { return NormalizeKey(gl, useCodeAsLayerKey); }
            var groups = allLayers.GroupBy(KeyFor).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // zTop/zBase je Bohrung
            var zTopAtBh = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            var zBaseAtBh = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in groups)
            {
                var key = kv.Key;
                foreach (var gl in kv.Value)
                {
                    string lid = (gl.LocationID ?? "").Trim();
                    // Layers reference boreholes by LocationID. Our borehole dictionary may have had to
                    // generate unique IDs for empty/missing LocationID. Therefore, match by actual LocationID.
                    var match = bhById.Values.FirstOrDefault(x => string.Equals((x.bh.LocationID ?? "").Trim(), lid, StringComparison.OrdinalIgnoreCase));
                    if (match.bh == null) continue;
                    var bh = match.bh;

                    double topD = gl.DepthTop;
                    double baseD = (gl.DepthBase > gl.DepthTop) ? gl.DepthBase : (gl.DepthTop + gl.DepthBase);
                    double zTop = _depthIsBelowGround ? bh.GroundLevel - topD : bh.GroundLevel + topD;
                    double zBase = _depthIsBelowGround ? bh.GroundLevel - baseD : bh.GroundLevel + baseD;

                    if (System.Math.Abs(zTop - zBase) < MinThicknessMeters)
                        zBase = _depthIsBelowGround ? zTop - MinThicknessMeters : zTop + MinThicknessMeters;

                    Dictionary<string, double> dT;
                    Dictionary<string, double> dB;
                    // Store by the same key that sections use (uniqueId) to keep later lookups consistent.
                    var storeKey = bhKeyByRef.TryGetValue(bh, out var kRef) ? kRef : lid;
                    if (!zTopAtBh.TryGetValue(storeKey, out dT)) { dT = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); zTopAtBh[storeKey] = dT; }
                    if (!zBaseAtBh.TryGetValue(storeKey, out dB)) { dB = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); zBaseAtBh[storeKey] = dB; }
                    dT[key] = zTop; dB[key] = zBase;
                }
            }

            // Stationen
            var bhStations = bhById.Values.OrderBy(v => v.t).ToList();
            if (bhStations.Count == 0) return "Keine Stationen.";

            double tMin = double.PositiveInfinity, tMax = double.NegativeInfinity;
            for (int i = 0; i < boundaryM.Count; i++)
            {
                var v = boundaryM[i];
                double tt = (v.X - axisP0_m.X) * dir.X + (v.Y - axisP0_m.Y) * dir.Y;
                if (tt < tMin) tMin = tt; if (tt > tMax) tMax = tt;
            }
            var lrMin = IntersectAtT(tMin);
            var lrMax = IntersectAtT(tMax);

            int nearestBhIndex(double tt)
            {
                int idx = 0; double best = double.MaxValue;
                for (int i = 0; i < bhStations.Count; i++)
                {
                    double d = System.Math.Abs(bhStations[i].t - tt);
                    if (d < best) { best = d; idx = i; }
                }
                return idx;
            }
            int iMin = nearestBhIndex(tMin);
            int iMax = nearestBhIndex(tMax);
            var nearKeyMin = bhKeyByRef.TryGetValue(bhStations[iMin].bh, out var nk0) ? nk0 : (bhStations[iMin].bh.LocationID ?? string.Empty).Trim();
            var nearKeyMax = bhKeyByRef.TryGetValue(bhStations[iMax].bh, out var nk1) ? nk1 : (bhStations[iMax].bh.LocationID ?? string.Empty).Trim();

            var sections = new List<Section>();
            sections.Add(new Section { Kind = "boundary", T = tMin, L = lrMin.left, R = lrMin.right, GL = bhStations[iMin].bh.GroundLevel, BoreId = nearKeyMin });
            foreach (var s in bhStations)
            {
                var lr = IntersectAtT(s.t);
                var boreKey = bhKeyByRef.TryGetValue(s.bh, out var kRef) ? kRef : (s.bh.LocationID ?? "").Trim();
                sections.Add(new Section { Kind = "bore", T = s.t, L = lr.left, R = lr.right, GL = s.bh.GroundLevel, BoreId = boreKey });
            }
            sections.Add(new Section { Kind = "boundary", T = tMax, L = lrMax.left, R = lrMax.right, GL = bhStations[iMax].bh.GroundLevel, BoreId = nearKeyMax });
            sections.Sort((a, b) => a.T.CompareTo(b.T));

            // Reihenfolge oben -> unten
            var keyMeanZ = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            // Only consider keys that actually exist in at least one borehole profile.
            var presentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in zTopAtBh)
                foreach (var k in kv.Value.Keys)
                    presentKeys.Add(k);

            foreach (var k in presentKeys)
            {
                double sum = 0; int cnt = 0;
                foreach (var e in bhById)
                {
                    if (zTopAtBh.TryGetValue(e.Key, out var dT) && dT.TryGetValue(k, out var z))
                    {
                        sum += z;
                        cnt++;
                    }
                }
                // cnt==0 should not happen, but keep a safe fallback.
                keyMeanZ[k] = cnt > 0 ? (sum / cnt) : double.NegativeInfinity;
            }

            var layerOrder = new List<string>(keyMeanZ.Keys);
            layerOrder.Sort((k1, k2) => keyMeanZ[k2].CompareTo(keyMeanZ[k1]));

            // Profile je Station
            var profilesTop = new List<Dictionary<string, double>>();
            var profilesBase = new List<Dictionary<string, double>>();
            for (int sIdx = 0; sIdx < sections.Count; sIdx++)
            {
                var sec = sections[sIdx];
                var dT = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var dB = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                double prevBase = sec.GL;

                foreach (var k in layerOrder)
                {
                    double zTop, zBase;
                    if (sec.Kind == "bore")
                    {
                        Dictionary<string, double> srcT, srcB;
                        if (!string.IsNullOrWhiteSpace(sec.BoreId) &&
                            zTopAtBh.TryGetValue(sec.BoreId, out srcT) && srcT.TryGetValue(k, out zTop) &&
                            zBaseAtBh.TryGetValue(sec.BoreId, out srcB) && srcB.TryGetValue(k, out zBase))
                        {
                            if (System.Math.Abs(zTop - zBase) < MinThicknessMeters)
                                zBase = _depthIsBelowGround ? zTop - MinThicknessMeters : zTop + MinThicknessMeters;
                        }
                        else
                        {
                            zTop = prevBase; zBase = prevBase;
                        }
                    }
                    else
                    {
                        int near = (System.Math.Abs(sections[1].T - sec.T) < System.Math.Abs(sections[sections.Count - 2].T - sec.T)) ? 1 : sections.Count - 2;
                        string nearId = sections[near].BoreId;
                        Dictionary<string, double> srcT, srcB;
                        if (!string.IsNullOrWhiteSpace(nearId) &&
                            zTopAtBh.TryGetValue(nearId, out srcT) && srcT.TryGetValue(k, out zTop) &&
                            zBaseAtBh.TryGetValue(nearId, out srcB) && srcB.TryGetValue(k, out zBase))
                        {
                            if (System.Math.Abs(zTop - zBase) < MinThicknessMeters)
                                zBase = _depthIsBelowGround ? zTop - MinThicknessMeters : zTop + MinThicknessMeters;
                        }
                        else { zTop = prevBase; zBase = prevBase; }
                    }
                    prevBase = zBase;
                    dT[k] = zTop; dB[k] = zBase;
                }

                profilesTop.Add(dT); profilesBase.Add(dB);
            }

            // Keile bauen
            int created = 0;
            using (var tx = new Transaction(doc, "AWESBox – 3D Bodenmodell (BRep/Querprofile)"))
            {
                tx.Start();
                foreach (var k in layerOrder)
                {
                    string displayName = FirstNonEmpty(groups[k], l => l.Description) ?? k;
                    for (int iSeg = 0; iSeg < sections.Count - 1; iSeg++)
                    {
                        var s0 = sections[iSeg];
                        var s1 = sections[iSeg + 1];

                        XY L0 = s0.L, R0 = s0.R, L1 = s1.L, R1 = s1.R;
                        double keep = DistM(L1, L0) + DistM(R1, R0);
                        double swap = DistM(R1, L0) + DistM(L1, R0);
                        if (swap < keep) { XY tmp = L1; L1 = R1; R1 = tmp; }

                        const double EPS_M = 1e-4;
                        if (DistM(L0, R0) < EPS_M || DistM(L1, R1) < EPS_M) continue;

                        double zT0 = profilesTop[iSeg][k], zB0 = profilesBase[iSeg][k];
                        double zT1 = profilesTop[iSeg + 1][k], zB1 = profilesBase[iSeg + 1][k];

                        // If the layer is absent at BOTH ends (collapsed to the previous base), skip.
                        // This avoids generating a chain of near-zero solids caused by missing keys.
                        if (System.Math.Abs(zT0 - zB0) < 1e-12 && System.Math.Abs(zT1 - zB1) < 1e-12)
                            continue;

                        double EPS_Z = System.Math.Min(1e-3, System.Math.Max(1e-4, 0.01 * MinThicknessMeters));
                        if (System.Math.Abs(zT0 - zB0) < 1e-9) zB0 = _depthIsBelowGround ? zT0 - EPS_Z : zT0 + EPS_Z;
                        if (System.Math.Abs(zT1 - zB1) < 1e-9) zB1 = _depthIsBelowGround ? zT1 - EPS_Z : zT1 + EPS_Z;

                        XYZ TL0 = ToInt(L0, zT0), TR0 = ToInt(R0, zT0);
                        XYZ BL0 = ToInt(L0, zB0), BR0 = ToInt(R0, zB0);
                        XYZ TL1 = ToInt(L1, zT1), TR1 = ToInt(R1, zT1);
                        XYZ BL1 = ToInt(L1, zB1), BR1 = ToInt(R1, zB1);

                        Solid solid = BuildWedgeBRep(TL0, TR0, TR1, TL1, BL0, BR0, BR1, BL1);
                        if (solid == null) continue;

                        var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                        ds.Name = SanitizeNameForRevit(displayName + $" [{iSeg + 1}]");
                        ds.SetShape(new System.Collections.Generic.List<GeometryObject> { solid });
                        created++;
                    }
                }
                tx.Commit();
            }

            return created > 0 ? null : "Keine Modelle erstellt.";
        }

        public struct XY
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
            public XY(double x, double y, double z = 0) { X = x; Y = y; Z = z; }
            public override bool Equals(object obj)
            {
                if (obj is XY other)
                    return Math.Abs(X - other.X) < 1e-6 && Math.Abs(Y - other.Y) < 1e-6;
                return false;
            }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 23 + X.GetHashCode();
                    hash = hash * 23 + Y.GetHashCode();
                    return hash;
                }
            }
        }

        public struct Tri2D
        {
            public int A { get; set; }
            public int B { get; set; }
            public int C { get; set; }
            public Tri2D(int a, int b, int c) { A = a; B = b; C = c; }
        }

        public class Tin
        {
            public List<XY> Nodes { get; set; } = new List<XY>();
            public List<Tri2D> Tris { get; set; } = new List<Tri2D>();
        }

        // Bowyer-Watson Delaunay Implementation
        public class Vertex
        {
            public double X { get; set; }
            public double Y { get; set; }

            public Vertex(double x, double y)
            {
                X = x;
                Y = y;
            }

            public bool Equals(Vertex other)
            {
                return Math.Abs(X - other.X) < 1e-6 && Math.Abs(Y - other.Y) < 1e-6;
            }
        }

        public class Edge
        {
            public Vertex V0 { get; set; }
            public Vertex V1 { get; set; }

            public Edge(Vertex v0, Vertex v1)
            {
                V0 = v0;
                V1 = v1;
            }

            public bool Equals(Edge other)
            {
                return (V0.Equals(other.V0) && V1.Equals(other.V1)) ||
                       (V0.Equals(other.V1) && V1.Equals(other.V0));
            }
        }

        public class CircumCircle
        {
            public Vertex C { get; set; } // Circumcenter
            public double R { get; set; } // Radius
        }

        public class Triangle
        {
            public Vertex V0 { get; set; }
            public Vertex V1 { get; set; }
            public Vertex V2 { get; set; }
            public CircumCircle CircumCirc { get; set; }

            public Triangle(Vertex v0, Vertex v1, Vertex v2)
            {
                V0 = v0;
                V1 = v1;
                V2 = v2;
                CircumCirc = CalcCircumCirc(v0, v1, v2);
            }

            public bool InCircumcircle(Vertex v)
            {
                var dx = CircumCirc.C.X - v.X;
                var dy = CircumCirc.C.Y - v.Y;
                return Math.Sqrt(dx * dx + dy * dy) <= CircumCirc.R + 1e-6;
            }
        }

        private static CircumCircle CalcCircumCirc(Vertex a, Vertex b, Vertex c)
        {
            double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            if (Math.Abs(d) < 1e-6) d = 1e-6; // Vermeide Division durch Null
            double ux = ((a.X * a.X + a.Y * a.Y) * (b.Y - c.Y) + (b.X * b.X + b.Y * b.Y) * (c.Y - a.Y) + (c.X * c.X + c.Y * c.Y) * (a.Y - b.Y)) / d;
            double uy = ((a.X * a.X + a.Y * a.Y) * (c.X - b.X) + (b.X * b.X + b.Y * b.Y) * (a.X - c.X) + (c.X * c.X + c.Y * c.Y) * (b.X - a.X)) / d;

            var center = new Vertex(ux, uy);
            double r = Math.Sqrt((ux - a.X) * (ux - a.X) + (uy - a.Y) * (uy - a.Y));

            return new CircumCircle { C = center, R = r };
        }

        public class Delaunay
        {
            public List<Triangle> Triangulate(List<Vertex> vertices)
            {
                var superTriangle = MakeSuperTriangle(vertices);

                var triangles = new List<Triangle> { superTriangle };

                foreach (var vertex in vertices)
                {
                    triangles = AddVertex(vertex, triangles);
                }

                triangles = triangles.Where(t =>
                    !t.V0.Equals(superTriangle.V0) && !t.V0.Equals(superTriangle.V1) && !t.V0.Equals(superTriangle.V2) &&
                    !t.V1.Equals(superTriangle.V0) && !t.V1.Equals(superTriangle.V1) && !t.V1.Equals(superTriangle.V2) &&
                    !t.V2.Equals(superTriangle.V0) && !t.V2.Equals(superTriangle.V1) && !t.V2.Equals(superTriangle.V2)).ToList();

                return triangles;
            }

            private Triangle MakeSuperTriangle(List<Vertex> vertices)
            {
                double minX = vertices.Min(v => v.X), minY = vertices.Min(v => v.Y);
                double maxX = vertices.Max(v => v.X), maxY = vertices.Max(v => v.Y);

                var dx = (maxX - minX) * 10;
                var dy = (maxY - minY) * 10;

                var v0 = new Vertex(minX - dx, minY - dy * 3);
                var v1 = new Vertex(minX - dx, maxY + dy);
                var v2 = new Vertex(maxX + dx * 3, maxY + dy);

                return new Triangle(v0, v1, v2);
            }

            private List<Triangle> AddVertex(Vertex vertex, List<Triangle> triangles)
            {
                var edges = new List<Edge>();

                var remainingTriangles = new List<Triangle>();
                foreach (var triangle in triangles)
                {
                    if (triangle.InCircumcircle(vertex))
                    {
                        edges.Add(new Edge(triangle.V0, triangle.V1));
                        edges.Add(new Edge(triangle.V1, triangle.V2));
                        edges.Add(new Edge(triangle.V2, triangle.V0));
                    }
                    else
                    {
                        remainingTriangles.Add(triangle);
                    }
                }

                edges = UniqueEdges(edges);

                foreach (var edge in edges)
                {
                    remainingTriangles.Add(new Triangle(edge.V0, edge.V1, vertex));
                }

                return remainingTriangles;
            }

            private List<Edge> UniqueEdges(List<Edge> edges)
            {
                var uniqueEdges = new List<Edge>();
                for (int i = 0; i < edges.Count; ++i)
                {
                    bool isUnique = true;
                    for (int j = 0; j < edges.Count; ++j)
                    {
                        if (i != j && edges[i].Equals(edges[j]))
                        {
                            isUnique = false;
                            break;
                        }
                    }
                    if (isUnique)
                    {
                        uniqueEdges.Add(edges[i]);
                    }
                }
                return uniqueEdges;
            }
        }

        // Placeholder für Material
        private Material CreateOrGetMaterial(Document doc, string name)
        {
            // Suche oder erstelle Material
            var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>();
            var mat = materials.FirstOrDefault(m => m.Name == name);
            if (mat != null) return mat;

            ElementId matId;
            using (var subTx = new SubTransaction(doc))
            {
                subTx.Start();
                matId = Material.Create(doc, name);
                subTx.Commit();
            }
            return doc.GetElement(matId) as Material;
        }

        private static string SanitizeNameForRevit(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Unbenannt";

            // Revit verbietet u.a.: \ / : * ? " < > | { } [ ] ; und Steuerzeichen
            var invalid = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '{', '}', '[', ']', ';' }));
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch < 32) continue;
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }

            var cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim(' ', '_', '.');
            cleaned = Regex.Replace(cleaned, @"_+", "_");
            if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Unbenannt";
            if (cleaned.Length > 240) cleaned = cleaned.Substring(0, 240);
            return cleaned;
        }
    }
}