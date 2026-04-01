using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using System;
using System.Globalization;

namespace BIMassist.Core
{
    public static class DataStorageManagement
    {
        // 3D Schnittbereich Parameter speichern

        private static readonly Guid SchnittbereichSchemaGuid = new Guid("{4b3ea056-77f3-482a-9b66-d8defe12fd2e}");
        public static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(SchnittbereichSchemaGuid);

            if (schema != null) return schema;

            SchemaBuilder schemaBuilder = new SchemaBuilder(SchnittbereichSchemaGuid);
            schemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            // Prevent cross-addin write conflicts when switching/opening documents.
            // Public write access can trigger Revit's "Datenkonflikt - Add-Ons" dialog if another add-in
            // uses colliding schema metadata.
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            
            schemaBuilder.AddSimpleField("localMin", typeof(string));
            schemaBuilder.AddSimpleField("localMax", typeof(string));
            schemaBuilder.AddSimpleField("Origin", typeof(string));
            schemaBuilder.AddSimpleField("BasisX", typeof(string));
            schemaBuilder.AddSimpleField("BasisY", typeof(string));
            schemaBuilder.AddSimpleField("BasisZ", typeof(string));

            schemaBuilder.SetSchemaName("BIMassist_SectionBoxStorage");
            return schemaBuilder.Finish();
        }
        public static DataStorage GetOrCreateDataStorage(Document doc)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage));

            foreach (DataStorage ds in collector)
            {
                try
                {
                    Entity e = ds.GetEntity(GetOrCreateSchema());
                    if (e.IsValid()) return ds;
                }
                catch
                {
                    // ignore invalid/legacy entities
                }
            }

            var storage = DataStorage.Create(doc);

            return storage;
            
        }

        public static string XYZToString(XYZ xyz)
        {
            return string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2}", xyz.X, xyz.Y, xyz.Z);
        }

        public static bool TryStringToXYZ(string? s, out XYZ xyz)
        {
            xyz = XYZ.Zero;
            if (string.IsNullOrWhiteSpace(s))
                return false;

            var parts = s.Split(',');
            if (parts.Length != 3)
                return false;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                return false;

            xyz = new XYZ(x, y, z);
            return true;
        }

        public static XYZ StringToXYZ(string s)
        {
            if (!TryStringToXYZ(s, out var xyz))
                throw new FormatException($"Invalid XYZ string '{s}'. Expected 'x,y,z' with invariant culture.");
            return xyz;
        }

        public static void TrySet(Entity entity, string fieldName, string value)
        {
            try
            {
                var schema = entity.Schema;
                if (schema == null) return;
                var field = schema.GetField(fieldName);
                if (field == null) return;
                entity.Set(fieldName, value);
            }
            catch
            {
                // ignore schema mismatches / invalid fields
            }
        }

        public static string? TryGetString(Entity entity, string fieldName)
        {
            try
            {
                var schema = entity.Schema;
                if (schema == null) return null;
                var field = schema.GetField(fieldName);
                if (field == null) return null;
                return entity.Get<string>(fieldName);
            }
            catch
            {
                return null;
            }
        }
        
        
        
    }
}
