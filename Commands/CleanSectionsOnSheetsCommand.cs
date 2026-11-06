using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;
using System.Text;
using RevitView = Autodesk.Revit.DB.View;

namespace BIMassist.Commands;

/// <summary>Port of the sheet-specific section-marker visibility Dynamo workflow.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class CleanSectionsOnSheetsCommand : IExternalCommand
{
    private static readonly HashSet<ViewType> SupportedViews =
    [
        ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan,
        ViewType.Section, ViewType.Elevation
    ];

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document? document = commandData.Application.ActiveUIDocument?.Document;
        if (document == null || document.IsFamilyDocument || document.IsReadOnly)
        {
            message = "Bitte ein bearbeitbares Revit-Projekt öffnen.";
            return Result.Failed;
        }

        TaskDialog choice = new("Schnitte bereinigen")
        {
            MainInstruction = "Welche Pläne sollen bearbeitet werden?",
            MainContent = "Ja: nur der aktuell geöffnete Plan\nNein: alle Pläne im Projekt\nAbbrechen: keine Änderung",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Yes
        };
        TaskDialogResult answer = choice.Show();
        if (answer == TaskDialogResult.Cancel) return Result.Cancelled;
        if (answer != TaskDialogResult.Yes && answer != TaskDialogResult.No) return Result.Cancelled;

        List<ViewSheet> sheets;
        if (answer == TaskDialogResult.Yes)
        {
            if (document.ActiveView is not ViewSheet activeSheet)
            {
                TaskDialog.Show("Schnitte bereinigen", "Der aktuell geöffnete View ist kein Plan. Bitte einen Plan öffnen oder 'alle Pläne' wählen.");
                return Result.Cancelled;
            }
            sheets = [activeSheet];
        }
        else
        {
            sheets = new FilteredElementCollector(document).OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>().OrderBy(sheet => sheet.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
        }
        if (sheets.Count == 0)
        {
            TaskDialog.Show("Schnitte bereinigen", "Keine Pläne im Projekt vorhanden; nichts geändert.");
            return Result.Cancelled;
        }

        try
        {
            List<RevitView> views = new FilteredElementCollector(document).OfClass(typeof(RevitView))
                .Cast<RevitView>().Where(view => !view.IsTemplate).ToList();
            Dictionary<long, RevitView> viewsById = views.ToDictionary(view => view.Id.Value);
            Dictionary<string, IReadOnlyCollection<long>> sectionIdsByName = views
                .Where(view => view.ViewType == ViewType.Section)
                .GroupBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyCollection<long>)group.Select(view => view.Id.Value).Distinct().ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var report = new Report();
            using Transaction transaction = new(document, "Schnittmarker auf Plänen bereinigen");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Die Transaktion konnte nicht gestartet werden.");
            try
            {
                foreach (ViewSheet sheet in sheets)
                {
                    HashSet<long> sheetViewIds = sheet.GetAllPlacedViews().Select(id => id.Value).ToHashSet();
                    report.Sheets++;
                    foreach (long viewId in sheetViewIds)
                    {
                        if (!viewsById.TryGetValue(viewId, out RevitView? view)
                            || !SupportedViews.Contains(view.ViewType)) continue;
                        report.Views++;
                        ProcessView(document, view, sheetViewIds, sectionIdsByName, report);
                    }
                }
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Die Änderungen konnten nicht übernommen werden.");
            }
            catch
            {
                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
                throw;
            }
            TaskDialog.Show("Schnitte bereinigen", report.ToText());
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("Schnitte bereinigen", "Keine erfolgreiche Gesamtbearbeitung:\n" + ex.Message);
            return Result.Failed;
        }
    }

    private static void ProcessView(Document document, RevitView view,
        IReadOnlySet<long> sheetViewIds,
        IReadOnlyDictionary<string, IReadOnlyCollection<long>> sectionIdsByName,
        Report report)
    {
        if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryViewProperties))
            throw new InvalidOperationException($"Ansicht '{view.Name}' nutzt temporäre Ansichtseigenschaften. Bitte den Modus zuerst beenden; keine Schnittmarker wurden dauerhaft geändert.");
        bool wasRevealing = view.IsInTemporaryViewMode(TemporaryViewMode.RevealHiddenElements);
        bool cropWasVisible = view.CropBoxVisible;
        bool revealEnabledHere = false;
        try
        {
            if (!wasRevealing)
            {
                view.EnableRevealHiddenMode();
                revealEnabledHere = true;
                document.Regenerate();
            }
            var markers = new FilteredElementCollector(document, view.Id)
                .OfCategory(BuiltInCategory.OST_Viewers).WhereElementIsNotElementType().ToElements();
            foreach (Element marker in markers)
            {
                MarkerUpdate update = SheetSectionVisibilityPolicy.Decide(marker.Name,
                    sectionIdsByName, sheetViewIds, marker.IsHidden(view));
                if (update == MarkerUpdate.None) continue;
                if (update == MarkerUpdate.Ambiguous)
                {
                    report.Ambiguous++;
                    continue; // Never guess which of multiple same-named sections the marker denotes.
                }
                if (update == MarkerUpdate.Hide && !marker.CanBeHidden(view))
                {
                    report.Unchanged++;
                    continue;
                }
                var ids = new List<ElementId> { marker.Id };
                if (update == MarkerUpdate.Hide)
                {
                    view.HideElements(ids);
                    report.Hidden++;
                }
                else
                {
                    view.UnhideElements(ids);
                    report.Unhidden++;
                }
            }
        }
        finally
        {
            if (revealEnabledHere)
                view.DisableTemporaryViewMode(TemporaryViewMode.RevealHiddenElements);
            if (view.CropBoxVisible != cropWasVisible)
                view.CropBoxVisible = cropWasVisible;
        }
    }

    private sealed class Report
    {
        internal int Sheets, Views, Hidden, Unhidden, Ambiguous, Unchanged;
        internal string ToText()
        {
            StringBuilder text = new();
            text.AppendLine($"Pläne: {Sheets}; Ansichten: {Views}");
            text.AppendLine($"Schnittmarker ausgeblendet: {Hidden}; eingeblendet: {Unhidden}");
            if (Ambiguous > 0)
                text.AppendLine($"Nicht bearbeitete Marker mit mehrdeutigem Schnittnamen: {Ambiguous}");
            if (Unchanged > 0)
                text.AppendLine($"Nicht ausblendbare Marker: {Unchanged}");
            return text.ToString();
        }
    }
}
