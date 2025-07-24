using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BIMassist.Fenster;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    internal class DeleteUnusedMaterialsWpfCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // 1. Alle Materialien sammeln
            var allMaterials = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();

            // 2. Verwendete Materialien finden
            var usedIds = GetUsedMaterialIds(doc);

            // 3. Unbenutzte Materialien finden
            var unused = allMaterials
                .Where(m => !usedIds.Contains(m.Id))
                .OrderBy(m => m.Name)
                .ToList();

            if (unused.Count == 0)
            {
                TaskDialog.Show("Materialien löschen", "Keine unbenutzten Materialien gefunden.");
                return Result.Succeeded;
            }

            // 4. WPF-Fenster anzeigen
            var dialog = new DeleteMaterialsDialog(unused);
            bool? dlgResult = dialog.ShowDialog();

            if (dlgResult != true || dialog.SelectedMaterials.Count == 0)
                return Result.Cancelled;

            // 5. Löschen nach Bestätigung
            using (Transaction t = new Transaction(doc, "Unbenutzte Materialien löschen"))
            {
                t.Start();
                foreach (var mat in dialog.SelectedMaterials)
                {
                    try { doc.Delete(mat.Id); } catch { /* Fehler ignorieren */ }
                }
                t.Commit();
            }

            TaskDialog.Show("Fertig", $"{dialog.SelectedMaterials.Count} Materialien gelöscht.");
            return Result.Succeeded;
        }

        private HashSet<ElementId> GetUsedMaterialIds(Document doc)
        {
            var usedMaterialIds = new HashSet<ElementId>();

            // Geometrie-basierte Materialien
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            foreach (Element elem in collector)
            {
                foreach (ElementId mid in elem.GetMaterialIds(false))
                {
                    usedMaterialIds.Add(mid);
                }
                // Zusätzlich Material-Parameter
                foreach (Parameter param in elem.Parameters)
                {
                    if (param.StorageType == StorageType.ElementId && param.HasValue)
                    {
                        ElementId mid = param.AsElementId();
                        // Ggf. noch prüfen: Ist das ein Material? Meist ok, da fast nur Material-Parameter so gespeichert werden
                        if (mid != null && mid != ElementId.InvalidElementId && mid.Value > 0)
                            usedMaterialIds.Add(mid);
                    }
                }
            }

            // Materialien von Typen (Walls, Floors, etc.)
            var types = new FilteredElementCollector(doc).WhereElementIsElementType();
            foreach (Element typeElem in types)
            {
                foreach (Parameter param in typeElem.Parameters)
                {
                    if (param.StorageType == StorageType.ElementId && param.HasValue)
                    {
                        ElementId mid = param.AsElementId();
                        if (mid != null && mid != ElementId.InvalidElementId && mid.Value > 0)
                            usedMaterialIds.Add(mid);
                    }
                }
            }

            return usedMaterialIds;
        }
    }
}
