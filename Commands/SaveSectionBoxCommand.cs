using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SaveSectionBoxCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            View3D view3D = doc.ActiveView as View3D;

            // Validierung
            if (view3D == null || view3D.IsTemplate)
            {
                TaskDialog.Show("Fehler", "Die Ausführung dieser Funktion ist nur innerhalb eines Projektes und einer 3D-Ansicht möglich.");
                return Result.Failed;
            }

            SaveSectionBoxToDataStorage(doc, view3D);

            return Result.Succeeded;
        }

        private void SaveSectionBoxToDataStorage(Document doc, View3D view)
        {
            BoundingBoxXYZ box = view.GetSectionBox();
            Transform t = box.Transform;
            Transform inverse = t.Inverse;

            XYZ localMin = inverse.OfPoint(box.Min);
            XYZ localMax = inverse.OfPoint(box.Max);


            Entity entity = new Entity(DataStorageManagement.GetOrCreateSchema());
            entity.Set("localMin", DataStorageManagement.XYZToString(localMin));
            entity.Set("localMax", DataStorageManagement.XYZToString(localMax));
            entity.Set("Origin", DataStorageManagement.XYZToString(t.Origin));
            entity.Set("BasisX", DataStorageManagement.XYZToString(t.BasisX));
            entity.Set("BasisY", DataStorageManagement.XYZToString(t.BasisY));
            entity.Set("BasisZ", DataStorageManagement.XYZToString(t.BasisZ));

            using (Transaction tx = new Transaction(doc, "Save SectionBox"))
            {
                tx.Start();
                var storage = DataStorageManagement.GetOrCreateDataStorage(doc);
                storage.SetEntity(entity);
                tx.Commit();
            }
        }
    }
}
