using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace BIMassist.Core
{
    public static class DataStorageManagement
    {
        readonly static Guid schemaGuidSubDocStorage = new Guid("{317090a4-0bc5-4b25-96f3-b0537fe6c6ce}");
        readonly static Guid schemaGuidDocStorage = new Guid("{fd983d61-8a5b-4bf0-9d85-00fd46982b90}");

        public static Schema[] GetDocStorageSchema()
        {
            var subSchemaBuilder = new SchemaBuilder(schemaGuidSubDocStorage);
            subSchemaBuilder.SetSchemaName("DocDataSchema");
            subSchemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            subSchemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            subSchemaBuilder.SetVendorId("gmail.com.weschnin");
            var SubFieldBuilder = subSchemaBuilder.AddSimpleField("DocPath", typeof(string));
            SubFieldBuilder = subSchemaBuilder.AddArrayField("DocData", typeof(byte));
            SubFieldBuilder.SetDocumentation("Dokumentenname");
            SubFieldBuilder.SetDocumentation("Dokumentenspeicher");
            var sub = subSchemaBuilder.Finish();

            var schemaBuilder = new SchemaBuilder(schemaGuidDocStorage);
            schemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            schemaBuilder.SetVendorId("gmail.com.weschnin");
            var fieldBuilder = schemaBuilder.AddArrayField("DocListe", typeof(Entity));
            fieldBuilder.SetSubSchemaGUID(schemaGuidSubDocStorage);
            fieldBuilder.SetDocumentation("Dokumentenliste");
            schemaBuilder.SetSchemaName("DocListSchema");
            var s = schemaBuilder.Finish();
            Schema[] schemas = { sub, s };
            return schemas;
        }

        readonly static Guid schemaGuidFamAuthor = new Guid("{5d4b6761-8b21-4aff-8957-816f694c449b}");
        public static Schema GetFamAuthorSchema()
        {
            Schema schema = Schema.Lookup(schemaGuidFamAuthor);

            if (schema != null) return schema;

            SchemaBuilder schemaBuilder = new SchemaBuilder(schemaGuidFamAuthor);
            schemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            schemaBuilder.SetVendorId("gmail.com.weschnin");
            var fieldBuilder = schemaBuilder.AddSimpleField("PWHashcode", typeof(string));
            fieldBuilder.SetDocumentation("Hashcode");
            schemaBuilder.SetSchemaName("PasswordHashcode");
            return schemaBuilder.Finish();
        }

        readonly static Guid schemaGuidMengenErmittlung = new Guid("{e4ea96e9-f506-44a5-a82b-8445719b0e3f}");
        public static Schema GetMengenErmittlungSchema()
        {
            Schema schema = Schema.Lookup(schemaGuidMengenErmittlung);

            if (schema != null) return schema;

            SchemaBuilder schemaBuilder = new SchemaBuilder(schemaGuidMengenErmittlung);
            schemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            schemaBuilder.SetVendorId("gmail.com.weschnin");
            FieldBuilder fieldBuilder = schemaBuilder.AddSimpleField(GetMengenErmittlungFieldName, typeof(string));
            fieldBuilder.SetDocumentation("EinstellungsdatenMengenErmittlung");
            schemaBuilder.SetSchemaName("Mengenermittlung");
            return schemaBuilder.Finish();
        }
        public static string GetMengenErmittlungFieldName
        {
            get => "SettingsDataMengenErmittlung";
        }

        readonly static Guid schemaGuidParamInit = new Guid("{0bb54362-5a2f-4ea2-8b63-7b89d93aa2e2}");
        public static Schema GetParamInitSchema()
        {
            Schema schema = Schema.Lookup(schemaGuidParamInit);

            if (schema != null) return schema;

            SchemaBuilder schemaBuilder = new SchemaBuilder(schemaGuidParamInit);
            schemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            schemaBuilder.SetVendorId("gmail.com.weschnin");
            FieldBuilder fieldBuilder = schemaBuilder.AddSimpleField(GetParamInitFieldName, typeof(string));
            fieldBuilder.SetDocumentation("EinstellungsdatenParameterInit");
            schemaBuilder.SetSchemaName("ParameterInit");
            return schemaBuilder.Finish();
        }
        public static string GetParamInitFieldName
        {
            get => "SettingsDataParamInit";
        }

        readonly static Guid schemaGuidAVAParameterListe = new Guid("{63016b8a-cf4f-4909-92b6-a8d21e841008}");
        public static Schema GetAVAParameterlisteSchema()
        {
            Schema schema = Schema.Lookup(schemaGuidAVAParameterListe);

            if (schema != null) return schema;

            SchemaBuilder schemaBuilder = new SchemaBuilder(schemaGuidAVAParameterListe);
            schemaBuilder.SetReadAccessLevel(AccessLevel.Public);
            schemaBuilder.SetWriteAccessLevel(AccessLevel.Public);
            schemaBuilder.SetVendorId("gmail.com.weschnin");
            FieldBuilder fieldBuilder = schemaBuilder.AddSimpleField(GetAVAParameterListeFieldName, typeof(string));
            fieldBuilder.SetDocumentation("EinstellungsdatenAVAParameterListe");
            schemaBuilder.SetSchemaName("AVAParameterListe");
            return schemaBuilder.Finish();
        }
        public static string GetAVAParameterListeFieldName
        {
            get => "SettingsDataAVAParameterListe";
        }


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
