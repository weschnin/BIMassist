using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    internal class MaterialDeleteCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument.Document;
            UIDocument uidoc = uiapp.ActiveUIDocument;

            // Benutzer auffordern, ein Element auszuwählen
            var selectedElementsRef = uidoc.Selection.PickObjects(Autodesk.Revit.UI.Selection.ObjectType.Element, "Wählen Sie ein Element, um nicht verwendete Materialien zu löschen.");
            if (selectedElementsRef == null)
            {
                TaskDialog.Show("Fehler", "Kein Element ausgewählt.");
                return Result.Failed;
            }

            MaterialTools.DeleteUnusedMaterials(doc, selectedElementsRef);

            return Result.Succeeded;
        }
    }
}
