internal static class ExportConsentWiringTests
{
    public static void Run(Action<string, Action> test)
    {
        test("all measured exports use noncreating pickers then checked protected-path overwrite consent", () =>
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Commands/AvaX31ReviewCommand.cs"))) root = root.Parent;
            var source = File.ReadAllText(Path.Combine(root!.FullName, "Commands/AvaX31ReviewCommand.cs"));
            var start = source.IndexOf("private void ExportMeasuredX31(", StringComparison.Ordinal);
            var end = source.IndexOf("private bool ClearPreviousTemporaryIsolation(", start, StringComparison.Ordinal);
            var exports = source[start..end];
            Check(!exports.Contains("new FileSaveDialog("), "Native Revit save picker can leave a placeholder before protection/validation checks");
            Check(exports.Contains("CreateTestFile = false") && exports.Contains("OverwritePrompt = false"),
                "Pickers must not create output and overwrite authorization belongs to the checked helper");
            Check(exports.Contains("AvaExportDestination.Choose(") && exports.Contains("DefaultButton = TaskDialogResult.No")
                && exports.Contains("dialog.Show() == TaskDialogResult.Yes"), "Existing output needs an explicit Yes with default No");
            foreach (var method in new[] { "ExportMeasuredX31", "ExportMeasuredX82", "ExportConfiguredExcel" })
            {
                var methodStart = exports.IndexOf("private void " + method + "(", StringComparison.Ordinal);
                var next = exports.IndexOf("\n        private ", methodStart + 1, StringComparison.Ordinal);
                var body = next < 0 ? exports[methodStart..] : exports[methodStart..next];
                Check(body.Contains("ChooseExportDestination(") && body.Contains("if (destination == null)"), method + " bypasses consent/cancel");
                Check(body.IndexOf("if (destination == null)", StringComparison.Ordinal) < body.IndexOf("Writer.Save(", StringComparison.Ordinal)
                    || body.Contains("AvaQuantityRunExport.Save"), method + " writes before cancellation check");
            }
            Check(exports.Contains("sourceLv?.FileName", StringComparison.Ordinal)
                && exports.Contains("source: sourceLv", StringComparison.Ordinal),
                "All export formats must pass the full portable SourceStamp, not resolve its basename under the Revit cwd");
            foreach (var token in new[] { "document.PathName", "quantityRun?.ProjectPath", "workspace.LastTemplatePath",
                "AvaWorkspace.DefaultSettingsPath", "sourceLv" })
                Check(exports.Contains(token), "Protected export path missing: " + token);
        });
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
