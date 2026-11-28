using System.IO;

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !File.Exists(Path.Combine(root.FullName, "Commands", "AvaX31ReviewCommand.cs"))) root = root.Parent;
if (root == null) throw new Exception("Repository not found");
var host = File.ReadAllText(Path.Combine(root.FullName, "Ava/Revit/AvaQuantityRunWindowHost.cs"));
var handler = File.ReadAllText(Path.Combine(root.FullName, "Commands/AvaX31ReviewCommand.cs"));
void Check(bool yes, string message) { if (!yes) throw new Exception(message); }
Check(host.Contains("if (message == AvaQuantityViewSettingsSession.SavedMessage) return;"),
    "Successful asynchronous settings saves must not open modal warnings after the window closes.");
Check(host.Contains("AvaQuantityViewSettingsStorage.Load(document)")
    && host.Contains("new AvaQuantityViewSettingsSession(document,")
    && host.Contains("settingsSession.Queue(") && host.Contains("settingsSession.Close()")
    && host.Contains("postSettings:") && host.Contains("settings:"),
    "Window configuration must load/save inside its bound project using a close-safe settings session, not user globals.");
Check(host.Contains("postMissing:") && host.Contains("handler.SetMissingRequest(")
    && host.Contains("postExcel:") && host.Contains("handler.SetExcelRequest("),
    "Missing-element selection/isolation and configured Excel export must be wired through ExternalEvent.");
Check(handler.Contains("public void SetExcelRequest(AvaQuantityTable table)")
    && handler.Contains("AvaQuantityXlsxWriter.Save(")
    && handler.Contains("*.xlsx") && handler.Contains("ExportConfiguredExcel(activeDocument"),
    "Configured XLSX must use the current document-checked external event and an explicit Excel picker/writer.");
Check(handler.IndexOf("sourceDocument.Equals(activeDocument)", StringComparison.Ordinal)
    < handler.IndexOf("ExportConfiguredExcel(activeDocument", StringComparison.Ordinal),
    "Document equality must be checked before exporting the modeless table.");
Check(!handler.Contains("Vollständiges LV mit gemessenen Mengen")
    && !handler.Contains("Nicht gemessene LV-Positionen und ihre Einheit bleiben"),
    "X82 UI must describe measured-only positions and cumulative LV quantities, not the obsolete complete LV.");
Console.WriteLine("PASS project-settings, missing-actions and Excel host wiring (production-source checks only; not live Revit)");
RunRefreshTests.Run(host, handler, root.FullName);
