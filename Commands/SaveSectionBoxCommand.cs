using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Core;
using BIMassist.Views;
using System;
using System.Linq;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SaveSectionBoxCommand : IExternalCommand
    {
        // NOTE: Extensible Storage schemas are immutable per GUID.
        // If a document already contains an older schema under the old GUID,
        // writing fields that don't exist there will throw.
        // Therefore we version the schema GUID when the field layout changes.
        private static readonly Guid NamedSchemaGuidV2 = new Guid("2C8E07C2-1F70-45C5-9C0F-1A9464D1F4E6");

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            View3D view3D = doc.ActiveView as View3D;
            if (view3D == null || view3D.IsTemplate)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Bitte eine 3D-Ansicht aktivieren.");
                return Result.Failed;
            }

            try
            {
                Schema schema = GetOrCreateNamedSchema();
                SectionBoxSaveWindow saveWindow = new SectionBoxSaveWindow(doc, schema);
                if (WpfOwner.ShowDialog(saveWindow, commandData.Application) != true)
                    return Result.Cancelled;

                SaveNamedSnapshot(doc, view3D, saveWindow.SelectedSnapshotName, schema);

                TaskDialog.Show(
                    "Schnittbox speichern",
                    saveWindow.OverwriteExisting
                        ? $"Schnittbox wurde überschrieben:\n\n{saveWindow.SelectedSnapshotName}"
                        : $"Schnittbox wurde neu gespeichert:\n\n{saveWindow.SelectedSnapshotName}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Schnittbox speichern", "Fehler:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static Schema GetOrCreateNamedSchema()
        {
            Schema schema = Schema.Lookup(NamedSchemaGuidV2);
            if (schema != null) return schema;

            var sb = new SchemaBuilder(NamedSchemaGuidV2);
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

            // View orientation (was missing in V1 and caused field mismatch exceptions)
            sb.AddSimpleField("EyePosition", typeof(string));
            sb.AddSimpleField("ForwardDirection", typeof(string));
            sb.AddSimpleField("UpDirection", typeof(string));

            return sb.Finish();
        }

        private void SaveNamedSnapshot(Document doc, View3D view, string name, Schema schema)
        {
            BoundingBoxXYZ box = view.GetSectionBox();
            Transform t = box.Transform;

            Entity entity = new Entity(schema);

            DataStorageManagement.TrySet(entity, "Name", name);
            DataStorageManagement.TrySet(entity, "localMin", DataStorageManagement.XYZToString(box.Min));
            DataStorageManagement.TrySet(entity, "localMax", DataStorageManagement.XYZToString(box.Max));
            DataStorageManagement.TrySet(entity, "Origin", DataStorageManagement.XYZToString(t.Origin));
            DataStorageManagement.TrySet(entity, "BasisX", DataStorageManagement.XYZToString(t.BasisX));
            DataStorageManagement.TrySet(entity, "BasisY", DataStorageManagement.XYZToString(t.BasisY));
            DataStorageManagement.TrySet(entity, "BasisZ", DataStorageManagement.XYZToString(t.BasisZ));

            ViewOrientation3D o = view.GetOrientation();
            DataStorageManagement.TrySet(entity, "EyePosition", DataStorageManagement.XYZToString(o.EyePosition));
            DataStorageManagement.TrySet(entity, "ForwardDirection", DataStorageManagement.XYZToString(o.ForwardDirection));
            DataStorageManagement.TrySet(entity, "UpDirection", DataStorageManagement.XYZToString(o.UpDirection));

            DataStorage existing = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(ds =>
                {
                    try
                    {
                        var e = ds.GetEntity(schema);
                        if (e.IsValid())
                            return string.Equals(DataStorageManagement.TryGetString(e, "Name"), name, StringComparison.OrdinalIgnoreCase);

                        return false;
                    }
                    catch
                    {
                        return false;
                    }
                });

            using (Transaction tx = new Transaction(doc, existing == null ? "Save SectionBox (neu)" : "Save SectionBox (überschreiben)"))
            {
                tx.Start();
                DataStorage storage = existing ?? DataStorage.Create(doc);
                storage.SetEntity(entity);
                tx.Commit();
            }
        }
    }
}
