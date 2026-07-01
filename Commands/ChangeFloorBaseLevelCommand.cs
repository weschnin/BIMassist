using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Views;
using System;
using System.Collections.Generic;
using System.Linq;

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
                Element hostElement = PickSupportedElement(uidoc, doc);

                if (hostElement is Floor floor)
                    return ChangeFloorLevel(doc, floor);

                if (hostElement is Wall wall)
                    return ChangeWallLevels(doc, wall);

                if (IsPipeElement(hostElement))
                    return ChangePipeLevels(doc, hostElement);

                TaskDialog.Show("Basisebene wechseln", "Bitte eine Geschossdecke, Wand oder ein Rohr auswählen.");
                return Result.Cancelled;
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

        private static Result ChangeFloorLevel(Document doc, Floor floor)
        {
            Parameter? levelParameter = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            Parameter? offsetParameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);

            if (!IsWritableElementIdParameter(levelParameter))
            {
                TaskDialog.Show("Basisebene wechseln", "Die Basisebene der ausgewählten Geschossdecke kann nicht geändert werden.");
                return Result.Cancelled;
            }

            if (!IsWritableDoubleParameter(offsetParameter))
            {
                TaskDialog.Show("Basisebene wechseln", "Der Versatz der ausgewählten Geschossdecke kann nicht geändert werden.");
                return Result.Cancelled;
            }

            Level? oldLevel = GetLevel(doc, levelParameter!.AsElementId());
            if (oldLevel == null)
            {
                TaskDialog.Show("Basisebene wechseln", "Die aktuelle Basisebene der Geschossdecke konnte nicht ermittelt werden.");
                return Result.Cancelled;
            }

            Level? newLevel = SelectLevelFromList(doc, "Neue Basisebene für die Geschossdecke auswählen", oldLevel.Id);
            if (newLevel == null)
                return Result.Cancelled;

            double oldOffset = offsetParameter!.AsDouble();
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

            TaskDialog.Show(
                "Basisebene wechseln",
                "Basisebene wurde gewechselt.\n\n" +
                $"Geschossdecke: Id {floor.Id.Value}\n" +
                $"Alte Basisebene: {oldLevel.Name}\n" +
                $"Neue Basisebene: {newLevel.Name}\n" +
                $"Alter Versatz: {FormatLength(doc, oldOffset)}\n" +
                $"Neuer Versatz: {FormatLength(doc, newOffset)}\n\n" +
                "Die Höhenposition bleibt durch den neu berechneten Versatz erhalten.");

            return Result.Succeeded;
        }

        private static Result ChangeWallLevels(Document doc, Wall wall)
        {
            Parameter? baseLevelParameter = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
            Parameter? baseOffsetParameter = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            Parameter? topLevelParameter = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
            Parameter? topOffsetParameter = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);

            if (!IsWritableElementIdParameter(baseLevelParameter) || !IsWritableDoubleParameter(baseOffsetParameter))
            {
                TaskDialog.Show("Basisebene wechseln", "Die untere Ebene oder der untere Versatz der ausgewählten Wand kann nicht geändert werden.");
                return Result.Cancelled;
            }

            if (!IsWritableElementIdParameter(topLevelParameter) || !IsWritableDoubleParameter(topOffsetParameter))
            {
                TaskDialog.Show(
                    "Basisebene wechseln",
                    "Die obere Ebene oder der obere Versatz der ausgewählten Wand kann nicht geändert werden.\n\n" +
                    "Wände werden nur unterstützt, wenn oben und unten jeweils eine Ebene als Abhängigkeit eingetragen ist.");
                return Result.Cancelled;
            }

            Level? oldBaseLevel = GetLevel(doc, baseLevelParameter!.AsElementId());
            Level? oldTopLevel = GetLevel(doc, topLevelParameter!.AsElementId());

            if (oldBaseLevel == null || oldTopLevel == null)
            {
                TaskDialog.Show(
                    "Basisebene wechseln",
                    "Die aktuelle untere oder obere Ebene der Wand konnte nicht ermittelt werden.\n\n" +
                    "Wände mit nicht verbundener Höhe werden in dieser Funktion nicht geändert.");
                return Result.Cancelled;
            }

            Level? newBaseLevel = SelectLevelFromList(doc, "Neue untere Ebene für die Wand auswählen", oldBaseLevel.Id);
            if (newBaseLevel == null)
                return Result.Cancelled;

            bool hasDifferentTopAndBaseLevels = oldTopLevel.Id != oldBaseLevel.Id;
            Level? newTopLevel = newBaseLevel;

            if (hasDifferentTopAndBaseLevels)
            {
                newTopLevel = SelectLevelFromList(doc, "Neue obere Ebene für die Wand auswählen", oldTopLevel.Id);
                if (newTopLevel == null)
                    return Result.Cancelled;
            }

            double oldBaseOffset = baseOffsetParameter!.AsDouble();
            double oldTopOffset = topOffsetParameter!.AsDouble();
            double baseWorldElevation = oldBaseLevel.Elevation + oldBaseOffset;
            double topWorldElevation = oldTopLevel.Elevation + oldTopOffset;
            double newBaseOffset = baseWorldElevation - newBaseLevel.Elevation;
            double newTopOffset = topWorldElevation - newTopLevel.Elevation;

            bool alreadyOnTarget = baseLevelParameter.AsElementId() == newBaseLevel.Id
                && topLevelParameter.AsElementId() == newTopLevel.Id
                && Math.Abs(oldBaseOffset - newBaseOffset) <= Tolerance
                && Math.Abs(oldTopOffset - newTopOffset) <= Tolerance;

            if (alreadyOnTarget)
            {
                TaskDialog.Show("Basisebene wechseln", "Die ausgewählte Wand liegt bereits auf den gewählten Ebenen mit passenden Versätzen.");
                return Result.Succeeded;
            }

            using (Transaction tx = new Transaction(doc, "Ebenen der Wand wechseln"))
            {
                tx.Start();
                baseLevelParameter.Set(newBaseLevel.Id);
                baseOffsetParameter.Set(newBaseOffset);
                topLevelParameter.Set(newTopLevel.Id);
                topOffsetParameter.Set(newTopOffset);
                tx.Commit();
            }

            string selectionNote = hasDifferentTopAndBaseLevels
                ? "Für die Wand wurden untere und obere Ebene separat gewählt."
                : "Untere und obere Ebene waren gleich; die neue Ebene wurde für beide Abhängigkeiten verwendet.";

            TaskDialog.Show(
                "Basisebene wechseln",
                "Wand-Ebenen wurden gewechselt.\n\n" +
                $"Wand: Id {wall.Id.Value}\n" +
                $"Alte untere Ebene: {oldBaseLevel.Name}\n" +
                $"Neue untere Ebene: {newBaseLevel.Name}\n" +
                $"Alter unterer Versatz: {FormatLength(doc, oldBaseOffset)}\n" +
                $"Neuer unterer Versatz: {FormatLength(doc, newBaseOffset)}\n\n" +
                $"Alte obere Ebene: {oldTopLevel.Name}\n" +
                $"Neue obere Ebene: {newTopLevel.Name}\n" +
                $"Alter oberer Versatz: {FormatLength(doc, oldTopOffset)}\n" +
                $"Neuer oberer Versatz: {FormatLength(doc, newTopOffset)}\n\n" +
                selectionNote + "\n" +
                "Die Höhenpositionen bleiben durch die neu berechneten Versätze erhalten.");

            return Result.Succeeded;
        }

        private static Result ChangePipeLevels(Document doc, Element pipeElement)
        {
            Parameter? startLevelParameter = pipeElement.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
            Parameter? startOffsetParameter = pipeElement.get_Parameter(BuiltInParameter.RBS_START_OFFSET_PARAM);
            Parameter? endLevelParameter = pipeElement.get_Parameter(BuiltInParameter.RBS_END_LEVEL_PARAM);
            Parameter? endOffsetParameter = pipeElement.get_Parameter(BuiltInParameter.RBS_END_OFFSET_PARAM);

            bool hasStartEndLevelParameters = IsWritableElementIdParameter(startLevelParameter)
                && IsWritableDoubleParameter(startOffsetParameter)
                && IsWritableElementIdParameter(endLevelParameter)
                && IsWritableDoubleParameter(endOffsetParameter)
                && GetLevel(doc, startLevelParameter!.AsElementId()) != null
                && GetLevel(doc, endLevelParameter!.AsElementId()) != null;

            if (hasStartEndLevelParameters)
                return ChangePipeStartEndLevels(doc, pipeElement, startLevelParameter!, startOffsetParameter!, endLevelParameter!, endOffsetParameter!);

            Parameter? referenceLevelParameter = pipeElement.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM);
            Parameter? offsetParameter = pipeElement.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);

            if (!IsWritableElementIdParameter(referenceLevelParameter) || !IsWritableDoubleParameter(offsetParameter))
            {
                TaskDialog.Show(
                    "Basisebene wechseln",
                    "Die Ebene oder der Versatz des ausgewählten Rohrs kann nicht geändert werden.\n\n" +
                    "Unterstützt werden Rohre mit schreibbarer Referenzebene und schreibbarem Versatz.");
                return Result.Cancelled;
            }

            Level? oldLevel = GetLevel(doc, referenceLevelParameter!.AsElementId());
            if (oldLevel == null)
            {
                TaskDialog.Show("Basisebene wechseln", "Die aktuelle Referenzebene des Rohrs konnte nicht ermittelt werden.");
                return Result.Cancelled;
            }

            Level? newLevel = SelectLevelFromList(doc, "Neue Referenzebene für das Rohr auswählen", oldLevel.Id);
            if (newLevel == null)
                return Result.Cancelled;

            double oldOffset = offsetParameter!.AsDouble();
            double targetWorldElevation = oldLevel.Elevation + oldOffset;
            double newOffset = targetWorldElevation - newLevel.Elevation;

            if (referenceLevelParameter.AsElementId() == newLevel.Id && Math.Abs(oldOffset - newOffset) <= Tolerance)
            {
                TaskDialog.Show("Basisebene wechseln", "Das ausgewählte Rohr liegt bereits auf dieser Referenzebene mit passendem Versatz.");
                return Result.Succeeded;
            }

            using (Transaction tx = new Transaction(doc, "Referenzebene des Rohrs wechseln"))
            {
                tx.Start();
                referenceLevelParameter.Set(newLevel.Id);
                offsetParameter.Set(newOffset);
                tx.Commit();
            }

            TaskDialog.Show(
                "Basisebene wechseln",
                "Rohr-Referenzebene wurde gewechselt.\n\n" +
                $"Rohr: Id {pipeElement.Id.Value}\n" +
                $"Alte Referenzebene: {oldLevel.Name}\n" +
                $"Neue Referenzebene: {newLevel.Name}\n" +
                $"Alter Versatz: {FormatLength(doc, oldOffset)}\n" +
                $"Neuer Versatz: {FormatLength(doc, newOffset)}\n\n" +
                "Die Höhenposition bleibt durch den neu berechneten Versatz erhalten.");

            return Result.Succeeded;
        }

        private static Result ChangePipeStartEndLevels(
            Document doc,
            Element pipeElement,
            Parameter startLevelParameter,
            Parameter startOffsetParameter,
            Parameter endLevelParameter,
            Parameter endOffsetParameter)
        {
            Level oldStartLevel = GetLevel(doc, startLevelParameter.AsElementId())!;
            Level oldEndLevel = GetLevel(doc, endLevelParameter.AsElementId())!;

            Level? newStartLevel = SelectLevelFromList(doc, "Neue Startebene für das Rohr auswählen", oldStartLevel.Id);
            if (newStartLevel == null)
                return Result.Cancelled;

            bool hasDifferentStartAndEndLevels = oldStartLevel.Id != oldEndLevel.Id;
            Level? newEndLevel = newStartLevel;

            if (hasDifferentStartAndEndLevels)
            {
                newEndLevel = SelectLevelFromList(doc, "Neue Endebene für das Rohr auswählen", oldEndLevel.Id);
                if (newEndLevel == null)
                    return Result.Cancelled;
            }

            double oldStartOffset = startOffsetParameter.AsDouble();
            double oldEndOffset = endOffsetParameter.AsDouble();
            double startWorldElevation = oldStartLevel.Elevation + oldStartOffset;
            double endWorldElevation = oldEndLevel.Elevation + oldEndOffset;
            double newStartOffset = startWorldElevation - newStartLevel.Elevation;
            double newEndOffset = endWorldElevation - newEndLevel.Elevation;

            bool alreadyOnTarget = startLevelParameter.AsElementId() == newStartLevel.Id
                && endLevelParameter.AsElementId() == newEndLevel.Id
                && Math.Abs(oldStartOffset - newStartOffset) <= Tolerance
                && Math.Abs(oldEndOffset - newEndOffset) <= Tolerance;

            if (alreadyOnTarget)
            {
                TaskDialog.Show("Basisebene wechseln", "Das ausgewählte Rohr liegt bereits auf den gewählten Ebenen mit passenden Versätzen.");
                return Result.Succeeded;
            }

            using (Transaction tx = new Transaction(doc, "Ebenen des Rohrs wechseln"))
            {
                tx.Start();
                startLevelParameter.Set(newStartLevel.Id);
                startOffsetParameter.Set(newStartOffset);
                endLevelParameter.Set(newEndLevel.Id);
                endOffsetParameter.Set(newEndOffset);
                tx.Commit();
            }

            string selectionNote = hasDifferentStartAndEndLevels
                ? "Für das Rohr wurden Start- und Endebene separat gewählt."
                : "Start- und Endebene waren gleich; die neue Ebene wurde für beide Abhängigkeiten verwendet.";

            TaskDialog.Show(
                "Basisebene wechseln",
                "Rohr-Ebenen wurden gewechselt.\n\n" +
                $"Rohr: Id {pipeElement.Id.Value}\n" +
                $"Alte Startebene: {oldStartLevel.Name}\n" +
                $"Neue Startebene: {newStartLevel.Name}\n" +
                $"Alter Startversatz: {FormatLength(doc, oldStartOffset)}\n" +
                $"Neuer Startversatz: {FormatLength(doc, newStartOffset)}\n\n" +
                $"Alte Endebene: {oldEndLevel.Name}\n" +
                $"Neue Endebene: {newEndLevel.Name}\n" +
                $"Alter Endversatz: {FormatLength(doc, oldEndOffset)}\n" +
                $"Neuer Endversatz: {FormatLength(doc, newEndOffset)}\n\n" +
                selectionNote + "\n" +
                "Die Höhenpositionen bleiben durch die neu berechneten Versätze erhalten.");

            return Result.Succeeded;
        }

        private static Element PickSupportedElement(UIDocument uidoc, Document doc)
        {
            Reference reference = uidoc.Selection.PickObject(
                ObjectType.Element,
                new SupportedElementSelectionFilter(),
                "Geschossdecke, Wand oder Rohr auswählen");

            Element element = doc.GetElement(reference.ElementId);
            if (element is Floor || element is Wall || IsPipeElement(element))
                return element;

            throw new InvalidOperationException("Es wurde keine Geschossdecke, Wand oder kein Rohr ausgewählt.");
        }

        private static Level? SelectLevelFromList(Document doc, string prompt, ElementId? preferredLevelId)
        {
            List<LevelPickerWindow.LevelOption> levelOptions = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .Select(level => new LevelPickerWindow.LevelOption(
                    level.Id,
                    level.Name,
                    level.Elevation,
                    FormatLength(doc, level.Elevation)))
                .ToList();

            if (levelOptions.Count == 0)
            {
                TaskDialog.Show("Ebene auswählen", "Im aktuellen Dokument wurden keine Ebenen gefunden.");
                return null;
            }

            LevelPickerWindow picker = new LevelPickerWindow(levelOptions, prompt, preferredLevelId);
            bool? dialogResult = picker.ShowDialog();
            if (dialogResult != true || picker.SelectedLevelId == null)
                return null;

            return GetLevel(doc, picker.SelectedLevelId);
        }

        private static Level? GetLevel(Document doc, ElementId levelId)
        {
            if (levelId == ElementId.InvalidElementId)
                return null;

            return doc.GetElement(levelId) as Level;
        }

        private static bool IsWritableElementIdParameter(Parameter? parameter)
        {
            return parameter != null
                && !parameter.IsReadOnly
                && parameter.StorageType == StorageType.ElementId;
        }

        private static bool IsWritableDoubleParameter(Parameter? parameter)
        {
            return parameter != null
                && !parameter.IsReadOnly
                && parameter.StorageType == StorageType.Double;
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

        private static BuiltInCategory? TryGetBuiltInCategory(ElementId? categoryId)
        {
            if (categoryId == null || categoryId == ElementId.InvalidElementId)
                return null;

            long value = categoryId.Value;
            if (Enum.IsDefined(typeof(BuiltInCategory), value))
                return (BuiltInCategory)Enum.ToObject(typeof(BuiltInCategory), value);

            return null;
        }

        private static bool IsPipeElement(Element element)
        {
            if (element == null)
                return false;

            if (element is Pipe || element is FlexPipe)
                return true;

            BuiltInCategory? category = TryGetBuiltInCategory(element.Category?.Id);
            return category == BuiltInCategory.OST_PipeCurves
                || category == BuiltInCategory.OST_FlexPipeCurves
                || category == BuiltInCategory.OST_PlaceHolderPipes;
        }

        private sealed class SupportedElementSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Floor || elem is Wall || IsPipeElement(elem);
            }

            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
