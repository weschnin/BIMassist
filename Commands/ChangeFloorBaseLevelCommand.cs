using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ChangeFloorBaseLevelCommand : IExternalCommand
    {
        private const double Tolerance = 1e-9;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                Floor floor = PickFloor(uidoc, doc);
                Level newLevel = PickLevel(uidoc, doc);

                Parameter? levelParameter = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                Parameter? offsetParameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);

                if (levelParameter == null || levelParameter.IsReadOnly || levelParameter.StorageType != StorageType.ElementId)
                {
                    TaskDialog.Show("Basisebene wechseln", "Die Basisebene der ausgewählten Geschossdecke kann nicht geändert werden.");
                    return Result.Cancelled;
                }

                if (offsetParameter == null || offsetParameter.IsReadOnly || offsetParameter.StorageType != StorageType.Double)
                {
                    TaskDialog.Show("Basisebene wechseln", "Der Versatz der ausgewählten Geschossdecke kann nicht geändert werden.");
                    return Result.Cancelled;
                }

                Level? oldLevel = doc.GetElement(levelParameter.AsElementId()) as Level;
                if (oldLevel == null)
                {
                    TaskDialog.Show("Basisebene wechseln", "Die aktuelle Basisebene der Geschossdecke konnte nicht ermittelt werden.");
                    return Result.Cancelled;
                }

                double oldOffset = offsetParameter.AsDouble();
                double targetWorldElevation = oldLevel.Elevation + oldOffset;
                double newOffset = targetWorldElevation - newLevel.Elevation;

                if (levelParameter.AsElementId() == newLevel.Id && Math.Abs(oldOffset - newOffset) <= Tolerance)
                {
                    TaskDialog.Show("Basisebene wechseln", "Die ausgewählte Geschossdecke liegt bereits auf dieser Basisebene mit passendem Versatz.");
                    return Result.Succeeded;
                }

                using (Transaction tx = new Transaction(doc, "Basisebene der Geschossdecke wechseln"))
                {
                    tx.Start();
                    levelParameter.Set(newLevel.Id);
                    offsetParameter.Set(newOffset);
                    tx.Commit();
                }

                string oldOffsetText = FormatLength(doc, oldOffset);
                string newOffsetText = FormatLength(doc, newOffset);

                TaskDialog.Show(
                    "Basisebene wechseln",
                    "Basisebene wurde gewechselt.\n\n" +
                    $"Geschossdecke: Id {floor.Id.Value}\n" +
                    $"Alte Basisebene: {oldLevel.Name}\n" +
                    $"Neue Basisebene: {newLevel.Name}\n" +
                    $"Alter Versatz: {oldOffsetText}\n" +
                    $"Neuer Versatz: {newOffsetText}\n\n" +
                    "Die Höhenposition bleibt durch den neu berechneten Versatz erhalten.");

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Basisebene wechseln", "Fehler:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static Floor PickFloor(UIDocument uidoc, Document doc)
        {
            Reference reference = uidoc.Selection.PickObject(
                ObjectType.Element,
                new FloorSelectionFilter(),
                "Geschossdecke auswählen");

            if (doc.GetElement(reference.ElementId) is Floor floor)
                return floor;

            throw new InvalidOperationException("Es wurde keine Geschossdecke ausgewählt.");
        }

        private static Level PickLevel(UIDocument uidoc, Document doc)
        {
            Reference reference = uidoc.Selection.PickObject(
                ObjectType.Element,
                new LevelSelectionFilter(),
                "Neue Basisebene auswählen");

            if (doc.GetElement(reference.ElementId) is Level level)
                return level;

            throw new InvalidOperationException("Es wurde keine Ebene ausgewählt.");
        }

        private static string FormatLength(Document doc, double value)
        {
            try
            {
                return UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, value, false);
            }
            catch
            {
                double meters = UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Meters);
                return meters.ToString("F3") + " m";
            }
        }

        private sealed class FloorSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Floor;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        private sealed class LevelSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Level;
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
