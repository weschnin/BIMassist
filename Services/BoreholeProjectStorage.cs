using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Core;
using BIMassist.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist.Services
{
    /// <summary>
    /// Persistiert Bohrungen + Schichten direkt im Revit-Projekt mittels Extensible Storage.
    /// Ein einziges DataStorage-Element hält ein Master-Entity mit zwei Array-Feldern (Holes/Layers).
    /// v2: Alle numerischen Werte werden als string gespeichert (kulturinvariant), Konvertierung zentral.
    /// </summary>
    public class BoreholeProjectStorage
    {
        private readonly Document _doc;
        public BoreholeProjectStorage(Document doc) { _doc = doc; }

        private DataStorage GetOrCreateDataStorage()
        {
            var schema = BoreholeDataStorageManagement.GetOrCreateMaster();
            return GetOrCreateDataStorage(schema, "AWESBox – Borehole DS anlegen");
        }

        private DataStorage GetOrCreateBoundaryDataStorage()
        {
            var schema = BoreholeDataStorageManagement.GetOrCreateBoundaryMaster();
            return GetOrCreateDataStorage(schema, "AWESBox – Borehole Boundary DS anlegen");
        }

        private DataStorage GetOrCreateSettingsDataStorage()
        {
            var schema = BoreholeDataStorageManagement.GetOrCreateSettings();
            return GetOrCreateDataStorage(schema, "AWESBox – Borehole Einstellungen DS anlegen");
        }

        private DataStorage GetOrCreateAxisDataStorage()
        {
            var schema = BoreholeDataStorageManagement.GetOrCreateAxis();
            return GetOrCreateDataStorage(schema, "AWESBox – Borehole Achse DS anlegen");
        }

        private DataStorage GetOrCreateDataStorage(Schema schema, string transactionName)
        {
            var ds = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
            if (ds != null) return ds;

            using (var tx = new Transaction(_doc, transactionName))
            {
                tx.Start();
                var created = DataStorage.Create(_doc);
                created.SetEntity(new Entity(schema));
                tx.Commit();
                return created;
            }
        }

        public void Save(IEnumerable<Borehole> holes, IEnumerable<GeologyLayer> layers, IEnumerable<GroundModelBoundaryPoint> boundaryPoints = null, BoreholeManagerSettings settings = null, GroundModelAxis axis = null, bool saveAxis = false)
        {
            if (_doc == null) return;

            try
            {
                var schema = BoreholeDataStorageManagement.GetOrCreateMaster();
                var boreholeS = BoreholeDataStorageManagement.GetOrCreateBorehole();
                var layerS = BoreholeDataStorageManagement.GetOrCreateLayer();
                var ds = GetOrCreateDataStorage();

                var e = new Entity(schema);

                // Holes → Entities (alle Zahlen als string, Strings null-sicher)
                var holeEntities = new List<Entity>();
                if (holes != null)
                {
                    foreach (var h in holes)
                    {
                        var eh = new Entity(boreholeS);
                        eh.Set("LocationID", h.LocationID ?? "");
                        eh.Set("LocationType", h.LocationType ?? "");
                        eh.Set("Easting", BoreholeDataStorageManagement.ToStr(h.Easting));
                        eh.Set("Northing", BoreholeDataStorageManagement.ToStr(h.Northing));
                        eh.Set("GroundLevel", BoreholeDataStorageManagement.ToStr(h.GroundLevel));
                        eh.Set("FinalDepth", BoreholeDataStorageManagement.ToStr(h.FinalDepth));
                        eh.Set("Orientation", h.Orientation ?? "");
                        eh.Set("Inclination", h.Inclination ?? "");
                        holeEntities.Add(eh);
                    }
                }

                // Layers → Entities (alle Zahlen als string, Strings null-sicher)
                var layerEntities = new List<Entity>();
                if (layers != null)
                {
                    foreach (var l in layers)
                    {
                        var el = new Entity(layerS);
                        el.Set("LocationID", l.LocationID ?? "");
                        el.Set("DepthTop", BoreholeDataStorageManagement.ToStr(l.DepthTop));
                        el.Set("DepthBase", BoreholeDataStorageManagement.ToStr(l.DepthBase));
                        el.Set("Description", l.Description ?? "");
                        el.Set("Colour", l.Colour ?? "");
                        el.Set("Consistency", l.Consistency ?? "");
                        el.Set("Classification", l.Classification ?? "");
                        el.Set("GeologyCode", l.GeologyCode ?? "");
                        layerEntities.Add(el);
                    }
                }

                e.Set<IList<Entity>>("Holes", holeEntities);
                e.Set<IList<Entity>>("Layers", layerEntities);

                using (var tx = new Transaction(_doc, "AWESBox – Borehole Daten speichern"))
                {
                    tx.Start();
                    ds.SetEntity(e);
                    tx.Commit();
                }

                SaveBoundary(boundaryPoints);
                if (settings != null)
                    SaveSettings(settings);
                if (saveAxis)
                    SaveAxis(axis);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AWESBox", "Speichern fehlgeschlagen:\n" + ex.Message);
            }
        }

        public (List<Borehole> holes, List<GeologyLayer> layers, List<GroundModelBoundaryPoint> boundaryPoints, BoreholeManagerSettings settings, GroundModelAxis axis)? Load()
        {
            if (_doc == null) return null;

            var holes = new List<Borehole>();
            var layers = new List<GeologyLayer>();

            var schema = BoreholeDataStorageManagement.GetOrCreateMaster();
            var ds = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
            if (ds == null) return (holes, layers, LoadBoundary(), LoadSettings(), LoadAxis());

            var e = ds.GetEntity(schema);

            // Hilfsleser: erst string → double, dann (rückwärtskompatibel) double → double
            double GetD(Entity ent, string field)
            {
                 try { var s = ent.Get<string>(field); return BoreholeDataStorageManagement.ToDouble(s); } catch { }
                try { return ent.Get<double>(field); } catch { }
                return 0.0;
            }
            string GetS(Entity ent, string field)
            {
                try { return ent.Get<string>(field) ?? ""; } catch { return ""; }
            }

            // Holes lesen
            var fHoles = e.Schema.GetField("Holes");
            if (fHoles != null)
            {
                var hArr = e.Get<IList<Entity>>("Holes");
                if (hArr != null)
                {
                    foreach (var eh in hArr)
                    {
                        var bh = new Borehole
                        {
                            LocationID = GetS(eh, "LocationID"),
                            LocationType = GetS(eh, "LocationType"),
                            Easting = GetD(eh, "Easting"),
                            Northing = GetD(eh, "Northing"),
                            GroundLevel = GetD(eh, "GroundLevel"),
                            FinalDepth = GetD(eh, "FinalDepth"),
                            Orientation = GetS(eh, "Orientation"),
                            Inclination = GetS(eh, "Inclination")
                        };
                        holes.Add(bh);
                    }
                }
            }

            // Layers lesen
            var fLayers = e.Schema.GetField("Layers");
            if (fLayers != null)
            {
                var lArr = e.Get<IList<Entity>>("Layers");
                if (lArr != null)
                {
                    foreach (var el in lArr)
                    {
                        var gl = new GeologyLayer
                        {
                            LocationID = GetS(el, "LocationID"),
                            DepthTop = GetD(el, "DepthTop"),
                            DepthBase = GetD(el, "DepthBase"),
                            Description = GetS(el, "Description"),
                            Colour = GetS(el, "Colour"),
                            Consistency = GetS(el, "Consistency"),
                            Classification = GetS(el, "Classification"),
                            GeologyCode = GetS(el, "GeologyCode")
                        };
                        layers.Add(gl);
                    }
                }
            }

            return (holes, layers, LoadBoundary(), LoadSettings(), LoadAxis());
        }

        private void SaveBoundary(IEnumerable<GroundModelBoundaryPoint> boundaryPoints)
        {
            var schema = BoreholeDataStorageManagement.GetOrCreateBoundaryMaster();
            var pointS = BoreholeDataStorageManagement.GetOrCreateBoundaryPoint();
            var ds = GetOrCreateBoundaryDataStorage();

            var e = new Entity(schema);
            var pointEntities = new List<Entity>();
            if (boundaryPoints != null)
            {
                foreach (var p in boundaryPoints)
                {
                    if (p == null) continue;
                    var ep = new Entity(pointS);
                    ep.Set("X", BoreholeDataStorageManagement.ToStr(p.X));
                    ep.Set("Y", BoreholeDataStorageManagement.ToStr(p.Y));
                    pointEntities.Add(ep);
                }
            }

            e.Set<IList<Entity>>("Points", pointEntities);
            using (var tx = new Transaction(_doc, "AWESBox – Borehole Umriss speichern"))
            {
                tx.Start();
                ds.SetEntity(e);
                tx.Commit();
            }
        }

        private List<GroundModelBoundaryPoint> LoadBoundary()
        {
            var points = new List<GroundModelBoundaryPoint>();
            var schema = BoreholeDataStorageManagement.GetOrCreateBoundaryMaster();
            var ds = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
            if (ds == null) return points;

            var e = ds.GetEntity(schema);
            var fPoints = e.Schema.GetField("Points");
            if (fPoints == null) return points;

            double GetD(Entity ent, string field)
            {
                try { var s = ent.Get<string>(field); return BoreholeDataStorageManagement.ToDouble(s); } catch { }
                try { return ent.Get<double>(field); } catch { }
                return 0.0;
            }

            var pArr = e.Get<IList<Entity>>("Points");
            if (pArr == null) return points;

            foreach (var ep in pArr)
            {
                points.Add(new GroundModelBoundaryPoint
                {
                    X = GetD(ep, "X"),
                    Y = GetD(ep, "Y")
                });
            }

            return points;
        }

        private void SaveSettings(BoreholeManagerSettings settings)
        {
            if (settings == null) return;

            var schema = BoreholeDataStorageManagement.GetOrCreateSettings();
            var ds = GetOrCreateSettingsDataStorage();
            var e = new Entity(schema);

            e.Set("BoreholeDiameterCm", BoreholeDataStorageManagement.ToStr(settings.BoreholeDiameterCm));
            e.Set("MaterialNameColumn", settings.MaterialNameColumn ?? "Description");

            using (var tx = new Transaction(_doc, "AWESBox – Borehole Einstellungen speichern"))
            {
                tx.Start();
                ds.SetEntity(e);
                tx.Commit();
            }
        }

        private BoreholeManagerSettings LoadSettings()
        {
            var settings = new BoreholeManagerSettings();
            var schema = BoreholeDataStorageManagement.GetOrCreateSettings();
            var ds = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
            if (ds == null) return settings;

            var e = ds.GetEntity(schema);

            try
            {
                var s = e.Get<string>("BoreholeDiameterCm");
                var d = BoreholeDataStorageManagement.ToDouble(s);
                if (d > 0) settings.BoreholeDiameterCm = d;
            }
            catch { }

            try
            {
                var materialColumn = e.Get<string>("MaterialNameColumn");
                if (!string.IsNullOrWhiteSpace(materialColumn))
                    settings.MaterialNameColumn = materialColumn;
            }
            catch { }

            return settings;
        }

        private void SaveAxis(GroundModelAxis axis)
        {
            var schema = BoreholeDataStorageManagement.GetOrCreateAxis();
            var ds = GetOrCreateAxisDataStorage();
            var e = new Entity(schema);

            if (axis != null && axis.IsValid())
            {
                e.Set("X0", BoreholeDataStorageManagement.ToStr(axis.X0));
                e.Set("Y0", BoreholeDataStorageManagement.ToStr(axis.Y0));
                e.Set("X1", BoreholeDataStorageManagement.ToStr(axis.X1));
                e.Set("Y1", BoreholeDataStorageManagement.ToStr(axis.Y1));
            }
            else
            {
                e.Set("X0", "");
                e.Set("Y0", "");
                e.Set("X1", "");
                e.Set("Y1", "");
            }

            using (var tx = new Transaction(_doc, "AWESBox – Borehole Achse speichern"))
            {
                tx.Start();
                ds.SetEntity(e);
                tx.Commit();
            }
        }

        private GroundModelAxis LoadAxis()
        {
            var schema = BoreholeDataStorageManagement.GetOrCreateAxis();
            var ds = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
            if (ds == null) return null;

            var e = ds.GetEntity(schema);
            double GetD(string field)
            {
                try { return BoreholeDataStorageManagement.ToDouble(e.Get<string>(field)); } catch { }
                return 0.0;
            }

            var axis = new GroundModelAxis
            {
                X0 = GetD("X0"),
                Y0 = GetD("Y0"),
                X1 = GetD("X1"),
                Y1 = GetD("Y1")
            };

            return axis.IsValid() ? axis : null;
        }
    }
}
