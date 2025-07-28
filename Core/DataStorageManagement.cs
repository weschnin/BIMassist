using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

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
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            schemaBuilder.SetVendorId("gmail.com.weschnin");
            
            schemaBuilder.AddSimpleField("localMin", typeof(string));
            schemaBuilder.AddSimpleField("localMax", typeof(string));
            schemaBuilder.AddSimpleField("Origin", typeof(string));
            schemaBuilder.AddSimpleField("BasisX", typeof(string));
            schemaBuilder.AddSimpleField("BasisY", typeof(string));
            schemaBuilder.AddSimpleField("BasisZ", typeof(string));

            schemaBuilder.SetSchemaName("SectionBoxStorage");
            return schemaBuilder.Finish();
        }
        public static DataStorage GetOrCreateDataStorage(Document doc)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage));

            foreach (DataStorage ds in collector)
            {
                Entity e = ds.GetEntity(GetOrCreateSchema());
                if (e.IsValid()) return ds;
            }

            var storage = DataStorage.Create(doc);

            return storage;
            
        }

        public static string XYZToString(XYZ xyz)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0},{1},{2}", xyz.X, xyz.Y, xyz.Z);
        }
        public static XYZ StringToXYZ(string s)
        {
            var parts = s.Split(',');
            return new XYZ(
                double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        }
        
        
        
    }
}
