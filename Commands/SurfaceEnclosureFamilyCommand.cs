using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Services;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SurfaceEnclosureFamilyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            if (doc.IsFamilyDocument)
            {
                message = "Die Funktion benötigt ein bearbeitbares Projekt, keine geöffnete Familie.";
                return Result.Failed;
            }

            try
            {
                IList<Reference> picked = uidoc.Selection.PickObjects(ObjectType.Face, new FaceSelectionFilter(),
                    "Umschließende Flächen wählen und mit Fertig bestätigen");
                if (picked.Count == 0)
                    return Result.Cancelled;

                var faces = new List<Face>();
                var selectedReferences = new List<Reference>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Reference reference in picked)
                {
                    string stableId = reference.ConvertToStableRepresentation(doc);
                    if (!seen.Add(stableId))
                        continue;
                    Element element = doc.GetElement(reference)
                        ?? throw new InvalidOperationException("Eine ausgewählte Fläche gehört zu keinem verfügbaren Element mehr.");
                    if (element.GetGeometryObjectFromReference(reference) is not Face face)
                        throw new InvalidOperationException("Die Auswahl enthält ein Objekt, das keine Revit-Fläche ist.");
                    faces.Add(face);
                    selectedReferences.Add(reference);
                }
                if (faces.Count < 2)
                    throw new InvalidOperationException("Mindestens zwei unterschiedliche umschließende Flächen auswählen.");

                double tolerance = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
                bool reused = SurfaceEnclosureBuilder.TryReuseCompleteSourceSolid(doc, selectedReferences, faces,
                    tolerance, out Solid? solid, out string diagnostic);
                if (!reused)
                {
                    string reuseDiagnostic = diagnostic;
                    if (!SurfaceEnclosureBuilder.TryBuild(faces, tolerance, doc.Application.ShortCurveTolerance,
                        out solid, out diagnostic) || solid == null)
                    {
                        TaskDialog.Show("Geometrie auf umschlossenen Flächen", diagnostic + "\n\n" + reuseDiagnostic);
                        return Result.Cancelled;
                    }
                }

                string name = Microsoft.VisualBasic.Interaction.InputBox(
                    "Name der neuen Familie aus den umschlossenen Flächen:",
                    "Geometrie auf umschlossenen Flächen erstellen", "").Trim();
                if (string.IsNullOrEmpty(name))
                    return Result.Cancelled;

                SurfaceEnclosureFamilyBuilder.CreateFamily(uiapp, doc, solid, name);
                TaskDialog.Show("Geometrie auf umschlossenen Flächen", diagnostic);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (System.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception error)
            {
                message = error.Message;
                return Result.Failed;
            }
        }

        private sealed class FaceSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element) => true;
            public bool AllowReference(Reference reference, XYZ position)
                => reference.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_SURFACE;
        }
    }
}
