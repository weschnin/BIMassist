using System.Text.RegularExpressions;
string? root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "App.cs"))) root = Directory.GetParent(root)?.FullName;
if (root == null) throw new Exception("Repository not found");
void Check(bool yes, string message) { if (!yes) throw new Exception(message); }
var app = File.ReadAllText(Path.Combine(root, "App.cs"));
var ava = app.IndexOf("new PulldownButtonData(\"AVA\"", StringComparison.Ordinal);
var info = app.IndexOf("new PushButtonData(\"BIMassist Info\"", StringComparison.Ordinal);
Check(ava >= 0 && info > ava && app[ava..info].Contains("BIMassist.Commands.AvaQuantityTakeoffCommand")
    && app[ava..info].Contains("BIMassist.Commands.BgkTransferFromReferenceCommand")
    && !app.Contains("new PulldownButtonData(\"BGK\"", StringComparison.Ordinal), "AVA functions are not grouped under AVA or the obsolete BGK group remains");
var source = File.ReadAllText(Path.Combine(root, "Commands", "AvaQuantityTakeoffCommand.cs"));
var options = File.ReadAllText(Path.Combine(root, "Ava", "Revit", "AvaGroupingDialog.cs"));
Check(options.Contains("Nur sichtbare Elemente der aktiven Ansicht berücksichtigen")
    && source.Contains("grouping.VisibleOnly") && !source.Contains("AvaTakeoffOptionsWindow.Show"),
    "Visible-only scope must be selected in the grouping window rather than a separate dialog");
Check(source.Contains("dauerhaft in Ansicht oder Kategorie sowie temporär ausgeblendete Elemente ausgeschlossen")
    && source.Contains("Elemente außerhalb des Zuschneidebereichs können dennoch enthalten sein")
    && source.Contains("keine pixelgenaue Sichtbarkeits- oder Verdeckungsprüfung"), "Scope results must disclose crop-boundary and pixel-visibility limitations");
Check(source.Contains("TransactionMode.Manual")
    && source.Contains("new Transaction(document, \"BIMassist AVA letzter Mengenermittlungslauf\")")
    && source.Contains("AvaQuantityRunStorage.Save(document, run)"), "The last takeoff run must be written in one explicit project-storage transaction");
Check(!source.Contains("parameter.Set(") && !source.Contains("ElementTransformUtils."), "Takeoff must not modify model parameters or geometry");
Check(source.Contains("document.IsFamilyDocument") && source.Contains("ActiveUIDocument"), "Command must reject missing project/family context");
Check(source.Contains("QuantityTakeoff.Calculate") && source.Contains("RevitQuantityReader.Elements")
    && source.Contains("AvaQuantityRunFactory.Create") && source.Contains("AvaQuantityRunWindowHost.Show"),
    "The command must calculate, persist, and open the unified measured-run window.");
Check(source.Contains("AvaQuantityRunStorage.Save(document, run)") && source.Contains("AvaQuantityRunStorage.Load(document)")
    && Regex.IsMatch(source, @"result\.Errors\.Count\s*>\s*0")
    && source.IndexOf("result.Errors.Count", StringComparison.Ordinal) < source.IndexOf("AvaQuantityRunStorage.Save", StringComparison.Ordinal),
    "Partial/error results must not be persisted or exported.");
Check(!source.Contains("X31Writer.Save") && !source.Contains("X82QuantityWriter.Save")
    && !source.Contains("MainInstruction = \"Mengenermittlung als X31 exportieren?\""),
    "Export must start from the unified run window instead of separate immediate dialogs.");
var validation = File.ReadAllText(Path.Combine(root, "Ava", "Core", "TemplateValidation.cs"));
Check(validation.Contains("LV-Einheit") && validation.Contains("nicht umrechenbare Einheiten blockieren den Export"),
    "Quantity unit conversion and incompatible-unit blocking must remain validated before persistence/export");
Check(source.Contains("result.Unmarked") && source.Contains("result.Unmapped"), "Scope omissions must be visible, not claimed a complete audit");
var transfer = File.ReadAllText(Path.Combine(root, "Commands", "BgkTransferFromReferenceCommand.cs"));
Check(transfer.Contains("Application.Documents") && transfer.Contains("ReferenceEquals(document, activeDocument)"), "Reference chooser must enumerate open projects and exclude the active project");
Check(transfer.Contains("BgkReferenceTransfer.CreatePlan") && transfer.Contains("BuiltInParameter.ASSEMBLY_CODE")
    && transfer.Contains("FamilyName(symbol)") && transfer.Contains("symbol.Name"), "Reference transfer does not use strict type identity and BGK field");
Check(transfer.Contains("new Transaction(activeDocument") && transfer.Contains("parameter.Set(decision.Value!)")
    && transfer.Contains("FileSaveDialog(\"Tabellarischer Text"), "Reference transfer write transaction or optional text report missing");
ExecuteTests.Run();
Console.WriteLine("PASS AVA quantity command ribbon/context/read-only/export-blocking and X31 save-dialog wiring (static, no Revit-host claim)");
