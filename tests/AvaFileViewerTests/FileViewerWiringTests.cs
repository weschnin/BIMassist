using System.IO;

internal static class FileViewerWiringTests
{
    internal static void Run()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "App.cs"))) root = Directory.GetParent(root)?.FullName;
        Program.Check(root != null, "Repository root not found.");
        string Read(string path) => File.ReadAllText(Path.Combine(root!, path));
        var app = Read("App.cs");
        var start = app.IndexOf("new PulldownButtonData(\"AVA\"", StringComparison.Ordinal);
        var end = app.IndexOf("new PushButtonData(\"Metadaten\"", start, StringComparison.Ordinal);
        var ribbon = app[start..end];
        Program.Check(!ribbon.Contains("AvaLvViewerCommand") && !ribbon.Contains("BgkTransferFromReferenceCommand")
            && ribbon.Contains("BGKManagerCommandStartup") && ribbon.Contains("AvaTemplateCommand") && ribbon.Contains("AvaQuantityTakeoffCommand"),
            "Ribbon must retain Manager/Vorlagen/Mengenermittlung and remove separate file-viewer/reference entries.");
        var host = Read("Ava/Revit/AvaQuantityRunWindowHost.cs");
        Program.Check(host.Contains("new AvaFileViewerSession(window, window.ShowStatus")
            && host.Contains("openX31Viewer:") && host.Contains("fileViewers?.OpenX31()")
            && host.Contains("openX82Viewer:") && host.Contains("fileViewers?.OpenX82()")
            && host.Contains("window.CurrentRun.TemplateJson") && host.Contains("fileViewers?.Dispose()"), "Actual Mengenermittlung host must wire current-snapshot file viewers and release their session if showing the main window fails.");
        var session = Read("Ava/Views/AvaFileViewerSession.cs");
        Program.Check(session.Contains("dialog.ShowDialog(owner)") && session.Contains("LvX82ViewerWindow.Create(path)")
            && session.Contains("X31FileViewerWindow.Create(path, positions)") && !session.Contains("Autodesk.Revit")
            && !session.Contains("ExternalEvent") && !session.Contains("X31ReviewWindow"), "File viewers must use actual WPF owner and existing X82 viewer, without model preflight/transactions/external events.");
        Program.Check(app.Contains("window is AvaQuantityRunWindow") && app.Contains("window is X31FileViewerWindow") && app.Contains("window is LvX82ViewerWindow"),
            "Add-in shutdown must close main and remaining file viewer windows.");
        Console.WriteLine("PASS ribbon relocation, host callbacks, current-snapshot source and add-in shutdown wiring (static)");
    }
}