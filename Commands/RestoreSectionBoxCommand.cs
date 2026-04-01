using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Views;
using System;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RestoreSectionBoxCommand : IExternalCommand
    {
        // Schema is immutable per GUID. Use the new V2 layout (includes orientation fields).
        private static readonly Guid NamedSchemaGuidV2 = new Guid("2C8E07C2-1F70-45C5-9C0F-1A9464D1F4E6");
        // Legacy GUID (V1) used in older project files
        private static readonly Guid NamedSchemaGuidV1 = new Guid("A1E7D2C4-9E19-4A1F-B9A0-8D0E0C6C4F11");

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                Schema schema = GetOrCreateNamedSchema();

                // Best effort migration: if legacy V1 snapshots exist, copy them into V2 so they show up.
                TryMigrateLegacySnapshots(doc, schema);

                var manager = new SnapshotManagerWindow(commandData.Application, doc, schema);
                WpfOwner.ShowModeless(manager, commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static void TryMigrateLegacySnapshots(Document doc, Schema v2)
        {
            var v1 = Schema.Lookup(NamedSchemaGuidV1);
            if (v1 == null) return;

            bool anyMigrated = false;

            var storages = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>();

            foreach (var ds in storages)
            {
                Entity e1;
                try { e1 = ds.GetEntity(v1); }
                catch { continue; }
                if (e1 == null || !e1.IsValid())
                    continue;

                // Skip if already migrated to V2
                try
                {
                    var e2Existing = ds.GetEntity(v2);
                    if (e2Existing != null && e2Existing.IsValid())
                        continue;
                }
                catch
                {
                    // ignore and attempt to migrate anyway
                }

                var name = BIMassist.Core.DataStorageManagement.TryGetString(e1, "Name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var e2 = new Entity(v2);
                BIMassist.Core.DataStorageManagement.TrySet(e2, "Name", name.Trim());

                // Copy common fields
                foreach (var f in new[] { "localMin", "localMax", "Origin", "BasisX", "BasisY", "BasisZ", "EyePosition", "ForwardDirection", "UpDirection" })
                {
                    var val = BIMassist.Core.DataStorageManagement.TryGetString(e1, f);
                    if (!string.IsNullOrWhiteSpace(val))
                        BIMassist.Core.DataStorageManagement.TrySet(e2, f, val);
                }

                using (var tx = new Transaction(doc, "SectionBox Snapshot Migration (V1->V2)"))
                {
                    tx.Start();
                    ds.SetEntity(e2);
                    tx.Commit();
                }

                anyMigrated = true;
            }

            if (anyMigrated)
            {
                // no UI here; manager will reload list
            }
        }

        private static Schema GetOrCreateNamedSchema()
        {
            Schema schema = Schema.Lookup(NamedSchemaGuidV2);
            if (schema != null) return schema;

            SchemaBuilder sb = new SchemaBuilder(NamedSchemaGuidV2);
            sb.SetSchemaName("AWESBox_SectionBoxSnapshot_V2");
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);

            sb.AddSimpleField("Name", typeof(string));
            sb.AddSimpleField("localMin", typeof(string));
            sb.AddSimpleField("localMax", typeof(string));
            sb.AddSimpleField("Origin", typeof(string));
            sb.AddSimpleField("BasisX", typeof(string));
            sb.AddSimpleField("BasisY", typeof(string));
            sb.AddSimpleField("BasisZ", typeof(string));

            sb.AddSimpleField("EyePosition", typeof(string));
            sb.AddSimpleField("ForwardDirection", typeof(string));
            sb.AddSimpleField("UpDirection", typeof(string));

            return sb.Finish();
        }
    }
}