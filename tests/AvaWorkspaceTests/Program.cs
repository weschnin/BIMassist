using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class Program
{
    private static string directory = "";
    private static int passed, failed;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--remember") { new AvaWorkspace().Remember(args[1]); return 0; }
        if (args.FirstOrDefault() == "--reopen")
        {
            var window = new AvaTemplateWindow(); window.Show();
            var editor = Field<TemplateEditor>(window, "editor");
            Console.WriteLine(editor.FilePath); Console.WriteLine(editor.Document.Name); Console.WriteLine(editor.IsDirty);
            window.Close(); return 0;
        }
        directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "ava-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Test("workspace remembers successful validated template across instances", () => {
                var path = Template("working.json", "Working template");
                var workspaceType = typeof(TemplateEditor).Assembly.GetType("BIMassist.Ava.Core.AvaWorkspace");
                Check(workspaceType != null, "Missing public AvaWorkspace persistence service");
                var workspace = Activator.CreateInstance(workspaceType!, new object[] { Settings })!;
                workspaceType!.GetMethod("Remember")!.Invoke(workspace, new object[] { path });
                var reopened = Activator.CreateInstance(workspaceType, new object[] { Settings })!;
                Check((string?)workspaceType.GetProperty("LastTemplatePath")!.GetValue(reopened) == path, "Last editing path was not persisted");
                Check(((AvaTemplate)workspaceType.GetMethod("LoadLast")!.Invoke(reopened, null)!).Name == "Working template", "LoadLast did not load real template");
            });
            Test("per-user default workspace survives separate processes and reopens the real window", () => {
                string settings = Path.Combine(directory, "cross-process-settings.json");
                string path = Template("cross-process.json", "Remembered in another process");
                RunChild(settings, "--remember", path);
                string[] output = RunChild(settings, "--reopen").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                Check(output.SequenceEqual(new[] { path, "Remembered in another process", "False" }), "next process did not auto-load the persisted editing file cleanly: " + string.Join(" / ", output));
            });
            Test("LoadLast without remembered file gives actionable InvalidDataException", () => {
                var workspace = new AvaWorkspace(Path.Combine(directory, "none.json"));
                Reject(() => workspace.LoadLast(), "Vorlageneditor");
            });
            Test("corrupt settings are nonfatal and reported by LoadLast", () => {
                string settings = Path.Combine(directory, "corrupt-settings.json"); File.WriteAllText(settings, "{not json");
                var workspace = new AvaWorkspace(settings);
                Check(workspace.LastTemplatePath == null, "corrupt settings supplied a path");
                Reject(() => workspace.LoadLast(), "Vorlageneditor");
                Check(File.ReadAllText(settings) == "{not json", "corrupt settings were overwritten during read");
            });
            Test("structurally corrupt settings are nonfatal on actual window Loaded and remain untouched", () => {
                string settings = Path.Combine(directory, "malformed-shape-settings.json");
                foreach (string contents in new[] { "null", "[]", "{}", "{\"LastTemplatePath\":null}", "{\"LastTemplatePath\":17}", "{\"LastTemplatePath\":\" \"}", "{\"LastTemplatePath\":\"relative.json\"}" })
                {
                    File.WriteAllText(settings, contents);
                    var window = Window(new AvaWorkspace(settings));
                    try
                    {
                        var editor = Field<TemplateEditor>(window, "editor");
                        var warning = (TextBlock)window.FindName("WorkspaceWarning");
                        Check(editor.FilePath == null && !editor.IsDirty && warning.Visibility == Visibility.Visible && warning.Text.Contains("Vorlageneditor"), "corrupt settings did not leave a clean editor and actionable warning: " + contents);
                        Check(File.ReadAllText(settings) == contents, "window modified corrupt settings while opening");
                    }
                    finally { window.Close(); }
                }
            });
            Test("missing corrupt invalid and malformed remembered paths are actionable", () => {
                var good = Template("remembered.json", "Valid");
                var workspace = new AvaWorkspace(Path.Combine(directory, "missing-settings.json")); workspace.Remember(good);
                File.Delete(good); Reject(() => workspace.LoadLast(), "Vorlageneditor");
                File.WriteAllText(good, "bad json"); Reject(() => workspace.LoadLast(), "Vorlageneditor");
                var invalid = new AvaTemplate { Name = "Invalid", Mappings = [new MappingRule { BgkKey = "unknown" }] };
                TemplateJson.Save(good, invalid); Reject(() => workspace.LoadLast(), "Vorlageneditor");
                string settings = Path.Combine(directory, "bad-path-settings.json");
                File.WriteAllText(settings, "{\"LastTemplatePath\":\"\\u0000\"}");
                Reject(() => new AvaWorkspace(settings).LoadLast(), "Vorlageneditor");
            });
            Test("Remember failure preserves previous settings and successful path atomically", () => {
                var first = Template("atomic-first.json", "First"); var second = Template("atomic-second.json", "Second");
                var settings = Path.Combine(directory, "atomic-settings.json"); var workspace = new AvaWorkspace(settings); workspace.Remember(first);
                var before = File.ReadAllBytes(settings);
                string bad = Path.Combine(directory, "rejected.json"); File.WriteAllText(bad, "bad json");
                Reject(() => workspace.Remember(bad));
                Check(workspace.LastTemplatePath == first && File.ReadAllBytes(settings).SequenceEqual(before), "failed template changed successful workspace");
                using (var locked = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { workspace.Remember(second); throw new Exception("locked settings write unexpectedly succeeded"); }
                    catch (IOException) { }
                    Check(workspace.LastTemplatePath == first, "failed settings write advanced in-memory path");
                }
                Check(File.ReadAllBytes(settings).SequenceEqual(before), "failed settings write damaged prior settings");
                Check(!Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "atomic write leaked temporary file");
                workspace.Remember(second);
                Check(new AvaWorkspace(settings).LoadLast().Name == "Second", "atomic replacement failed");
            });
            Test("autoload is deferred to actual Loaded and runs exactly once without replacing later edits", () => {
                string path = Template("loaded-once.json", "At construction");
                var workspace = new AvaWorkspace(Path.Combine(directory, "loaded-once-settings.json")); workspace.Remember(path);
                var window = new AvaTemplateWindow(workspace);
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No));
                try
                {
                    Check(Field<TemplateEditor>(window, "editor").FilePath == null, "constructor performed autoload before Loaded");
                    TemplateJson.Save(path, new AvaTemplate { Name = "At Loaded", ReviewScope = "Alle Bauteile" });
                    window.Show();
                    var editor = Field<TemplateEditor>(window, "editor");
                    Check(editor.Document.Name == "At Loaded" && editor.FilePath == path && !editor.IsDirty, "Loaded did not load current saved template");
                    ((TextBox)window.FindName("ProfileName")).Text = "Keep live edits";
                    TemplateJson.Save(path, new AvaTemplate { Name = "Do not reload", ReviewScope = "Alle Bauteile" });
                    window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Check(ReferenceEquals(editor, Field<TemplateEditor>(window, "editor")) && editor.Document.Name == "Keep live edits" && editor.IsDirty, "repeated Loaded replaced live edits");
                }
                finally { window.Close(); }
            });
            Test("actual editor auto-loads same last editing template on two reopens cleanly", () => {
                string path = Template("reopen.json", "Automatically reopened");
                string settings = Path.Combine(directory, "reopen-settings.json"); new AvaWorkspace(settings).Remember(path);
                byte[] before = File.ReadAllBytes(path);
                for (int i = 0; i < 2; i++)
                {
                    var window = Window(new AvaWorkspace(settings)); var editor = Field<TemplateEditor>(window, "editor");
                    Check(editor.Document.Name == "Automatically reopened" && editor.FilePath == path && !editor.IsDirty, "actual window did not reopen clean document");
                    Check(((TextBox)window.FindName("ProfileName")).Text == editor.Document.Name, "autoload did not refresh UI");
                    window.Show(); window.Close();
                }
                Check(File.ReadAllBytes(path).SequenceEqual(before), "auto-open modified source template");
            });
            Test("failed auto-open leaves clean new editor and persistent visible warning without overwriting source", () => {
                string path = Template("bad-autoload.json", "Will become corrupt");
                string settings = Path.Combine(directory, "bad-autoload-settings.json"); new AvaWorkspace(settings).Remember(path);
                File.WriteAllText(path, "corrupt source - preserve me");
                var window = Window(new AvaWorkspace(settings));
                Check(Field<TemplateEditor>(window, "editor").FilePath == null && !Field<TemplateEditor>(window, "editor").IsDirty, "failed auto-open committed bad path or dirty document");
                var warning = (TextBlock?)window.FindName("WorkspaceWarning");
                Check(warning != null && warning.Visibility == Visibility.Visible && warning.Text.Contains("Vorlageneditor"), "auto-open failure has no visible actionable warning");
                ((TabControl)window.FindName("EditorTabs")).SelectedIndex = 4;
                Check(warning!.Text.Length > 0 && warning.Visibility == Visibility.Visible, "validation refresh hid auto-open warning");
                window.Show(); window.Close();
                Check(File.ReadAllText(path) == "corrupt source - preserve me", "failed auto-open overwrote source");
                File.Delete(path);
                var missing = Window(new AvaWorkspace(settings));
                Check(((TextBlock)missing.FindName("WorkspaceWarning")).Text.Length > 0, "missing source has no warning"); missing.Close();
                File.WriteAllText(settings, "bad settings");
                var corrupt = Window(new AvaWorkspace(settings));
                Check(((TextBlock)corrupt.FindName("WorkspaceWarning")).Text.Length > 0, "corrupt settings crash or have no warning"); corrupt.Close();
            });
            Test("actual clean close and edit-then-revert do not prompt or show false dirty title", () => {
                var window = Window(new AvaWorkspace(Path.Combine(directory, "clean-close.json")));
                int prompts = 0, saves = 0;
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => { prompts++; return MessageBoxResult.Cancel; }));
                Hook(window, "SavePathSelector", (Func<string?>)(() => { saves++; return null; }));
                var name = (TextBox)window.FindName("ProfileName"); string initial = name.Text;
                name.Text = "Changed"; Check(window.Title.EndsWith(" *"), "real edit does not show dirty title");
                name.Text = initial;
                Check(!Field<TemplateEditor>(window, "editor").IsDirty && !window.Title.EndsWith(" *"), "reverted edit has false dirty title");
                bool closed = false; window.Closed += (_, _) => closed = true; window.Show(); window.Close();
                Check(closed && prompts == 0 && saves == 0, "clean Closing handler prompted or saved");
            });
            Test("actual Closing during dispatcher shutdown never prompts or saves", () => {
                Exception? failure = null;
                var thread = new Thread(() => {
                    try
                    {
                        var window = Window(new AvaWorkspace(Path.Combine(directory, "shutdown-settings.json")));
                        int prompts = 0, saves = 0; bool closed = false;
                        Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => { prompts++; return MessageBoxResult.No; }));
                        Hook(window, "SavePathSelector", (Func<string?>)(() => { saves++; return null; }));
                        ((TextBox)window.FindName("ProfileName")).Text = "Dirty at shutdown";
                        window.Closed += (_, _) => closed = true;
                        var dispatcher = window.Dispatcher;
                        dispatcher.ShutdownStarted += (_, _) => window.Close();
                        dispatcher.InvokeShutdown();
                        Check(closed && prompts == 0 && saves == 0, "shutdown Closing showed a pending-save prompt or attempted to save");
                    }
                    catch (Exception ex) { failure = ex; }
                });
                thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
                Check(thread.Join(TimeSpan.FromSeconds(30)), "shutdown test thread did not finish");
                if (failure != null) throw failure;
            });
            Test("actual Closing cancel keeps dirty editor open and discard closes without saving", () => {
                var window = Window(new AvaWorkspace(Path.Combine(directory, "cancel-close.json")));
                int prompts = 0, saves = 0;
                MessageBoxResult answer = MessageBoxResult.Cancel;
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => { prompts++; return answer; }));
                Hook(window, "SavePathSelector", (Func<string?>)(() => { saves++; return null; }));
                ((TextBox)window.FindName("ProfileName")).Text = "Dirty";
                bool closed = false; window.Closed += (_, _) => closed = true; window.Show(); window.Close();
                Check(!closed && window.IsVisible && prompts == 1 && saves == 0, "Cancel did not cancel actual Closing or invoked save");
                answer = MessageBoxResult.No; window.Close();
                Check(closed && prompts == 2 && saves == 0, "discard did not close without saving");
            });
            Test("actual Closing save cancellation and file failure keep document dirty and window open", () => {
                var window = Window(new AvaWorkspace(Path.Combine(directory, "save-close-failure.json")));
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.Yes));
                Hook(window, "DraftPrompt", (Func<int, bool>)(_ => true));
                string? target = null; int errors = 0;
                Hook(window, "SavePathSelector", (Func<string?>)(() => target));
                Hook(window, "ErrorReporter", (Action<Exception>)(_ => errors++));
                ((TextBox)window.FindName("ProfileName")).Text = "Unsaved";
                bool closed = false; window.Closed += (_, _) => closed = true; window.Show(); window.Close();
                Check(!closed && Field<TemplateEditor>(window, "editor").IsDirty && errors == 0, "cancelled Save As allowed close");
                target = Path.Combine(directory, "missing-parent", "cannot-save.json"); window.Close();
                Check(!closed && window.IsVisible && Field<TemplateEditor>(window, "editor").IsDirty && errors == 1, "failed save allowed close or lost dirty state");
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No)); window.Close();
            });
            Test("actual Closing file-write failure preserves existing working path document and reversible baseline", () => {
                string path = Template("locked-working.json", "Original baseline");
                string settings = Path.Combine(directory, "locked-working-settings.json");
                var workspace = new AvaWorkspace(settings); workspace.Remember(path);
                byte[] remembered = File.ReadAllBytes(settings), source = File.ReadAllBytes(path);
                var window = Window(workspace); var editor = Field<TemplateEditor>(window, "editor");
                int prompts = 0, selectors = 0, errors = 0; bool closed = false;
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => { prompts++; return MessageBoxResult.Yes; }));
                Hook(window, "DraftPrompt", (Func<int, bool>)(_ => true));
                Hook(window, "SavePathSelector", (Func<string?>)(() => { selectors++; return null; }));
                Hook(window, "ErrorReporter", (Action<Exception>)(_ => errors++));
                window.Closed += (_, _) => closed = true;
                try
                {
                    ((TextBox)window.FindName("ProfileName")).Text = "Unsaved locked changes";
                    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) window.Close();
                    Check(!closed && window.IsVisible && prompts == 1 && selectors == 0 && errors == 1, "failed existing-file save closed window or chose a different destination");
                    Check(ReferenceEquals(editor, Field<TemplateEditor>(window, "editor")) && editor.FilePath == path && editor.IsDirty && workspace.LastTemplatePath == path, "failed actual close lost working editor/path/dirty state");
                    Check(File.ReadAllBytes(path).SequenceEqual(source) && File.ReadAllBytes(settings).SequenceEqual(remembered), "failed save modified source or remembered settings");
                    ((TextBox)window.FindName("ProfileName")).Text = "Original baseline";
                    Check(!editor.IsDirty, "file failure advanced the dirty baseline");
                    window.Close(); Check(closed && prompts == 1, "reverted file failure still prompted");
                }
                finally { Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No)); if (!closed) window.Close(); }
            });
            Test("actual Closing successful save persists edit path and next window loads saved content", () => {
                string settings = Path.Combine(directory, "save-close-success.json"), target = Path.Combine(directory, "saved-on-close.json");
                var window = Window(new AvaWorkspace(settings));
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.Yes));
                Hook(window, "DraftPrompt", (Func<int, bool>)(_ => true));
                Hook(window, "SavePathSelector", (Func<string?>)(() => target));
                ((TextBox)window.FindName("ProfileName")).Text = "Saved by actual Closing";
                bool closed = false; window.Closed += (_, _) => closed = true; window.Show(); window.Close();
                Check(closed && TemplateJson.Load(target).Name == "Saved by actual Closing", "Closing did not save real template");
                Check(new AvaWorkspace(settings).LastTemplatePath == target, "successful save did not remember editing path");
                var next = Window(new AvaWorkspace(settings));
                Check(Field<TemplateEditor>(next, "editor").Document.Name == "Saved by actual Closing" && !Field<TemplateEditor>(next, "editor").IsDirty, "saved template not cleanly reopened"); next.Close();
            });
            Test("actual open persists editing path and rejected replacement preserves loaded dirty state", () => {
                var workspace = new AvaWorkspace(Path.Combine(directory, "open-settings.json")); var window = Window(workspace);
                string path = Template("opened.json", "Opened normally"); int prompts = 0, errors = 0;
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => { prompts++; return MessageBoxResult.Cancel; }));
                Hook(window, "ErrorReporter", (Action<Exception>)(_ => errors++));
                Check(Call<bool>(window, "OpenPath", path, false), "actual open failed");
                var editor = Field<TemplateEditor>(window, "editor");
                Check(editor.FilePath == path && !editor.IsDirty && workspace.LastTemplatePath == path, "open did not commit clean editing path");
                ((TextBox)window.FindName("ProfileName")).Text = "Keep dirty changes"; var document = editor.Document;
                string bad = Path.Combine(directory, "open-invalid.json"); File.WriteAllText(bad, "invalid");
                Check(!Call<bool>(window, "OpenPath", bad, false) && errors == 1 && prompts == 0, "bad candidate prompted/discarded before validation");
                string other = Template("other-open.json", "Other");
                Check(!Call<bool>(window, "OpenPath", other, false) && prompts == 1, "cancelled replacement succeeded");
                Check(ReferenceEquals(editor.Document, document) && editor.FilePath == path && editor.IsDirty && workspace.LastTemplatePath == path, "failed/cancelled open changed current state");
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No)); window.Close();
            });
            Test("opening the working file after saving pending edits never loads a stale pre-save snapshot", () => {
                string path = Template("same-open.json", "Before pending save");
                var workspace = new AvaWorkspace(Path.Combine(directory, "same-open-settings.json")); workspace.Remember(path);
                var window = Window(workspace);
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.Yes));
                Hook(window, "DraftPrompt", (Func<int, bool>)(_ => true));
                try
                {
                    ((TextBox)window.FindName("ProfileName")).Text = "Saved before replacement";
                    Check(Call<bool>(window, "OpenPath", path, false), "opening existing working file failed");
                    var editor = Field<TemplateEditor>(window, "editor");
                    Check(editor.Document.Name == TemplateJson.Load(path).Name && editor.Document.Name == "Saved before replacement" && !editor.IsDirty,
                        "Open committed stale pre-save content as clean over newly saved pending edits");
                }
                finally { Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No)); window.Close(); }
            });
            Test("import and export copies never hijack last editing path or silently clean imported changes", () => {
                string settings = Path.Combine(directory, "copy-settings.json"), working = Template("copy-working.json", "Original");
                var workspace = new AvaWorkspace(settings); workspace.Remember(working); var window = Window(workspace);
                string imported = Template("portable.json", "Imported"), exported = Path.Combine(directory, "exported.json");
                var importedDocument = TemplateJson.Load(imported);
                importedDocument.BgkCatalog = new(new("manual", "BIMassist manuell bearbeitet", ""), [new("01-10", "Bearbeitete BGK", "", [new("100", "Bearbeiteter Typ")])]);
                TemplateJson.Save(imported, importedDocument);
                byte[] original = File.ReadAllBytes(working), source = File.ReadAllBytes(imported);
                Check(Call<bool>(window, "OpenPath", imported, true), "import failed");
                var editor = Field<TemplateEditor>(window, "editor");
                Check(editor.FilePath == null && editor.IsDirty && workspace.LastTemplatePath == working, "import hijacked working source path or marked copy clean");
                Call<object?>(window, "ExportPath", exported);
                Check(TemplateJson.Load(exported).Name == "Imported" && editor.FilePath == null && editor.IsDirty, "export changed editing state");
                Check(TemplateJson.Load(exported).BgkCatalog.Entries.Single().Types.Single().Description == "Bearbeiteter Typ",
                    "portable export did not include the edited BGK catalog snapshot");
                Check(new AvaWorkspace(settings).LastTemplatePath == working && File.ReadAllBytes(working).SequenceEqual(original) && File.ReadAllBytes(imported).SequenceEqual(source), "copy operation overwrote source or persisted copy path");
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No)); window.Close();
            });
            Test("failed Save As preserves working path baseline and last successful path", () => {
                string working = Template("failed-save-working.json", "Before"); var workspace = new AvaWorkspace(Path.Combine(directory, "failed-save-settings.json")); workspace.Remember(working);
                var window = Window(workspace); int errors = 0;
                Hook(window, "ErrorReporter", (Action<Exception>)(_ => errors++)); Hook(window, "DraftPrompt", (Func<int, bool>)(_ => true));
                Hook(window, "SavePathSelector", (Func<string?>)(() => Path.Combine(directory, "not-created", "failure.json")));
                ((TextBox)window.FindName("ProfileName")).Text = "After";
                Check(!Call<bool>(window, "Save", true), "invalid Save As succeeded");
                var editor = Field<TemplateEditor>(window, "editor");
                Check(errors == 1 && editor.IsDirty && editor.FilePath == working && workspace.LastTemplatePath == working && TemplateJson.Load(working).Name == "Before", "failed Save As lost baseline or overwrote working file");
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No)); window.Close();
            });
            Test("refused draft and invalid document both cancel actual Closing without writing", () => {
                var window = Window(new AvaWorkspace(Path.Combine(directory, "refused-draft-settings.json"))); int errors = 0, saves = 0;
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.Yes)); Hook(window, "DraftPrompt", (Func<int, bool>)(_ => false));
                Hook(window, "SavePathSelector", (Func<string?>)(() => { saves++; return Path.Combine(directory, "must-not-exist.json"); }));
                Hook(window, "ErrorReporter", (Action<Exception>)(_ => errors++));
                ((TextBox)window.FindName("ProfileName")).Text = "Draft"; bool closed = false; window.Closed += (_, _) => closed = true;
                window.Show(); window.Close(); Check(!closed && saves == 0 && errors == 0, "refused draft allowed close or wrote file");
                Field<TemplateEditor>(window, "editor").Document.Mappings.Add(new MappingRule { BgkKey = "missing" });
                window.Close(); Check(!closed && saves == 0 && errors == 1, "invalid document allowed close or invoked Save As");
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No)); window.Close();
            });
            Test("settings persistence failure cancels actual Closing without advancing editor baseline", () => {
                string settings = Path.Combine(directory, "locked-close-settings.json"), working = Template("locked-close-working.json", "Before");
                var workspace = new AvaWorkspace(settings); workspace.Remember(working); var window = Window(workspace);
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.Yes)); Hook(window, "DraftPrompt", (Func<int, bool>)(_ => true));
                int errors = 0; Hook(window, "ErrorReporter", (Action<Exception>)(_ => errors++));
                ((TextBox)window.FindName("ProfileName")).Text = "Pending persistence"; bool closed = false; window.Closed += (_, _) => closed = true;
                window.Show();
                using (var locked = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    window.Close();
                    Check(!closed && errors == 1 && Field<TemplateEditor>(window, "editor").IsDirty, "failed settings save lost pending baseline or closed editor");
                }
                window.Close(); Check(closed && new AvaWorkspace(settings).LoadLast().Name == "Pending persistence", "retry did not complete persistence and close");
            });
            Test("workspace settings path cannot overwrite template source", () => {
                string path = Template("settings-collision.json", "Never overwrite source"); byte[] original = File.ReadAllBytes(path);
                var workspace = new AvaWorkspace(path);
                Reject(() => workspace.Remember(path));
                Check(File.ReadAllBytes(path).SequenceEqual(original), "Remember overwrote source with settings");
            });
            Test("Save As and JSON export cannot overwrite workspace settings", () => {
                string working = Template("reserved-working.json", "Keep working");
                string settings = Path.Combine(directory, "reserved-settings.json");
                var workspace = new AvaWorkspace(settings); workspace.Remember(working);
                byte[] original = File.ReadAllBytes(settings);
                var window = Window(workspace); int errors = 0;
                Hook(window, "ErrorReporter", (Action<Exception>)(_ => errors++));
                Hook(window, "DraftPrompt", (Func<int, bool>)(_ => true));
                Hook(window, "SavePathSelector", (Func<string?>)(() => settings));
                Hook(window, "PendingPrompt", (Func<MessageBoxResult>)(() => MessageBoxResult.No));
                try
                {
                    ((TextBox)window.FindName("ProfileName")).Text = "Still dirty";
                    Check(!Call<bool>(window, "Save", true) && errors == 1, "Save As accepted reserved settings destination");
                    Check(File.ReadAllBytes(settings).SequenceEqual(original), "failed Save As overwrote workspace settings before rejecting path");
                    var editor = Field<TemplateEditor>(window, "editor");
                    Check(editor.IsDirty && editor.FilePath == working && workspace.LastTemplatePath == working, "rejected save changed editing baseline or path");
                    Reject(() => Call<object?>(window, "ExportPath", settings));
                    Check(File.ReadAllBytes(settings).SequenceEqual(original), "JSON export overwrote workspace settings");
                }
                finally { window.Close(); }
            });
            Test("every JSON and catalog dialog has explicit selectable type plus All Files and safe flags", () => {
                Type? type = typeof(AvaTemplateWindow).Assembly.GetType("BIMassist.Ava.Views.AvaFileDialogs");
                Check(type != null && type.IsPublic, "Missing shared public AVA file-dialog factory");
                foreach (bool import in new[] { false, true })
                {
                    var open = (Microsoft.Win32.OpenFileDialog)type!.GetMethod("OpenTemplate")!.Invoke(null, new object[] { import })!;
                    CheckOpen(open, ".json");
                }
                foreach (var pair in new[] { ("bgk", ".xml"), ("lv", ".x82"), ("parameters", ".txt") })
                    CheckOpen((Microsoft.Win32.OpenFileDialog)type!.GetMethod("OpenCatalog")!.Invoke(null, new object[] { pair.Item1 })!, pair.Item2);
                foreach (string title in new[] { "Speichern", "Speichern unter", "JSON exportieren" })
                {
                    var save = (Microsoft.Win32.SaveFileDialog)type!.GetMethod("SaveTemplate")!.Invoke(null, new object?[] { title, null })!;
                    CheckFilter(save.Filter, ".json");
                    Check(save.Title == title && save.FilterIndex == 1 && "." + save.DefaultExt.TrimStart('.') == ".json" && save.AddExtension && save.OverwritePrompt && save.CheckPathExists, "unsafe JSON save/export dialog flags");
                }
                var named = (Microsoft.Win32.SaveFileDialog)type!.GetMethod("SaveTemplate")!.Invoke(null, new object?[] { "Save", Path.Combine(directory, "current.json") })!;
                Check(named.FileName == "current.json", "Save As default lost editing filename");
            });
            Test("editor notice points to separate quantity ribbon command without claiming no X31 exists", () => {
                var window = Window(new AvaWorkspace(Path.Combine(directory, "notice-settings.json")));
                var text = string.Join(" ", ((DockPanel)window.Content).Children.OfType<TextBlock>().Select(b => b.Text));
                Check(!text.Contains("kein X31") && text.Contains("Mengenermittlung") && text.Contains("separat"), "editor notice still denies separate quantity export"); window.Close();
            });
            Test("parameterless editor supports isolated default-workspace path without real user settings", () => {
                var defaultPath = typeof(AvaWorkspace).GetProperty("DefaultSettingsPath", BindingFlags.Static | BindingFlags.NonPublic);
                Check(defaultPath != null, "Missing isolated default-workspace path strategy for legacy parameterless UI tests");
                const string variable = "BIMASSIST_AVA_WORKSPACE_SETTINGS"; string? previous = Environment.GetEnvironmentVariable(variable);
                try
                {
                    string isolated = Path.Combine(directory, "isolated-default-settings.json"); Environment.SetEnvironmentVariable(variable, isolated);
                    Check((string?)defaultPath!.GetValue(null) == isolated, "default workspace ignores explicit settings override");
                    string path = Template("parameterless.json", "Parameterless reopened"); new AvaWorkspace().Remember(path);
                    var window = new AvaTemplateWindow(); window.Show();
                    Check(Field<TemplateEditor>(window, "editor").Document.Name == "Parameterless reopened", "parameterless window did not use isolated default workspace on Loaded"); window.Close();
                    Environment.SetEnvironmentVariable(variable, null);
                    Check((string?)defaultPath.GetValue(null) == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMassist", "ava-workspace.json"), "default settings no longer live under LocalApplicationData/BIMassist");
                }
                finally { Environment.SetEnvironmentVariable(variable, previous); }
            });
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"AvaWorkspaceTests: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
    private static string RunChild(string settings, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["BIMASSIST_AVA_WORKSPACE_SETTINGS"] = settings;
        using var process = Process.Start(start) ?? throw new Exception("Failed to start workspace child process");
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Workspace child process timed out"); }
        string output = process.StandardOutput.ReadToEnd(), errors = process.StandardError.ReadToEnd();
        Check(process.ExitCode == 0, "Workspace child process failed: " + output + errors);
        return output;
    }
    private static string Settings => Path.Combine(directory, "settings", "workspace.json");
    private static string Template(string file, string name)
    {
        string path = Path.Combine(directory, file); TemplateJson.Save(path, new AvaTemplate { Name = name, ReviewScope = "Alle Bauteile" }); return path;
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    private static AvaTemplateWindow Window(AvaWorkspace workspace)
    {
        var constructor = typeof(AvaTemplateWindow).GetConstructor(new[] { typeof(AvaWorkspace) });
        Check(constructor != null, "Missing injectable workspace constructor on actual AVA editor");
        var window = (AvaTemplateWindow)constructor!.Invoke(new object[] { workspace });
        window.Show(); // Exercise real Loaded and Closing on a shown WPF window.
        return window;
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Hook(object target, string name, object callback)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(property != null, "Missing injectable actual-window interaction: " + name); property!.SetValue(target, callback);
    }
    private static T Call<T>(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(method != null, "Missing actual-window file operation: " + name);
        try { return (T)method!.Invoke(target, args)!; }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static void CheckOpen(Microsoft.Win32.OpenFileDialog dialog, string extension)
    {
        CheckFilter(dialog.Filter, extension);
        // WPF normalizes a configured '.json' DefaultExt to 'json' when read back.
        Check("." + dialog.DefaultExt.TrimStart('.') == extension && dialog.AddExtension && dialog.CheckFileExists && dialog.CheckPathExists && !dialog.Multiselect && dialog.FilterIndex == 1, $"unsafe single-open dialog flags: expected {extension}, actual DefaultExt={dialog.DefaultExt}, AddExtension={dialog.AddExtension}, CheckFileExists={dialog.CheckFileExists}, CheckPathExists={dialog.CheckPathExists}, Multiselect={dialog.Multiselect}, FilterIndex={dialog.FilterIndex}");
    }
    private static void CheckFilter(string filter, string extension)
    {
        string[] parts = filter.Split('|');
        Check(parts.Length == 4 && parts[0].Contains("*" + extension, StringComparison.Ordinal) && parts[1] == "*" + extension && parts[3] == "*.*", "only the supported file type and All Files must be separate selectable filters: " + filter);
    }
    private static void Reject(Action action, string? message = null)
    {
        try { action(); }
        catch (InvalidDataException ex) { Check(message == null || ex.Message.Contains(message), "Non-actionable error: " + ex.Message); return; }
        throw new Exception("Expected InvalidDataException");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
