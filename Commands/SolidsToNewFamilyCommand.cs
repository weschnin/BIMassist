using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SolidsToNewFamilyCommand : IExternalCommand
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

                IList<Solid> Solids = FamilyGeometryTools.ReadGeometryFromFamily(doc, solidsElementIds, true);
                if (Solids?.Count > 0)
                {
                    string Textinput = Microsoft.VisualBasic.Interaction.InputBox("Bitte einen neuen Familienamen eingeben:",
                        "Texteingabe", "");

                    FamilyGeometryTools.CreateNewFamily(uiapp, doc, Solids, Textinput);
                }
            }
            catch (Exception msg)
            {
                TaskDialog.Show("Error", msg.Message);
            }

            return Result.Succeeded;
        }
        
    }
}
