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
        private static readonly Guid NamedSchemaGuid = new Guid("A1E7D2C4-9E19-4A1F-B9A0-8D0E0C6C4F11");

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            View3D view3D = doc.ActiveView as View3D;
            if (view3D == null || view3D.IsTemplate)
            {
                TaskDialog.Show("Fehler", "Bitte eine 3D-Ansicht aktivieren.");
                return Result.Failed;
            }

            NamePromptWindow nameDlg = new NamePromptWindow();
            if (WpfOwner.ShowDialog(nameDlg, commandData.Application) != true)
                return Result.Cancelled;

            try
            {
                SaveNamedSnapshot(doc, view3D, nameDlg.EnteredName);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static Schema GetOrCreateNamedSchema()
        {
            Schema schema = Schema.Lookup(NamedSchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder sb = new SchemaBuilder(NamedSchemaGuid);
            sb.SetSchemaName("AWESBox_SectionBoxSnapshot");
            sb.SetReadAccessLevel(AccessLevel.Public);
            sb.SetWriteAccessLevel(AccessLevel.Public);

            sb.AddSimpleField("Name", typeof(string));
            sb.AddSimpleField("localMin", typeof(string));
            sb.AddSimpleField("localMax", typeof(string));
            sb.AddSimpleField("Origin", typeof(string));
            sb.AddSimpleField("BasisX", typeof(string));
            sb.AddSimpleField("BasisY", typeof(string));
            sb.AddSimpleField("BasisZ", typeof(string));

            // KEIN Datum/Uhrzeit-Feld notwendig
            return sb.Finish();
        }

        private void SaveNamedSnapshot(Document doc, View3D view, string name)
        {
            BoundingBoxXYZ box = view.GetSectionBox();
            Transform t = box.Transform;

            Schema schema = GetOrCreateNamedSchema();
            Entity entity = new Entity(schema);

            entity.Set("Name", name);
            entity.Set("localMin", DataStorageManagement.XYZToString(box.Min));
            entity.Set("localMax", DataStorageManagement.XYZToString(box.Max));
            entity.Set("Origin", DataStorageManagement.XYZToString(t.Origin));
            entity.Set("BasisX", DataStorageManagement.XYZToString(t.BasisX));
            entity.Set("BasisY", DataStorageManagement.XYZToString(t.BasisY));
            entity.Set("BasisZ", DataStorageManagement.XYZToString(t.BasisZ));

            ViewOrientation3D o = view.GetOrientation();
            entity.Set("EyePosition", DataStorageManagement.XYZToString(o.EyePosition));
            entity.Set("ForwardDirection", DataStorageManagement.XYZToString(o.ForwardDirection));
            entity.Set("UpDirection", DataStorageManagement.XYZToString(o.UpDirection));

            DataStorage existing = new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(ds =>
                {
                    try
                    {
                        Entity e = ds.GetEntity(schema);
                        return e.IsValid() &&
                               string.Equals(e.Get<string>("Name"), name, StringComparison.OrdinalIgnoreCase);
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
