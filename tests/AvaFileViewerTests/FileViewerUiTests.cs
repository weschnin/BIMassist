using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class FileViewerUiTests
{
    internal static void TableDisplaysPhysicalRows()
    {
        var type = typeof(AvaQuantityRunWindow).Assembly.GetType("BIMassist.Ava.Views.X31FileViewerWindow");
        Program.Check(type != null, "Missing standalone file-only X31 Viewer window.");
        var path = Path.Combine(Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS")! + ".viewer.x31");
        using (var input = Program.Xml([Program.Comment("(ID: BIMassist-abc)"), Program.Calculation("2+"), Program.Calculation("3=")]))
            File.WriteAllBytes(path, input.ToArray());
        var before = File.ReadAllBytes(path);
        var window = (Window)type!.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [path, null])!;
        try
        {
            window.Show();
            var grid = Descendants<DataGrid>(window).Single();
            var headers = grid.Columns.Select(column => column.Header?.ToString()).ToArray();
            Program.Check(new[] { "OZ", "Kurztext", "Zeilentyp", "Bemerkung", "Formelnummer", "Berechnungsansatz", "Ergebnis", "Einheit", "REB-Rohzeile" }.All(headers.Contains), "X31 file viewer is missing independent fields/raw row visibility.");
            Program.Check(grid.IsReadOnly && !grid.CanUserAddRows && !grid.CanUserDeleteRows && grid.Items.Count == 3, "Viewer must expose every physical row read-only.");
            var parsed = grid.Items.Cast<X31ReviewRow>().ToArray();
            Program.Check(parsed[0].Comment == "(ID: BIMassist-abc)" && parsed[1].Result == "5" && parsed[2].Kind == X31ReviewRowKind.Continuation, "Table must bind actual file rows/results.");
            var rawProperty = typeof(X31ReviewRow).GetProperty("RawRow");
            Program.Check(rawProperty != null && ((string)rawProperty.GetValue(parsed[0])!).Contains("BIMassist-abc"), "Raw REB row must be inspectable without reconstruction.");
            Program.Check(Descendants<TextBox>(window).Any(text => text.IsReadOnly && text.Text.Contains("Kurztext")), "Metadata warnings must be visible and scrollable.");
            Program.Check(File.ReadAllBytes(path).SequenceEqual(before), "Viewing changed source bytes.");
        }
        finally { window.Close(); File.Delete(path); }
        Console.WriteLine("PASS STA actual X31 Viewer table, read-only bindings, raw comments/continuations and disclosed metadata");
    }
    internal static void ViewerButtonsHaveIndependentRow()
    {
        var constructor = typeof(AvaQuantityRunWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        Program.Check(constructor.GetParameters().Any(parameter => parameter.Name == "openX31Viewer"), "Mengenermittlung must offer independent X31/X82 file-viewer actions.");
        var x31 = 0; var x82 = 0; var modelCalls = 0;
        var run = new AvaQuantityRun(1, Guid.NewGuid(), DateTimeOffset.UtcNow, "Test", "", "Test", "{}", []);
        var values = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "run" => (object)run, "groupingOptions" => Array.Empty<GroupingParameterOption>(),
            "postSelection" => (Action<IReadOnlyList<string>, bool>)((_, _) => modelCalls++),
            "postX31" => (Action<bool, bool>)((_, _) => modelCalls++), "postX82" => (Action)(() => modelCalls++),
            "postGrouping" => (Action<GroupingParameterOption?>)(_ => { }), "postRecalculate" => (Action<bool>)(_ => modelCalls++),
            "openX31Viewer" => (Action)(() => x31++), "openX82Viewer" => (Action)(() => x82++),
            _ => parameter.DefaultValue
        }).ToArray();
        var window = (AvaQuantityRunWindow)constructor.Invoke(values);
        try
        {
            window.Show();
            var buttons = Descendants<Button>(window).ToDictionary(button => button.Content.ToString()!);
            var currentRun = typeof(AvaQuantityRunWindow).GetProperty("CurrentRun", BindingFlags.Instance | BindingFlags.NonPublic);
            Program.Check(currentRun != null && ReferenceEquals(currentRun.GetValue(window), run), "Host viewer metadata must be sourced from the current run.");
            var replacement = run with { RunId = Guid.NewGuid(), TemplateJson = "new snapshot" };
            window.SetRun(replacement);
            Program.Check(ReferenceEquals(currentRun!.GetValue(window), replacement), "SetRun must refresh the snapshot offered to file viewers.");
            var x31Button = buttons["X31 Viewer"]; var x82Button = buttons["X82 Viewer"];
            Program.Check(x31Button.IsEnabled && x82Button.IsEnabled && Descendants<DataGrid>(window).Single().SelectedItems.Count == 0, "File viewers must be available without selected rows, even on an empty run.");
            Program.Check(Grid.GetRow((UIElement)x31Button.Parent) == 3 && ReferenceEquals(x31Button.Parent, x82Button.Parent)
                && Grid.GetRow((UIElement)buttons["Selektieren"].Parent) == 1
                && Grid.GetRow((UIElement)buttons["X31 exportieren"].Parent) == 2, "Preserve actions and exports in first two rows; place viewers in their own compact third row.");
            x31Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            x82Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(x31 == 1 && x82 == 1 && modelCalls == 0, "File viewing must not reuse model actions or export callbacks.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS STA Mengenermittlung independent viewer buttons in third row, no selection/model/export dependency");
    }
    internal static void OwnedSessionsSurviveCancelAndFailure()
    {
        var type = typeof(AvaQuantityRunWindow).Assembly.GetType("BIMassist.Ava.Views.AvaFileViewerSession");
        Program.Check(type != null, "Missing owned, lifecycle-safe file viewer session.");
        var prefix = Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS")! + ".session";
        var x31Path = prefix + ".x31"; var x82Path = prefix + ".x82"; var badPath = prefix + ".bad";
        using (var input = Program.Xml([Program.Calculation("1=", "B0000001")])) File.WriteAllBytes(x31Path, input.ToArray());
        File.WriteAllText(x82Path, "<GAEB xmlns='http://www.gaeb.de/GAEB_DA_XML/DA82/3.3'><BoQ ID='lv'><BoQInfo><Name>LV</Name></BoQInfo><BoQBody/></BoQ></GAEB>");
        File.WriteAllText(badPath, "not XML");
        string? next = null; var status = ""; var picks = 0;
        var owner = new Window { Width = 900, Height = 500 };
        owner.Show();
        object? session = null;
        try
        {
            Func<Window, string, string?> picker = (actualOwner, extension) =>
            {
                Program.Check(ReferenceEquals(actualOwner, owner) && extension is "x31" or "x82", "File dialog must use the actual main window as owner.");
                picks++; return next;
            };
            session = Activator.CreateInstance(type!, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [owner, (Action<string>)(message => status = message), picker, null], null)!;
            void Open(string extension) => type!.GetMethod("Open" + extension.ToUpperInvariant(), BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);
            int Count() => (int)type!.GetProperty("ChildCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            Open("x31");
            Program.Check(Count() == 0 && owner.IsVisible, "Cancel must not close or disable Mengenermittlung.");
            next = badPath; Open("x31");
            Program.Check(Count() == 0 && status.Contains("nicht geöffnet") && owner.IsEnabled, "Parse failure must be reported without destroying the owner.");
            next = x31Path; Open("x31");
            var first = owner.OwnedWindows.Cast<Window>().Single();
            Program.Check(first is X31FileViewerWindow && Count() == 1, "X31 action must open the real file viewer as owned child.");
            Open("x31");
            Program.Check(!first.IsVisible && Count() == 1 && owner.OwnedWindows.Count == 1, "Repeated opens must replace/clean the previous viewer instead of retaining children.");
            next = x82Path; Open("x82");
            Program.Check(owner.OwnedWindows.OfType<LvX82ViewerWindow>().Count() == 1 && Count() == 2, "X82 must reuse the actual existing viewer, not a duplicate.");
            owner.OwnedWindows.OfType<LvX82ViewerWindow>().Single().Close();
            Program.Check(Count() == 1 && owner.IsVisible, "Closing a child must release its registration without closing Mengenermittlung.");
            owner.Close();
            Program.Check(Count() == 0, "Closing Mengenermittlung must close all children and release references.");
            var calls = picks; Open("x31");
            Program.Check(picks == calls && Count() == 0, "A closed owner must not open dialogs or children again.");
        }
        finally { owner.Close(); File.Delete(x31Path); File.Delete(x82Path); File.Delete(badPath); }
        Console.WriteLine("PASS STA owned X31/X82 session cancel/error/repeat/child-close/owner-close lifecycle using real viewers");
    }
    internal static void MetadataComesFromCurrentSnapshot()
    {
        var path = Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS")! + ".snapshot.x31";
        using (var input = Program.Xml([Program.Calculation("1=")])) File.WriteAllBytes(path, input.ToArray());
        var template = new AvaTemplate { LvCatalog = new(new("", "", ""), [new("item-exact", "lv-exact", ["01", "0020"], "01.0020", "Snapshot alt", "", "m")]) };
        var json = TemplateJson.Serialize(template);
        var owner = new Window(); owner.Show();
        try
        {
            var session = new AvaFileViewerSession(owner, _ => { }, (_, _) => path, () => json);
            session.OpenX31();
            X31ReviewRow Row() => Descendants<DataGrid>(owner.OwnedWindows.Cast<Window>().Single()).Single().Items.Cast<X31ReviewRow>().Single();
            Program.Check(Row().ShortText == "Snapshot alt" && Row().Unit == "m", "Exact metadata must come from the persisted run's snapshot, not a missing or moved template file.");
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Text = "Snapshot neu" };
            json = TemplateJson.Serialize(template);
            session.OpenX31();
            Program.Check(Row().ShortText == "Snapshot neu", "Reopened viewer must read refreshed snapshot metadata, not a stale session catalog.");
            json = "broken";
            session.OpenX31();
            Program.Check(Row().ShortText == "" && Row().Unit == "", "Unavailable optional snapshot must not block file viewing or invent metadata.");
        }
        finally { owner.Close(); File.Delete(path); }
        Console.WriteLine("PASS STA X31 exact metadata uses current in-memory run snapshot; invalid optional metadata does not block file viewing");
    }
    internal static void DispatcherShutdownReleasesChildren()
    {
        var path = Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS")! + ".shutdown.x31";
        using (var input = Program.Xml([Program.Calculation("1=")])) File.WriteAllBytes(path, input.ToArray());
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Window? owner = null;
            try
            {
                owner = new Window(); owner.Show();
                var picks = 0;
                var session = new AvaFileViewerSession(owner, _ => { }, (_, _) => { picks++; return path; });
                session.OpenX31();
                Program.Check(session.ChildCount == 1, "Shutdown test did not open a real viewer.");
                owner.Dispatcher.ShutdownStarted += (_, _) =>
                {
                    Program.Check(session.ChildCount == 0, "Dispatcher shutdown must release viewers before later shutdown handlers execute.");
                    session.OpenX31();
                    Program.Check(picks == 1, "Shutdown must prevent file dialogs even before HasShutdownStarted becomes true.");
                    owner.Close();
                };
                owner.Dispatcher.InvokeShutdown();
            }
            catch (Exception ex) { error = ex; }
            finally { if (owner?.IsVisible == true) owner.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Program.Check(thread.Join(TimeSpan.FromSeconds(30)), "STA shutdown did not finish.");
        File.Delete(path);
        if (error != null) throw new Exception("STA dispatcher shutdown failed", error);
        Console.WriteLine("PASS STA dispatcher shutdown closes children, detaches lifecycle hooks and prevents new dialogs");
    }
    internal static void UnshownOwnerCanReleaseSession()
    {
        var owner = new Window(); var picks = 0;
        var session = new AvaFileViewerSession(owner, _ => { }, (_, _) => { picks++; return null; });
        Program.Check((object)session is IDisposable, "A failed owner Show must be able to detach the session's dispatcher hook without waiting for owner closure.");
        ((IDisposable)(object)session).Dispose();
        session.OpenX31(); session.OpenX82();
        Program.Check(picks == 0 && session.ChildCount == 0, "Disposed/unshown sessions must neither open a picker nor retain children.");
        owner.Close();
        Console.WriteLine("PASS failed/unshown owner can explicitly release file viewer session");
    }
    internal static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            foreach (var nested in Descendants<T>(child)) yield return nested;
    }
}