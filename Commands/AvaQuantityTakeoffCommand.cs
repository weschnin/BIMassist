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

[Transaction(TransactionMode.Manual)]
public sealed class AvaQuantityTakeoffCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        => ExecuteCore(commandData.Application, null, null, out message);

    internal static Result Recalculate(UIApplication application, bool visibleOnly, Action<AvaQuantityRun> present, out string message)
        => ExecuteCore(application, visibleOnly, present, out message);

    // A supplied scope requests a fresh calculation; only ribbon execution reopens the saved run.
    private static Result ExecuteCore(UIApplication application, bool? requestedVisibleOnly, Action<AvaQuantityRun>? present, out string message)
    {
        message = "";
        string? scopeDescription = null;
        try
        {
            if (requestedVisibleOnly.HasValue && present == null) throw new ArgumentNullException(nameof(present));
            var uiDocument = application.ActiveUIDocument;
            if (uiDocument == null) throw new InvalidDataException("Zuerst ein Revit-Projekt öffnen.");
            var document = uiDocument.Document;
            if (document.IsFamilyDocument) throw new InvalidDataException("Mengenermittlung steht nur in einem Projekt, nicht im Familieneditor, zur Verfügung.");
            if (!requestedVisibleOnly.HasValue)
            {
                var savedRun = AvaQuantityRunStorage.Load(document);
                if (savedRun != null)
                {
                    AvaQuantityRunWindowHost.Show(application, document, savedRun);
                    return Result.Succeeded;
                }
            }
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
                if (ShowFileDialog(choose, application) != true) return Result.Cancelled;
                template = TemplateJson.Load(choose.FileName); TemplateEditor.EnsureValid(template);
                workspace.Remember(choose.FileName);
            }
            var parameterChoices = new AvaParameterChoiceStore();
            var activeView = uiDocument.ActiveView;
            var grouping = requestedVisibleOnly.HasValue
                ? new QuantityGroupingSelection(QuantityGroupingMode.Position, VisibleOnly: requestedVisibleOnly.Value)
                : AvaGroupingDialog.Show(application, document, activeView);
            if (grouping == null) return Result.Cancelled;
            var visibleOnly = grouping.VisibleOnly;
            scopeDescription = visibleOnly
                ? "Revit-Ansichtssichtbarkeit in „" + activeView.Name + "“ (dauerhaft in Ansicht oder Kategorie sowie temporär ausgeblendete Elemente ausgeschlossen; Zoom-/Pan-Bildausschnitt nicht maßgeblich; Elemente außerhalb des Zuschneidebereichs können dennoch enthalten sein; keine pixelgenaue Sichtbarkeits- oder Verdeckungsprüfung)"
                : "alle Modellelement-Instanzen im aktuellen Hauptmodell";
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
                    var handle = application.MainWindowHandle;
                    if (handle != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(dialog).Owner = handle;
                    if (dialog.ShowDialog() != true || dialog.SelectedIdentity == null)
                        throw new OperationCanceledException("Parameterauswahl abgebrochen.");
                    if (dialog.RememberSelection) parameterChoices.Remember(key, dialog.SelectedIdentity);
                    return dialog.SelectedIdentity;
                }, visibleOnly ? activeView.Id : null));
            if (result.Errors.Count > 0)
            {
                const int errorLimit = 15;
                message = "Mengenermittlung abgebrochen: " + result.Errors.Count + " Fehler. Keine X31-Datei erstellt oder ersetzt.\n\n"
                    + TemplateDiagnostics(template, workspace) + "\n\n"
                    + ResultDiagnostics(result, failed: true)
                    + "\n" + SkipDiagnostics(result)
                    + "\nUmfang: " + scopeDescription
                    + "\nVerknüpfte Modelle werden nicht ausgewertet."
                    + "\n\nFehlerdetails (erste " + Math.Min(errorLimit, result.Errors.Count) + " von " + result.Errors.Count + ", maximal " + errorLimit + "):\n"
                    + string.Join("\n\n", result.Errors.Take(errorLimit).Select(e => e.Message))
                    + (result.Errors.Count > errorLimit ? "\n" + (result.Errors.Count - errorLimit) + " weitere Fehler nicht angezeigt." : "");
                TaskDialog.Show("AVA – Mengenermittlung", message);
                return Result.Failed;
            }
            if (result.Rows.Count == 0)
            {
                TaskDialog.Show("AVA – Mengenermittlung", "Keine passenden Mengenzeilen gefunden.\n" + TemplateDiagnostics(template, workspace)
                    + "\n" + ResultDiagnostics(result)
                    + "\n" + SkipDiagnostics(result)
                    + "\nUmfang: " + scopeDescription
                    + "\nKeine Datei erstellt. Verknüpfte Modelle werden nicht ausgewertet.");
                return Result.Cancelled;
            }
            var labels = grouping.Mode switch
            {
                QuantityGroupingMode.Position => new Dictionary<string, string>(StringComparer.Ordinal),
                QuantityGroupingMode.FamilyAndType => RevitQuantityReader.ReadFamilyAndTypeLabels(document, result.Rows.Select(r => r.ElementId)),
                _ => RevitQuantityReader.ReadGroupingLabels(document, result.Rows.Select(r => r.ElementId), grouping.Parameter!)
            };
            var groupingParameter = grouping.Mode switch
            {
                QuantityGroupingMode.Position => "",
                QuantityGroupingMode.FamilyAndType => "Familie und Typ",
                _ => grouping.Parameter!.DisplayLabel
            };
            var run = AvaQuantityRunFactory.Create(template, result, document.Title,
                string.IsNullOrWhiteSpace(document.PathName) ? "" : Path.GetFullPath(document.PathName),
                scopeDescription!, Guid.NewGuid(), DateTimeOffset.UtcNow, groupingParameter, labels,
                RevitQuantityReader.ReadGroupingSnapshots(document, result.Rows.Select(row => row.ElementId)))
                with { VisibleOnly = visibleOnly };
            using (var storageTransaction = new Transaction(document, "BIMassist AVA letzter Mengenermittlungslauf"))
            {
                if (storageTransaction.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Die Transaktion zum Speichern des letzten Mengenermittlungslaufs konnte nicht gestartet werden.");
                // Resolve failures before Commit returns; do not leave a modeless Pending transaction.
                storageTransaction.SetFailureHandlingOptions(storageTransaction.GetFailureHandlingOptions().SetForcedModalHandling(true));
                AvaQuantityRunStorage.Save(document, run);
                if (storageTransaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Der letzte Mengenermittlungslauf wurde nicht im Revit-Projekt bestätigt.");
            }
            var persistedRun = AvaQuantityRunStorage.Load(document)
                ?? throw new InvalidDataException("Der gerade gespeicherte Mengenermittlungslauf konnte nicht zurückgelesen werden.");
            var warnings = TemplateValidator.Validate(template).Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
            if (warnings.Count > 0 || result.Skipped.Count > 0)
            {
                var details = new System.Collections.Generic.List<string>();
                details.Add(TemplateDiagnostics(template, workspace));
                details.Add(ResultDiagnostics(result));
                details.Add("Umfang: " + scopeDescription + "\nVerknüpfte Modelle werden nicht ausgewertet.");
                if (warnings.Count > 0)
                {
                    details.Add("Vorlagenhinweise (gesamte Vorlage; keine zusätzlichen Modellelemente):");
                    details.AddRange(warnings.Select(warning => warning.ToString()));
                }
                if (result.Skipped.Count > 0)
                {
                    details.Add(SkipDiagnostics(result));
                }
                TaskDialog.Show("AVA – Mengenermittlung Hinweise", string.Join("\n", details));
            }
            if (present != null) present(persistedRun);
            else AvaQuantityRunWindowHost.Show(application, document, persistedRun);
            return Result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = "Die AVA-Mengenermittlung konnte nicht abgeschlossen werden.\n\n" + ex.Message
                + (scopeDescription == null ? "" : "\n\nUmfang: " + scopeDescription);
            return Result.Failed;
        }
    }

    private static string ResultDiagnostics(TakeoffResult result, bool failed = false) =>
        "Untersuchte Modellelemente: " + result.Examined + " (verschiedene ElementIDs)"
        + "\nMit passender BGK-/Typzuordnung: " + result.MatchedElements + " (vor Bedingungsprüfung)"
        + "\nElemente mit Mengenzeile: " + result.MeasuredElements + " (verschiedene ElementIDs)"
        + (failed ? "\nErmittelte Mengenzeilen (nicht exportiert): " : "\nErmittelte Mengenzeilen: ") + result.Rows.Count
        + "\nElemente ohne Mengenzeile: " + (result.Examined - result.MeasuredElements)
        + "\nOhne vollständige BGK-/Typkennzeichnung: " + result.Unmarked
        + "\nOhne passende BGK-/Typzuordnung: " + result.Unmapped
        + "\nEin Element kann mehrere Mengenzeilen erzeugen (mehrere Regeln/LV-Positionen); Mengenzeilen sind keine Elementanzahl.";

    private static string SkipDiagnostics(TakeoffResult result) =>
        "Elemente ohne Mengenzeile – Diagnosedetails: " + result.Skipped.Count + " von " + result.SkippedTotal
        + " (maximal 100):\n" + string.Join("\n", result.Skipped.Select(skip => skip.Message))
        + (result.SkippedTotal > result.Skipped.Count
            ? "\n" + (result.SkippedTotal - result.Skipped.Count) + " weitere Elemente ohne Mengenzeile nicht angezeigt." : "");

    private static string TemplateDiagnostics(AvaTemplate template, AvaWorkspace workspace) =>
        "Vorlage (benutzerweit zuletzt gewählt, nicht projektgebunden): " + template.Name + " (" + template.Version + ")\n"
        + workspace.LastTemplatePath + "\nBGK-Quelle: " + template.BgkIdentification
        + "\nTyp-Quelle: " + template.TypeIdentification + "\nKeine automatische Typ-/Instanz-Ausweichsuche.";

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
