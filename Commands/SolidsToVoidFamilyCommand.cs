using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;
using BIMassist.Services;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SolidsToVoidFamilyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                message = "Abzugskörper können mit dieser Funktion nur aus einem Projekt erzeugt werden.";
                return Result.Failed;
            }
            try
            {
                IList<ElementId> selected = uidoc.Selection.GetElementIds().ToList();
                if (selected.Count == 0)
                    selected = uidoc.Selection.PickObjects(ObjectType.Element, "Geometrien für den Abzugskörper auswählen")
                        .Select(reference => reference.ElementId).ToList();
                if (selected.Count == 0) return Result.Cancelled;

                List<GeometryObject> geometry = new();
                foreach (ElementId elementId in selected.GroupBy(id => id.Value).Select(group => group.First()))
                {
                    Element element = doc.GetElement(elementId)
                        ?? throw new InvalidOperationException($"Element {elementId.Value} ist nicht mehr im Projekt vorhanden.");
                    // Dieselbe rekursive Element-/Unterfamilien-Auslese wie beim Geometrie-Befehl,
                    // aber ohne Ansichtsfilter oder stillen DirectShape-Fallback: ein Void muss
                    // ausschließlich aus geschlossenen, schneidbaren Solids bestehen.
                    IList<GeometryObject> part = FamilyGeometryTools.GetAllGeometryObjects(
                        element, new Options { DetailLevel = ViewDetailLevel.Fine });
                    if (part.Count == 0 || part.Any(item => item is Mesh || item is not Solid
                        || ((Solid)item).Volume <= 1e-9))
                        throw new InvalidOperationException($"Element {elementId.Value} enthält Meshes, offene Flächen oder keine vollständig schneidbaren Volumenkörper.");
                    geometry.AddRange(part);
                }
                string name = Microsoft.VisualBasic.Interaction.InputBox(
                    "Bitte einen Namen für die neue Abzugskörper-Familie eingeben:",
                    "Abzugskörper erstellen", "").Trim();
                if (string.IsNullOrEmpty(name)) return Result.Cancelled;

                VoidGeometryFamilyBuilder.CreateVoidFamily(uiapp, doc, geometry, name);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception error)
            {
                message = error.Message;
                TaskDialog.Show("Abzugskörper", error.Message);
                return Result.Failed;
            }
        }
    }
}
