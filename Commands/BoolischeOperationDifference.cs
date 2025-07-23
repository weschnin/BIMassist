using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class BoolischeOperationDifference : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                Element element_1 = doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element, "1. Element wählen"));
                IList<Solid> GeometryElement_1 = FamilyGeometryTools.ReadGeometryFromFamily(doc, new List<ElementId>() { element_1.Id }, true);
                Element element_2 = doc.GetElement(uidoc.Selection.PickObject(ObjectType.Element, "2. Element wählen"));
                IList<Solid> GeometryElement_2 = FamilyGeometryTools.ReadGeometryFromFamily(doc, new List<ElementId>() { element_2.Id }, true);
                IList<Solid> GeometryElement_Result = new List<Solid>();

                foreach (Solid solid1 in GeometryElement_1)
                {
                    foreach (Solid solid2 in GeometryElement_2)
                    {
                        try
                        {
                            Solid sd = BooleanOperationsUtils.ExecuteBooleanOperation(solid1, solid2, BooleanOperationsType.Difference);
                            if (sd.Volume > 0)
                                GeometryElement_Result.Add(sd);
                            else
                            {
                                sd = BooleanOperationsUtils.ExecuteBooleanOperation(solid2, solid1, BooleanOperationsType.Difference);
                                if (sd.Volume > 0)
                                    GeometryElement_Result.Add(sd);
                            }
                        }
                        catch (Exception msg)
                        {
                            TaskDialog.Show("Error", msg.Message);
                        }
                    }
                }

                string Textinput = Microsoft.VisualBasic.Interaction.InputBox("Bitte einen neuen Familienamen eingeben:",
                       "Texteingabe", "");

                if (GeometryElement_Result.Count > 0)
                    FamilyGeometryTools.CreateNewFamily(uiapp, doc, GeometryElement_Result, Textinput);
            }
            catch (Exception msg)
            {
                TaskDialog.Show("Error", msg.Message);
            }

            return Result.Succeeded;
        }
    }
}

