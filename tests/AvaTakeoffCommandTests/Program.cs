using System.Text.RegularExpressions;
string? root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "App.cs"))) root = Directory.GetParent(root)?.FullName;
if (root == null) throw new Exception("Repository not found");
void Check(bool yes, string message) { if (!yes) throw new Exception(message); }
var app = File.ReadAllText(Path.Combine(root, "App.cs"));
var bgk = app.IndexOf("new PulldownButtonData(\"BGK\"", StringComparison.Ordinal);
var info = app.IndexOf("new PushButtonData(\"BIMassist Info\"", StringComparison.Ordinal);
Check(bgk >= 0 && info > bgk && app[bgk..info].Contains("BIMassist.Commands.AvaQuantityTakeoffCommand")
    && app[bgk..info].Contains("BIMassist.Commands.BgkTransferFromReferenceCommand")
    && !app.Contains("new PulldownButtonData(\"AVA\"", StringComparison.Ordinal), "AVA functions are not grouped under BGK or the obsolete AVA group remains");
var source = File.ReadAllText(Path.Combine(root, "Commands", "AvaQuantityTakeoffCommand.cs"));
Check(source.Contains("TransactionMode.ReadOnly"), "Takeoff must be read-only");
Check(!Regex.IsMatch(source, @"new\s+(Transaction|TransactionGroup|SubTransaction)\b"), "Takeoff must not write Revit elements");
Check(source.Contains("document.IsFamilyDocument") && source.Contains("ActiveUIDocument"), "Command must reject missing project/family context");
Check(source.Contains("QuantityTakeoff.Calculate") && source.Contains("RevitQuantityReader.Elements") && source.Contains("X31Writer.Save"), "Command does not connect actual model calculation to X31 writer");
Check(Regex.IsMatch(source, @"result\.Errors\.Count\s*>\s*0") && source.IndexOf("result.Errors.Count", StringComparison.Ordinal) < source.IndexOf("X31Writer.Save", StringComparison.Ordinal), "Partial/error results must not be exported");
Check(source.Contains("new FileSaveDialog(") && source.Contains("InitialFileName = \"Mengenermittlung.x31\"")
    && source.Contains("DefaultFilterEntry = \"*.x31\"") && source.Contains("GetSelectedModelPath()")
    && !source.Contains("ShowFileDialog(save,"), "X31 export must use Revit's native save dialog, not WPF HWND ownership");
Check(source.Contains("LV-Einheit") && source.Contains("Keine automatische Umrechnung"), "Declared quantity versus LV-unit warning must precede export");
Check(source.Contains("result.Unmarked") && source.Contains("result.Unmapped"), "Scope omissions must be visible, not claimed a complete audit");
var transfer = File.ReadAllText(Path.Combine(root, "Commands", "BgkTransferFromReferenceCommand.cs"));
Check(transfer.Contains("Application.Documents") && transfer.Contains("ReferenceEquals(document, activeDocument)"), "Reference chooser must enumerate open projects and exclude the active project");
Check(transfer.Contains("BgkReferenceTransfer.CreatePlan") && transfer.Contains("BuiltInParameter.ASSEMBLY_CODE")
    && transfer.Contains("FamilyName(symbol)") && transfer.Contains("symbol.Name"), "Reference transfer does not use strict type identity and BGK field");
Check(transfer.Contains("new Transaction(activeDocument") && transfer.Contains("parameter.Set(decision.Value!)")
    && transfer.Contains("FileSaveDialog(\"Tabellarischer Text"), "Reference transfer write transaction or optional text report missing");
ExecuteTests.Run();
Console.WriteLine("PASS AVA quantity command ribbon/context/read-only/export-blocking and X31 save-dialog wiring (static, no Revit-host claim)");
