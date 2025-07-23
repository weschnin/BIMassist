using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SolidsToFamilyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument.Document;
            UIDocument uidoc = uiapp.ActiveUIDocument;

            IList<ElementId> solidsElementIds = new List<ElementId>();
            try
            {
                if (uidoc.Selection.GetElementIds().Count > 0)
                {
                    solidsElementIds = uidoc.Selection.GetElementIds().ToList();
                }
                else
                    solidsElementIds = uidoc.Selection.PickObjects(ObjectType.Element, "Auswählen von Geometrien").Select(x => x.ElementId).ToList();

                //var position = doc.GetElement(solidsElementIds[0]).Location as LocationPoint;

                var family = doc.GetElement(solidsElementIds[0]) as FamilyInstance;

                IList<Solid> Solids = FamilyGeometryTools.ReadGeometryFromFamily(doc, solidsElementIds, true);

                if (Solids?.Count > 0)
                {
                    Reference r = uidoc.Selection.PickObject(ObjectType.Element, "Ziel Familie auswählen");
                    FamilyGeometryTools.AddSolidsToFamily(uiapp, doc, Solids, r.ElementId);
                }
            }
            catch (Exception msg)
            {
                TaskDialog.Show("Fehler", msg.Message);
            }

            return Result.Succeeded;
        }

    }
}
