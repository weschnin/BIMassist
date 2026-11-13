using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;
using BIMassist.Ava.Views;

namespace BIMassist.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class AvaQuantityTakeoffCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uiDocument = commandData.Application.ActiveUIDocument;
            if (uiDocument == null) throw new InvalidDataException("Zuerst ein Revit-Projekt öffnen.");
            var document = uiDocument.Document;
            if (document.IsFamilyDocument) throw new InvalidDataException("Mengenermittlung steht nur in einem Projekt, nicht im Familieneditor, zur Verfügung.");
            var workspace = new AvaWorkspace();
            AvaTemplate? template = null;
            if (workspace.HasRememberedTemplate)
            {
                try { template = workspace.LoadLast(); }
                catch (InvalidDataException ex)
                {
                    TaskDialog.Show("AVA – Mengenermittlung", "Die gespeicherte AVA-Vorlage ist nicht verfügbar oder ungültig. Wählen Sie eine gültige Vorlage.\n\n" + ex.Message);
                }
            }
            if (template == null)
            {
                var choose = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "AVA-Vorlage für die Mengenermittlung wählen", Filter = "AVA-Vorlage (*.json)|*.json|Alle Dateien (*.*)|*.*",
                    DefaultExt = ".json", FilterIndex = 1, CheckFileExists = true, Multiselect = false
                };
                if (ShowFileDialog(choose, commandData.Application) != true) return Result.Cancelled;
                template = TemplateJson.Load(choose.FileName); TemplateEditor.EnsureValid(template);
                workspace.Remember(choose.FileName);
            }
            var parameterChoices = new AvaParameterChoiceStore();
            var documentIdentity = string.IsNullOrWhiteSpace(document.PathName)
                ? "unsaved:" + document.Title : Path.GetFullPath(document.PathName);
            var result = QuantityTakeoff.Calculate(template, RevitQuantityReader.Elements(document, template,
                (element, source, candidates) =>
                {
                    var type = document.GetElement(element.GetTypeId());
                    var key = AvaParameterChoiceStore.Key(documentIdentity, template.ProfileId,
                        type?.UniqueId ?? element.UniqueId, source);
                    var remembered = parameterChoices.FindAvailable(key, candidates);
                    if (remembered != null) return remembered;

                    var dialog = new ParameterChoiceWindow(source.Name, candidates);
                    var handle = commandData.Application.MainWindowHandle;
                    if (handle != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(dialog).Owner = handle;
                    if (dialog.ShowDialog() != true || dialog.SelectedIdentity == null)
                        throw new OperationCanceledException("Parameterauswahl abgebrochen.");
                    if (dialog.RememberSelection) parameterChoices.Remember(key, dialog.SelectedIdentity);
                    return dialog.SelectedIdentity;
                }));
            if (result.Errors.Count > 0)
            {
                const int errorLimit = 15;
                message = "Mengenermittlung abgebrochen: " + result.Errors.Count + " Fehler. Keine X31-Datei erstellt oder ersetzt.\n\n"
                    + "Vorlage: " + template.Name + " (" + template.Version + ")\n" + workspace.LastTemplatePath + "\n\n"
                    + "Untersuchte Modellelemente: " + result.Examined
                    + "\nOhne vollständige BGK-/Typkennzeichnung: " + result.Unmarked
                    + "\nOhne passende BGK-/Typzuordnung: " + result.Unmapped
                    + "\nErmittelte Mengenzeilen (nicht exportiert): " + result.Rows.Count
                    + "\nVerknüpfte Modelle werden nicht ausgewertet."
                    + "\n\nFehlerdetails (erste " + Math.Min(errorLimit, result.Errors.Count) + " von " + result.Errors.Count + ", maximal " + errorLimit + "):\n"
                    + string.Join("\n\n", result.Errors.Take(errorLimit).Select(e => e.Message))
                    + (result.Errors.Count > errorLimit ? "\n" + (result.Errors.Count - errorLimit) + " weitere Fehler nicht angezeigt." : "");
                TaskDialog.Show("AVA – Mengenermittlung", message);
                return Result.Failed;
            }
            if (result.Rows.Count == 0)
            {
                TaskDialog.Show("AVA – Mengenermittlung", "Keine passenden Mengenzeilen gefunden.\nUntersuchte Modellelemente: " + result.Examined
                    + "\nOhne vollständige BGK-/Typkennzeichnung: " + result.Unmarked + "\nOhne passende BGK-/Typzuordnung: " + result.Unmapped
                    + "\nKeine Datei erstellt. Verknüpfte Modelle werden nicht ausgewertet.");
                return Result.Cancelled;
            }
            var grouping = AvaGroupingDialog.Show(commandData.Application, document);
            if (grouping == null) return Result.Cancelled;
            var labels = grouping.Mode switch
            {
                QuantityGroupingMode.Position => new Dictionary<string, string>(StringComparer.Ordinal),
                QuantityGroupingMode.FamilyAndType => RevitQuantityReader.ReadFamilyAndTypeLabels(document, result.Rows.Select(r => r.ElementId)),
                _ => RevitQuantityReader.ReadGroupingLabels(document, result.Rows.Select(r => r.ElementId), grouping.Parameter!)
            };
            var exportRows = result.Rows.Select(r => new QuantityTakeoffRow(r.LvItemId, r.Value, r.Unit, r.Explanation,
                r.ElementId, grouping.Mode == QuantityGroupingMode.Position ? null : labels[r.ElementId],
                grouping.Mode == QuantityGroupingMode.Position ? null : grouping.GroupKind)).ToList();
            var units = result.Rows.Where(r => template.LvCatalog.Entries.Single(l => l.Id == r.LvItemId).Unit != r.Unit)
                .Select(r => { var lv = template.LvCatalog.Entries.Single(l => l.Id == r.LvItemId); return lv.Ordinal + ": Menge " + r.Unit + " / LV-Einheit " + lv.Unit; })
                .Distinct(StringComparer.Ordinal).ToList();
            var warnings = TemplateValidator.Validate(template).Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
            var confirm = new TaskDialog("AVA – Mengenermittlung")
            {
                MainInstruction = "Mengenermittlung als X31 exportieren?",
                MainContent = "Projekt: " + document.Title + "\nVorlage: " + template.Name + "\n" + workspace.LastTemplatePath
                    + "\nGruppierung: " + (grouping.Mode == QuantityGroupingMode.Position ? "nach Leistungspositionen" : grouping.Mode == QuantityGroupingMode.FamilyAndType ? "nach Familienname und Typ" : grouping.GroupKind + " nach Parameter „" + grouping.Parameter!.DisplayLabel + "“")
                    + "\n\n" + result.Examined + " Modellelemente untersucht; " + result.Rows.Count + " Mengenzeilen."
                    + "\nOhne vollständige Kennzeichnung: " + result.Unmarked + "; ohne BGK-/Typzuordnung: " + result.Unmapped + "."
                    + "\n\nUmfang: alle Modellelement-Instanzen im aktuellen Hauptmodell, keine verknüpften Modelle. Der textuelle Prüfumfang ist kein Kategorienfilter; dies ist keine vollständige Modellprüfung."
                    + "\nAlle zutreffenden Regeln werden additiv ausgewertet. Verwendet wird die gespeicherte Vorlage. Keine Revit-Schreibzugriffe."
                    + (units.Count > 0 ? "\n\nACHTUNG: " + units.Count + " unterschiedliche Mengen-/LV-Einheiten. Keine automatische Umrechnung. Mit Ja werden die Zahlen unverändert unter der jeweiligen LV-Position ausgegeben; eigene Einheit steht im Erläuterungstext." : ""),
                ExpandedContent = string.Join("\n", units.Concat(warnings.Select(w => w.ToString()))),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            using var save = new FileSaveDialog("GAEB-Mengenermittlung (*.x31)|*.x31|Alle Dateien (*.*)|*.*")
            {
                Title = "AVA-Mengenermittlung speichern",
                DefaultFilterEntry = "*.x31",
                InitialFileName = "Mengenermittlung.x31"
            };
            if (save.Show() != ItemSelectionDialogResult.Confirmed) return Result.Cancelled;
            var selectedPath = save.GetSelectedModelPath();
            if (selectedPath == null) throw new InvalidDataException("Der Revit-Speicherdialog hat keinen Dateipfad zurückgegeben.");
            var outputPath = Path.GetFullPath(ModelPathUtils.ConvertModelPathToUserVisiblePath(selectedPath));
            if (string.Equals(outputPath, Path.GetFullPath(workspace.LastTemplatePath!), StringComparison.OrdinalIgnoreCase)
                || string.Equals(outputPath, Path.GetFullPath(AvaWorkspace.DefaultSettingsPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die X31-Ausgabe darf weder die aktive AVA-Vorlage noch die Arbeitsbereich-Einstellungen überschreiben.");
            X31Writer.Save(outputPath, template, exportRows);
            TaskDialog.Show("AVA – Mengenermittlung", "X31-Datei erstellt und zurückgelesen.\n" + result.Rows.Count + " Mengenzeilen.\n\n" + outputPath
                + "\n\nDer Import in California ist separat zu prüfen.");
            return Result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = "Die AVA-Mengenermittlung konnte nicht abgeschlossen werden.\n\n" + ex.Message;
            return Result.Failed;
        }
    }

    // Mirror WpfOwner's Revit HWND ownership without showing a second window.
    // WPF CommonDialog accepts a Window, not an arbitrary external HWND.
    private static bool? ShowFileDialog(Microsoft.Win32.CommonDialog dialog, UIApplication application)
    {
        var handle = application.MainWindowHandle;
        if (handle == IntPtr.Zero) return dialog.ShowDialog();
        var owner = new System.Windows.Window();
        try
        {
            new System.Windows.Interop.WindowInteropHelper(owner).Owner = handle;
            return dialog.ShowDialog(owner);
        }
        finally { owner.Close(); }
    }
}
