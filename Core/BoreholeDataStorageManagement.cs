using System;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace BIMassist.Core
{
    internal static class BoreholeDataStorageManagement
    {
        public static readonly Guid Schema_Master = new("c1309bd4-d62c-48e9-96e0-0dbe985f5ed4");
        public static readonly Guid Schema_Borehole = new("d5ea3a12-61ef-4fa7-8c7c-29bc017cc919");
        public static readonly Guid Schema_Layer = new("680b299b-aeab-4ba2-b207-06dd95650166");

        public static Schema GetOrCreateMaster()
        {
            var s = Schema.Lookup(Schema_Master);
            if (s != null) return s;
            var borehole = GetOrCreateBorehole();
            var layer = GetOrCreateLayer();

            var sb = new SchemaBuilder(Schema_Master);
            sb.SetSchemaName("BIMassist_BoreholeDataset_v2");
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.SetVendorId("github.com.weschnin");

            var fbH = sb.AddArrayField("Holes", typeof(Entity));
            fbH.SetSubSchemaGUID(borehole.GUID);

            var fbL = sb.AddArrayField("Layers", typeof(Entity));
            fbL.SetSubSchemaGUID(layer.GUID);

            return sb.Finish();
        }

        public static Schema GetOrCreateBorehole()
        {
            var s = Schema.Lookup(Schema_Borehole);
            if (s != null) return s;

            var sb = new SchemaBuilder(Schema_Borehole);
            sb.SetSchemaName("BIMassist_Borehole_v2");
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.SetVendorId("github.com.weschnin");

            sb.AddSimpleField("LocationID", typeof(string));
            sb.AddSimpleField("LocationType", typeof(string));
            sb.AddSimpleField("Easting", typeof(string));
            sb.AddSimpleField("Northing", typeof(string));
            sb.AddSimpleField("GroundLevel", typeof(string));
            sb.AddSimpleField("FinalDepth", typeof(string));
            sb.AddSimpleField("Orientation", typeof(string));
            sb.AddSimpleField("Inclination", typeof(string));
            return sb.Finish();
        }

        public static Schema GetOrCreateLayer()
        {
            var s = Schema.Lookup(Schema_Layer);
            if (s != null) return s;

            var sb = new SchemaBuilder(Schema_Layer);
            sb.SetSchemaName("BIMassist_GeologyLayer_v2");
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);
            sb.SetVendorId("github.com.weschnin");

            sb.AddSimpleField("LocationID", typeof(string));
            sb.AddSimpleField("DepthTop", typeof(string));
            sb.AddSimpleField("DepthBase", typeof(string));
            sb.AddSimpleField("Description", typeof(string));
            sb.AddSimpleField("Colour", typeof(string));
            sb.AddSimpleField("Consistency", typeof(string));
            sb.AddSimpleField("Classification", typeof(string));
            sb.AddSimpleField("GeologyCode", typeof(string));
            return sb.Finish();
        }

        public static string ToStr(double value)
            => value.ToString("G17", CultureInfo.InvariantCulture);

        public static double ToDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0.0;
            s = s.Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }
    }
}
