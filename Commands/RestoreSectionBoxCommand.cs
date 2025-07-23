using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RestoreSectionBoxCommand : IExternalCommand
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

            RestoreSectionBoxFromDataStorage(doc, view3D);

            return Result.Succeeded;
        }
        private void RestoreSectionBoxFromDataStorage(Document doc, View3D view)
        {
            DataStorage storage = DataStorageManagement.GetOrCreateDataStorage(doc);
            Entity entity = storage.GetEntity(DataStorageManagement.GetOrCreateSchema());

            if (!entity.IsValid()) return;

            XYZ localMin = DataStorageManagement.StringToXYZ(entity.Get<string>("localMin"));
            XYZ localMax = DataStorageManagement.StringToXYZ(entity.Get<string>("localMax"));
            XYZ origin = DataStorageManagement.StringToXYZ(entity.Get<string>("Origin"));
            XYZ basisX_raw = DataStorageManagement.StringToXYZ(entity.Get<string>("BasisX"));
            XYZ basisZ_raw = DataStorageManagement.StringToXYZ(entity.Get<string>("BasisZ"));

            // Basisvektoren rekonstruieren
            XYZ basisZ = basisZ_raw.Normalize();
            XYZ basisX = basisX_raw.Normalize();
            XYZ basisY = basisZ.CrossProduct(basisX).Normalize(); // Nur dieser CrossProduct
            basisX = basisY.CrossProduct(basisZ).Normalize();

            Transform t = Transform.Identity;
            t.Origin = origin;
            t.BasisX = basisX;
            t.BasisY = basisY;
            t.BasisZ = basisZ;

            // Min/Max wieder in Weltkoordinaten
            XYZ min = t.OfPoint(localMin);
            XYZ max = t.OfPoint(localMax);

            BoundingBoxXYZ box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = min,
                Max = max
            };

            using (Transaction tx = new Transaction(doc, "Restore SectionBox"))
            {
                tx.Start();
                view.IsSectionBoxActive = true;
                view.SetSectionBox(box);
                tx.Commit();
            }
        }
    }
}
