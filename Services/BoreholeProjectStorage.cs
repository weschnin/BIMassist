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
            var ds = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
            if (ds != null) return ds;

            using (var tx = new Transaction(_doc, "AWESBox – Borehole DS anlegen"))
            {
                tx.Start();
                var created = DataStorage.Create(_doc);
                created.SetEntity(new Entity(schema));
                tx.Commit();
                return created;
            }
        }

        public void Save(IEnumerable<Borehole> holes, IEnumerable<GeologyLayer> layers)
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
            }
            catch (Exception ex)
            {
                TaskDialog.Show("AWESBox", "Speichern fehlgeschlagen:\n" + ex.Message);
            }
        }

        public (List<Borehole> holes, List<GeologyLayer> layers)? Load()
        {
            if (_doc == null) return null;

            var schema = BoreholeDataStorageManagement.GetOrCreateMaster();
            var ds = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(d => d.GetEntity(schema).IsValid());
            if (ds == null) return null;

            var e = ds.GetEntity(schema);
            var holes = new List<Borehole>();
            var layers = new List<GeologyLayer>();

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

            return (holes, layers);
        }
    }
}
