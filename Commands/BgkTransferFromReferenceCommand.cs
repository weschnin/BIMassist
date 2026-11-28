using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WpfComboBox = System.Windows.Controls.ComboBox;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;
using BIMassist.Core;

namespace BIMassist.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class BgkTransferFromReferenceCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var session = new BgkReferenceImportSession(commandData.Application, _ => { });
        try { return session.RunInContext(commandData.Application, commandData.Application.ActiveUIDocument?.Document, out message); }
        finally { session.Close(); }
    }
}

/// <summary>Shared API-context workflow, used by the manager and the compatibility command.</summary>
internal static class BgkReferenceImportWorkflow
{
    internal static Result Run(UIApplication uiapp, Document? targetDocument, BgkReferenceImportWriter writer, ref string message,
        Func<UIApplication, IReadOnlyList<Document>, Document?>? selectReference = null)
    {
        try
        {
            var activeDocument = uiapp.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("Zuerst ein Revit-Projekt öffnen.");
            if (targetDocument == null || !targetDocument.IsValidObject || !targetDocument.Equals(activeDocument))
                throw new InvalidOperationException("Das Projekt wurde seit dem Klick gewechselt oder geschlossen. Bitte erneut auslösen.");
            if (activeDocument.IsFamilyDocument)
                throw new InvalidOperationException("Die BGK-Übernahme ist nur in einem Revit-Projekt verfügbar.");
            var references = uiapp.Application.Documents.Cast<Document>()
                .Where(document => !document.Equals(activeDocument) && !document.IsFamilyDocument).ToArray();
            if (references.Length == 0)
                throw new InvalidOperationException("Es ist kein weiteres geöffnetes Projekt als Referenz verfügbar.");
            var reference = (selectReference ?? SelectReferenceProject)(uiapp, references);
            if (reference == null) return Result.Cancelled;
            if (!reference.IsValidObject || reference.IsFamilyDocument || !references.Contains(reference))
                throw new InvalidOperationException("Das Referenzprojekt wurde geschlossen oder ist nicht mehr verfügbar.");

            var targetSymbols = new FilteredElementCollector(activeDocument).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToArray();
            var sourceSymbols = new FilteredElementCollector(reference).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToArray();
            string FamilyName(FamilySymbol symbol) => symbol.Family?.Name ?? "";
            var referenceTypes = sourceSymbols.Select(symbol => new BgkReferenceType(
                FamilyName(symbol), symbol.Name, ReadBgk(symbol))).ToArray();
            var targets = targetSymbols.Select(symbol =>
            {
                var parameter = symbol.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
                var writable = parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.String
                    && parameter.Definition.GetDataType().Equals(SpecTypeId.String.Text);
                return new BgkTargetType(FamilyName(symbol), symbol.Name, writable);
            }).ToArray();
            var plan = BgkReferenceTransfer.CreatePlan(targets, referenceTypes);
            var summary = Summary(plan);
            var confirm = new TaskDialog("BGK aus Referenzprojekt übernehmen")
            {
                MainInstruction = "BGK-Werte aus dem Referenzprojekt übernehmen?",
                MainContent = "Referenz: " + DocumentLabel(reference) + "\nAktives Projekt: " + DocumentLabel(activeDocument)
                    + "\n\n" + summary + "\n\nEs wird ausschließlich ASSEMBLY_CODE am Familientyp geschrieben."
                    + "\nAbgleich: exakter, eindeutiger Familienname + Typname; keine Teil- oder Ähnlichkeitssuche.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            if (!activeDocument.IsValidObject || !activeDocument.Equals(uiapp.ActiveUIDocument?.Document))
                throw new InvalidOperationException("Das Projekt wurde während der Bestätigung gewechselt oder geschlossen. Bitte erneut auslösen.");
            if (!reference.IsValidObject)
                throw new InvalidOperationException("Das Referenzprojekt wurde während der Bestätigung geschlossen. Bitte erneut auslösen.");

            writer.Committed = () =>
            {
                var resultDialog = new TaskDialog("BGK-Übernahme abgeschlossen")
                {
                    MainInstruction = "BGK-Übernahme abgeschlossen und rückgelesen.",
                    MainContent = "Referenz: " + DocumentLabel(reference) + "\nAktives Projekt: " + DocumentLabel(activeDocument) + "\n\n" + summary,
                    CommonButtons = TaskDialogCommonButtons.Ok
                };
                resultDialog.Show();
                var skipped = plan.Where(x => x.Status != BgkTransferStatus.Copy).ToArray();
                if (skipped.Length > 0 && TaskDialog.Show("Nicht übernommene Typen", "Sollen die nicht übernommenen aktiven Familien/Typen als tabellarische Textdatei ausgegeben werden?") == TaskDialogResult.Yes)
                {
                    try { SaveSkippedReport(uiapp, skipped); }
                    catch (Exception ex) { TaskDialog.Show("Bericht konnte nicht gespeichert werden", "Die BGK-Übernahme ist abgeschlossen. Der optionale Bericht konnte nicht gespeichert werden:\n" + ex.Message); }
                }
            };
            writer.Write(activeDocument, targetSymbols, plan);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            writer.Committed = null;
            message = ex.Message;
            TaskDialog.Show("BGK-Übernahme fehlgeschlagen", ex.Message);
            return Result.Failed;
        }
    }

    private static string? ReadBgk(FamilySymbol symbol)
    {
        var parameter = symbol.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
        return parameter is { HasValue: true, StorageType: StorageType.String }
            && parameter.Definition.GetDataType().Equals(SpecTypeId.String.Text) ? parameter.AsString() : null;
    }

    private static Document? SelectReferenceProject(UIApplication application, IReadOnlyList<Document> documents)
    {
        var choices = documents.Select((document, index) => new ProjectChoice(document,
            $"{document.Title} — {DocumentLabel(document)}")).ToArray();
        var window = new Window
        {
            Title = "BGK-Referenzprojekt auswählen", Width = 640, Height = 190, MinWidth = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13
        };
        if (application.MainWindowHandle != IntPtr.Zero)
            new System.Windows.Interop.WindowInteropHelper(window).Owner = application.MainWindowHandle;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Geöffnetes Referenzprojekt auswählen (das aktive Projekt ist ausgeschlossen):", Margin = new Thickness(0, 0, 0, 8) });
        var combo = new WpfComboBox { ItemsSource = choices, DisplayMemberPath = nameof(ProjectChoice.Label), SelectedIndex = 0, MinHeight = 30 };
        panel.Children.Add(combo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var select = new Button { Content = "Auswählen", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100 };
        select.Click += (_, _) => { if (combo.SelectedItem is ProjectChoice) window.DialogResult = true; };
        buttons.Children.Add(select); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        window.Content = panel;
        return window.ShowDialog() == true ? (combo.SelectedItem as ProjectChoice)?.Document : null;
    }

    private static string Summary(IReadOnlyList<BgkTransferDecision> plan) =>
        "Familien-/Typen im aktiven Projekt: " + plan.Count
        + "\nÜbernommen: " + plan.Count(x => x.Status == BgkTransferStatus.Copy)
        + "\nKein exakter Referenztreffer: " + plan.Count(x => x.Status == BgkTransferStatus.NoExactMatch)
        + "\nMehrdeutige Referenz: " + plan.Count(x => x.Status == BgkTransferStatus.AmbiguousReference)
        + "\nMehrdeutiger aktiver Schlüssel: " + plan.Count(x => x.Status == BgkTransferStatus.AmbiguousTarget)
        + "\nBGK im Referenztyp leer: " + plan.Count(x => x.Status == BgkTransferStatus.EmptyReferenceValue)
        + "\nASSEMBLY_CODE fehlt/ist nicht beschreibbar: " + plan.Count(x => x.Status == BgkTransferStatus.TargetUnavailable);

    private static void SaveSkippedReport(UIApplication application, IReadOnlyList<BgkTransferDecision> skipped)
    {
        using var save = new FileSaveDialog("Tabellarischer Text (*.txt)|*.txt|Alle Dateien (*.*)|*.*")
        {
            Title = "Nicht übernommene Familien und Typen speichern", DefaultFilterEntry = "*.txt",
            InitialFileName = "BGK-nicht-uebernommen.txt"
        };
        if (save.Show() != ItemSelectionDialogResult.Confirmed || save.GetSelectedModelPath() is not { } modelPath) return;
        var path = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
        static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        var report = new StringBuilder("Familie\tTyp\tGrund\r\n");
        foreach (var entry in skipped)
            report.Append(Cell(entry.Target.FamilyName)).Append('\t').Append(Cell(entry.Target.TypeName)).Append('\t')
                .Append(StatusText(entry.Status)).Append("\r\n");
        File.WriteAllText(path, report.ToString(), new UTF8Encoding(true));
        TaskDialog.Show("BGK-Übernahme", "Bericht gespeichert: " + path);
    }

    private static string StatusText(BgkTransferStatus status) => status switch
    {
        BgkTransferStatus.NoExactMatch => "Kein exakter Familien-/Typ-Treffer",
        BgkTransferStatus.AmbiguousReference => "Familien-/Typ-Schlüssel in Referenz mehrdeutig",
        BgkTransferStatus.AmbiguousTarget => "Familien-/Typ-Schlüssel im aktiven Projekt mehrdeutig",
        BgkTransferStatus.EmptyReferenceValue => "BGK im Referenztyp leer",
        BgkTransferStatus.TargetUnavailable => "ASSEMBLY_CODE fehlt, ist schreibgeschützt oder kein Text",
        _ => "Übernommen"
    };

    private static string DocumentLabel(Document document) => string.IsNullOrWhiteSpace(document.PathName) ? document.Title : document.PathName;
    private sealed record ProjectChoice(Document Document, string Label);
}
