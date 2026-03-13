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

                

                var family = doc.GetElement(solidsElementIds[0]) as FamilyInstance;

                IList<GeometryObject> geos = FamilyGeometryTools.ReadGeometryFromFamily(doc, solidsElementIds);

                if (geos?.Count > 0)
                {
                    Reference r = uidoc.Selection.PickObject(ObjectType.Element, "Ziel Familie auswählen");
                    FamilyGeometryTools.AddSolidsToFamily(uiapp, doc, geos, r.ElementId);
                }
            }
            catch (Exception msg)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Fehler", msg.Message);
            }

            return Result.Succeeded;
        }

    }
}
