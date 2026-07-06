using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class SolidsIntersectionToNewFamilyCommand : IExternalCommand
    {
        private const double MinIntersectionVolume = 1e-9;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                var elementIds = GetElementIds(uidoc);
                if (elementIds == null || elementIds.Count < 2)
                    return Result.Cancelled;

                var sourceSolids = ReadSourceSolids(doc, elementIds);
                if (sourceSolids.Count < 2)
                {
                    TaskDialog.Show("BIMassist", "Aus der Auswahl konnten weniger als zwei gültige Solid-Volumenkörper gelesen werden.\n\nHinweis: Die Schnittmenge kann nur aus echten Solid-Geometrien erzeugt werden; Mesh-Fallbacks sind für boolesche Schnittmengen nicht geeignet.");
                    return Result.Failed;
                }

                var intersectionSolids = CreatePairwiseIntersections(sourceSolids, out int failedIntersections);
                if (intersectionSolids.Count == 0)
                {
                    string failureHint = failedIntersections > 0
                        ? $"\n\n{failedIntersections} Paar(e) konnten vom Revit-Geometriekernel nicht geschnitten werden."
                        : string.Empty;

                    TaskDialog.Show("BIMassist", "Es wurde keine volumetrische Schnittmenge gefunden.\n\nBitte Geometrien wählen, die sich nicht nur berühren, sondern ein gemeinsames Volumen haben." + failureHint);
                    return Result.Cancelled;
                }

                string familyName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Bitte einen neuen Familienamen für die Schnittmengen eingeben:",
                    "Neue Familie als Schnittmenge",
                    "Schnittmenge");

                if (string.IsNullOrWhiteSpace(familyName))
                    return Result.Cancelled;

                FamilyGeometryTools.CreateNewFamily(
                    uiapp,
                    doc,
                    intersectionSolids.Cast<GeometryObject>().ToList(),
                    familyName.Trim());

                TaskDialog.Show("BIMassist", $"Neue Familie wurde aus {intersectionSolids.Count} Schnittmengen-Geometrie(n) erstellt.");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BIMassist", "Fehler beim Erstellen der Schnittmengen-Familie:\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static IList<ElementId> GetElementIds(UIDocument uidoc)
        {
            var preselection = uidoc.Selection.GetElementIds()?.ToList() ?? new List<ElementId>();
            if (preselection.Count >= 2)
                return preselection.Distinct(new ElementIdValueComparer()).ToList();

            if (preselection.Count == 1)
            {
                TaskDialog.Show("BIMassist", "Für diese Funktion müssen mindestens zwei Geometrien ausgewählt sein.\n\nBitte erneut zwei oder mehr Geometrien wählen.");
                uidoc.Selection.SetElementIds(new List<ElementId>());
            }

            return uidoc.Selection
                .PickObjects(ObjectType.Element, "Zwei oder mehr Geometrien für die Schnittmengen auswählen")
                .Select(r => r.ElementId)
                .Distinct(new ElementIdValueComparer())
                .ToList();
        }

        private static List<SourceSolid> ReadSourceSolids(Document doc, IList<ElementId> elementIds)
        {
            var sourceSolids = new List<SourceSolid>();

            foreach (ElementId elementId in elementIds)
            {
                Solid solid = ReadUnionSolid(doc, elementId, $"Element {elementId.Value}");
                if (solid != null && solid.Volume > MinIntersectionVolume)
                {
                    sourceSolids.Add(new SourceSolid(elementId, solid));
                }
            }

            return sourceSolids;
        }

        private static List<Solid> CreatePairwiseIntersections(List<SourceSolid> sourceSolids, out int failedIntersections)
        {
            var intersectionSolids = new List<Solid>();
            failedIntersections = 0;

            for (int i = 0; i < sourceSolids.Count - 1; i++)
            {
                for (int j = i + 1; j < sourceSolids.Count; j++)
                {
                    try
                    {
                        Solid intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                            sourceSolids[i].Solid,
                            sourceSolids[j].Solid,
                            BooleanOperationsType.Intersect);

                        if (intersection != null && intersection.Volume > MinIntersectionVolume)
                        {
                            intersectionSolids.Add(intersection);
                        }
                    }
                    catch
                    {
                        failedIntersections++;
                    }
                }
            }

            return intersectionSolids;
        }

        private static Solid ReadUnionSolid(Document doc, ElementId elementId, string label)
        {
            IList<GeometryObject> geometryObjects = FamilyGeometryTools.ReadGeometryFromFamily(doc, new List<ElementId> { elementId });
            var solids = geometryObjects?
                .OfType<Solid>()
                .Where(s => s != null && s.Volume > MinIntersectionVolume)
                .ToList() ?? new List<Solid>();

            if (solids.Count == 0)
                return null;

            Solid result = solids[0];
            for (int i = 1; i < solids.Count; i++)
            {
                try
                {
                    result = BooleanOperationsUtils.ExecuteBooleanOperation(result, solids[i], BooleanOperationsType.Union);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("BIMassist", $"Mehrere Teilkörper von {label} konnten nicht zu einem Solid vereinigt werden.\n\nRevit-Fehler:\n{ex.Message}");
                    return null;
                }
            }

            return result;
        }

        private sealed class SourceSolid
        {
            public SourceSolid(ElementId elementId, Solid solid)
            {
                ElementId = elementId;
                Solid = solid;
            }

            public ElementId ElementId { get; }
            public Solid Solid { get; }
        }

        private sealed class ElementIdValueComparer : IEqualityComparer<ElementId>
        {
            public bool Equals(ElementId x, ElementId y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.Value == y.Value;
            }

            public int GetHashCode(ElementId obj)
            {
                return obj?.Value.GetHashCode() ?? 0;
            }
        }
    }
}
