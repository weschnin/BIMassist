using BIMassist.Ava.Core;
using BIMassist.Ava.Views;
using System.Windows;
using System.Windows.Controls;

internal static class QuantityRunWindowTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    internal static AvaQuantityRecord Record(string id = "00012", decimal result = 3.123456m) => new(
        Guid.NewGuid(), "B0000001", "item", "0010", "Menge", "m", id,
        "Length", "Length=3.123456", "Teilbereich (Instanz)", "Raum A", "Familie", "Typ", result)
    {
        GroupingValues = new Dictionary<string, string> { ["Teilbereich (Instanz)"] = "Raum A", ["Gewerk (Typ)"] = "Rohbau" }
    };

    internal static AvaQuantityRun Run(params AvaQuantityRecord[] records) => new(1, Guid.NewGuid(),
        DateTimeOffset.UtcNow, "Testprojekt", "", "aktive Ansicht", "{}", records);

    internal static IEnumerable<WrapPanel> Toolbars(AvaQuantityRunWindow window) => ((Grid)window.Content).Children.OfType<WrapPanel>();
    internal static DataGrid Grid(AvaQuantityRunWindow window) => ((Grid)window.Content).Children.OfType<DataGrid>().Single();
    internal static ComboBox Grouping(AvaQuantityRunWindow window) => Toolbars(window).SelectMany(panel => panel.Children.OfType<ComboBox>())
        .Single(combo => combo.DisplayMemberPath == "DisplayName");
    internal static Button Button(AvaQuantityRunWindow window, string label) => Toolbars(window).SelectMany(panel => panel.Children.OfType<Button>())
        .Single(button => Equals(button.Content, label));
    internal static void Choose(AvaQuantityRunWindow window, string label)
    {
        var selector = Grouping(window);
        selector.SelectedItem = selector.Items.Cast<object>().Single(item =>
            Equals(item.GetType().GetProperty("DisplayName")!.GetValue(item), label));
    }
    private static object? Value(object row, string key) => row.GetType().GetProperty(key)!.GetValue(row);
    private static AvaQuantityViewSettings Capture(AvaQuantityRunWindow window) => window.CaptureSettings();

    private static AvaQuantityRunWindow WithSettings(AvaQuantityRun run, AvaQuantityViewSettings settings,
        Action<AvaQuantityViewSettings> changed, Action<bool>? missing = null, Action<AvaQuantityTable>? excel = null)
        => new(run, [], (_, _) => { }, (_, _) => { }, () => { }, _ => { }, _ => { },
            settings: settings, postSettings: changed, postMissing: missing, postExcel: excel);

    internal static void SettingsRoundTripAndRefresh()
    {
        var custom = new AvaQuantityViewSettings
        {
            Columns = [new("Result", 0, true, 177), new("BimassistId", 1, false, 123)],
            Sorts = [new("Result", true), new("Ordinal", false)],
            Grouping = "Gewerk (Typ)", VisibleOnly = true, X31UseRevitElementId = true
        };
        var changes = new List<AvaQuantityViewSettings>();
        var run = Run(Record());
        var window = WithSettings(run, custom, changes.Add);
        AvaQuantityViewSettings captured;
        var beforeClose = -1;
        try
        {
            Check(changes.Count == 0, "Restore and initialization must not post settings changes.");
            var grid = Grid(window);
            var result = grid.Columns.Single(column => column.SortMemberPath == "Result");
            var id = grid.Columns.Single(column => column.SortMemberPath == "BimassistId");
            Check(result.DisplayIndex == 0 && result.Width.Value == 177 && id.Visibility == Visibility.Collapsed,
                "Restore must apply stable column keys, visibility, display order, and widths.");
            captured = Capture(window);
            Check(captured.Sorts.SequenceEqual(custom.Sorts) && captured.Grouping == custom.Grouping && captured.VisibleOnly && captured.X31UseRevitElementId,
                "Capture must retain all sort keys/directions, grouping, view scope, and X31 ID choice.");
            Check(grid.Items.SortDescriptions.Count == 2 && result.SortDirection == System.ComponentModel.ListSortDirection.Descending,
                "Sort restoration must affect the actual collection view and sort glyph.");
            result.Width = 201;
            Check(changes.Count > 0 && changes[^1].Columns.Single(column => column.Key == "Result").Width == 201,
                "Resizing a column must post the current settings.");
            id.Visibility = Visibility.Visible;
            Check(changes[^1].Columns.Single(column => column.Key == "BimassistId").Visible, "Column visibility changes must post settings.");
            id.DisplayIndex = 0;
            Check(changes[^1].Columns.Single(column => column.Key == "BimassistId").DisplayIndex == 0, "Column order changes must post settings.");
            grid.Items.SortDescriptions.Clear();
            grid.Items.SortDescriptions.Add(new("Ordinal", System.ComponentModel.ListSortDirection.Ascending));
            Check(changes[^1].Sorts.SequenceEqual([new AvaQuantitySortSetting("Ordinal", false)]), "Sort changes must post settings.");
            var before = Capture(window);
            var count = changes.Count;
            window.SetRun(run with { Records = [Record("17", 8m)] });
            captured = Capture(window);
            Check(captured.Columns.SequenceEqual(before.Columns) && captured.Sorts.SequenceEqual(before.Sorts)
                && captured.Grouping == before.Grouping && captured.VisibleOnly && captured.X31UseRevitElementId,
                "SetRun must preserve the user's current presentation instead of reinitializing it.");
            Check(changes.Count == count, "SetRun must suppress transient restore notifications.");
            window.SetRun(run with { Records = [Record() with { GroupingValues = new Dictionary<string, string>(), GroupingParameter = "", GroupingValue = "" }] });
            Check(Capture(window).Grouping == "" && Equals(Value(grid.Items[0], "GroupingValue"), ""),
                "A parameter absent from the refreshed snapshot must fall back to no grouping, never reuse stale values.");
            window.SetRun(run);
            Check(Capture(window).Grouping == "", "An explicitly selected/fallback none must stay none on later refresh.");
            captured = Capture(window);
            beforeClose = changes.Count;
        }
        finally { window.Close(); }
        Check(changes.Count == beforeClose + 1 && changes[^1].Columns.SequenceEqual(captured.Columns)
            && changes[^1].Sorts.SequenceEqual(captured.Sorts) && changes[^1].Grouping == captured.Grouping,
            "Closing must post final settings exactly once, even after a suppressed refresh.");
        var reopened = WithSettings(run, changes[^1], _ => { });
        try
        {
            var restored = Capture(reopened);
            Check(restored.Columns.SequenceEqual(captured.Columns) && restored.Sorts.SequenceEqual(captured.Sorts)
                && restored.Grouping == "" && restored.VisibleOnly && restored.X31UseRevitElementId,
                "Reopening must restore all captured settings, including an explicit no-grouping choice.");
        }
        finally { reopened.Close(); }
        Console.WriteLine("PASS: settings restore/capture, change callbacks, close/reopen, and refresh retention (real WPF).");
    }

    internal static void TwoActionRowsAndMissingCallbacks()
    {
        bool? missing = null;
        var changes = new List<AvaQuantityViewSettings>();
        var window = WithSettings(Run(Record()), new(), changes.Add, isolate => missing = isolate);
        try
        {
            var toolbars = Toolbars(window).OrderBy(System.Windows.Controls.Grid.GetRow).ToArray();
            Check(toolbars.Length == 3, "The window must retain distinct model/export rows and a third file-viewer row.");
            Check(toolbars[0].Children.OfType<Control>().Take(4).Select(control => control is ComboBox ? "Gruppierung" : ((Button)control).Content?.ToString())
                .SequenceEqual(["Gruppierung", "Neu berechnen", "Selektieren", "Isolieren"]),
                "Row one must start with grouping, recalculate, select, isolate in that order.");
            Check(toolbars[1].Children.OfType<Control>().Select(control => control is ComboBox ? "X31-Bemerkung" : ((Button)control).Content?.ToString())
                .SequenceEqual(["X31-Bemerkung", "X31 exportieren", "X82 exportieren", "Excel exportieren"]),
                "Row two must contain the identity selector and X31, X82, Excel exports in that order.");
            Check(toolbars[2].Children.OfType<Button>().Select(button => button.Content?.ToString()).SequenceEqual(["X31 Viewer", "X82 Viewer"]),
                "Row three must contain the independent file viewers, not model/preflight actions.");
            Check(missing == null, "Initialization must not post missing actions.");
            Button(window, "Fehlende selektieren").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(missing == false, "Missing select must post false without any row selection.");
            Button(window, "Fehlende isolieren").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(missing == true, "Missing isolate must post true independently of row selection.");
            Check(Button(window, "Fehlende selektieren").ToolTip?.ToString()?.Contains("aktiven Ansicht") == true
                && Button(window, "Fehlende isolieren").ToolTip?.ToString()?.Contains("Kandidaten") == true,
                "Missing action tooltips must explain candidate scope in the active view.");
            Check(((Grid)window.Content).Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>())
                .Any(text => text.Text.Contains("Kandidaten") && text.Text.Contains("aktiven Ansicht")),
                "The heading must explain missing candidates within the active view.");
            Choose(window, "OZ");
            Check(changes[^1].Grouping == "OZ", "Grouping choices must post current settings.");
            var visible = toolbars[0].Children.OfType<CheckBox>().Single();
            visible.IsChecked = true;
            Check(changes[^1].VisibleOnly, "Visible-only changes must post settings.");
            var identity = toolbars[1].Children.OfType<ComboBox>().Single();
            identity.SelectedIndex = 1;
            Check(changes[^1].X31UseRevitElementId, "X31 identity changes must post settings.");
        }
        finally { window.Close(); }
        var runScope = new AvaQuantityRunWindow(Run(Record()) with { VisibleOnly = true }, [], (_, _) => { }, (_, _) => { }, () => { }, _ => { }, _ => { });
        try { Check(Capture(runScope).VisibleOnly, "Without saved view settings the checkbox must use the run's captured VisibleOnly scope."); }
        finally { runScope.Close(); }
        var savedScope = WithSettings(Run(Record()) with { VisibleOnly = true }, new() { VisibleOnly = false }, _ => { });
        try { Check(!Capture(savedScope).VisibleOnly, "Stored view settings must override run scope, including an explicit false."); }
        finally { savedScope.Close(); }
        Console.WriteLine("PASS: ordered model/export rows plus file-viewer row, missing callbacks, scope defaults, and settings controls (real WPF).");
    }

    internal static void ExcelCapturesCurrentPresentation()
    {
        var records = new[]
        {
            Record("00012", 3.123456m) with { Ordinal = "A" },
            Record("00013", 2.987654m) with { Ordinal = "B", BimassistId = "B0000002" },
            Record("00014", 1.99999m) with { Ordinal = "A", BimassistId = "B0000003" }
        };
        AvaQuantityTable? exported = null;
        var window = WithSettings(Run(records), new() { Grouping = "OZ", Sorts = [new("Result", true)] }, _ => { }, excel: table => exported = table);
        try
        {
            var grid = Grid(window);
            foreach (var column in grid.Columns) column.Visibility = Visibility.Collapsed;
            var id = grid.Columns.Single(column => column.SortMemberPath == "RevitElementId");
            var result = grid.Columns.Single(column => column.SortMemberPath == "Result");
            var groupingValue = grid.Columns.Single(column => column.SortMemberPath == "GroupingValue");
            result.Visibility = id.Visibility = groupingValue.Visibility = Visibility.Visible;
            groupingValue.DisplayIndex = 0;
            result.DisplayIndex = 1;
            id.DisplayIndex = 2;
            result.Width = 185;
            Button(window, "Excel exportieren").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(exported != null && exported.Columns.Select(column => column.Key).SequenceEqual(["GroupingValue", "Result", "RevitElementId"]),
                "Excel must contain only currently visible columns, ordered by DisplayIndex.");
            Check(exported!.Columns[1].Header == "Ergebnis" && exported.Columns[1].Width == 185,
                "Excel must keep current headers and display widths.");
            var groupedIds = grid.Items.Groups!.Cast<System.Windows.Data.CollectionViewGroup>().SelectMany(group => group.Items)
                .Select(row => Value(row, "RevitElementId")).ToArray();
            Check(exported.Rows.Select(row => row[2]).SequenceEqual(groupedIds)
                && exported.Rows.Select(row => row[2]).SequenceEqual(["00012", "00014", "00013"]),
                "Excel must use the actual sorted/grouped leaf-row order, not the original records.");
            Check(exported.Rows.Select(row => row[1]).SequenceEqual([3.123m, 2m, 2.988m]),
                "Excel results must be rounded numeric decimals with no more than three places.");
            Check(exported.Rows.All(row => row[2] is string) && records[0].Result == 3.123456m,
                "Excel IDs must stay text and display rounding must not mutate the full stored result.");
            Choose(window, "Keine Gruppierung");
            grid.Items.SortDescriptions.Clear();
            grid.Items.SortDescriptions.Add(new("Result", System.ComponentModel.ListSortDirection.Ascending));
            Button(window, "Excel exportieren").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(exported.Rows.Select(row => row[2]).SequenceEqual(["00014", "00013", "00012"])
                && exported.Rows.All(row => Equals(row[0], "")), "Each export must recapture current sorting and grouping, not reuse a stale table.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS: Excel visible columns/order/widths, live sorted/grouped rows, numeric quantities, and textual IDs (real WPF).");
    }

    internal static void ExcelEmptyStateIsVisible()
    {
        var posts = 0;
        var window = WithSettings(Run(), new(), _ => { }, excel: _ => posts++);
        try
        {
            Button(window, "Excel exportieren").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(posts == 0 && ((Grid)window.Content).Children.OfType<TextBlock>().Any(text => text.Text.Contains("Keine Datensätze")),
                "An empty run must visibly explain why Excel export cannot proceed, without posting an empty table.");
            window.SetRun(Run(Record()));
            foreach (var column in Grid(window).Columns) column.Visibility = Visibility.Collapsed;
            Button(window, "Excel exportieren").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(posts == 0 && ((Grid)window.Content).Children.OfType<TextBlock>().Any(text => text.Text.Contains("Keine sichtbaren Spalten")),
                "All-hidden columns must visibly block export rather than leaking default/hidden fields.");
            var captured = window.CaptureTable();
            Check(captured.Columns.Count == 0 && captured.Rows.All(row => row.Count == 0), "Capturing all-hidden columns must not leak any cell data.");
            var hidden = Capture(window);
            var reopened = WithSettings(Run(Record()), hidden, _ => { });
            try { Check(Grid(reopened).Columns.All(column => column.Visibility == Visibility.Collapsed), "All-hidden state must survive reopen; never silently restore defaults."); }
            finally { reopened.Close(); }
            var menu = Grid(window).ContextMenu;
            Check(menu != null && menu.Items.Count == Grid(window).Columns.Count,
                "The table must keep a reachable column menu even when every header is hidden.");
            var showColumn = menu!.Items.OfType<MenuItem>().First();
            showColumn.IsChecked = true;
            showColumn.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(Grid(window).Columns[0].Visibility == Visibility.Visible, "The all-hidden state must be recoverable through the table menu.");
            Button(window, "Excel exportieren").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(posts == 1, "Excel export must become available after restoring a visible column.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS: visible Excel empty/all-hidden states without data leakage (real WPF).");
    }

    internal static void DisplayRoundsWithoutChangingStoredValues()
    {
        var record = Record() with
        {
            FormulaWithParameters = "x * y [x ← Volumen | VOLUME | Instance | HOST_VOLUME_COMPUTED; y ← Länge | LENGTH | Instance | CURVE_ELEM_LENGTH]",
            FormulaWithValues = "3.123456 * 2.987654"
        };
        var window = WithSettings(Run(record), new(), _ => { });
        try
        {
            var grid = Grid(window);
            var result = (DataGridTextColumn)grid.Columns.Single(column => column.SortMemberPath == "Result");
            Check(((System.Windows.Data.Binding)result.Binding).StringFormat == "0.###", "Numeric result display must use max-three-decimal binding formatting.");
            var row = grid.Items[0];
            Check(Equals(Value(row, "Result"), 3.123m), "Display must round results through the shared helper.");
            Check(Equals(Value(row, "FormulaWithParameters"), "x * y"), "Symbolic display must remove legacy technical source bindings through the shared helper.");
            Check(Equals(Value(row, "FormulaWithValues"), "3.123 * 2.988"), "Numeric formula display must round literals through the shared helper.");
            Check(record.Result == 3.123456m && record.FormulaWithValues == "3.123456 * 2.987654" && record.FormulaWithParameters.Contains("HOST_VOLUME_COMPUTED"),
                "Display cleanup must not mutate the stored full result or formulas.");
            var table = window.CaptureTable();
            var symbolicIndex = table.Columns.Select(column => column.Key).ToList().IndexOf("FormulaWithParameters");
            var numericIndex = table.Columns.Select(column => column.Key).ToList().IndexOf("FormulaWithValues");
            Check(Equals(table.Rows[0][symbolicIndex], "x * y") && Equals(table.Rows[0][numericIndex], "3.123 * 2.988"),
                "Excel must use the same cleaned display formulas, not hidden legacy metadata.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS: max-three-decimal result and formula displays, preserving full run values (real WPF).");
    }

    internal static void GroupingAlwaysReflectsCurrentChoice()
    {
        var window = new AvaQuantityRunWindow(Run(Record()), [], (_, _) => { }, (_, _) => { }, () => { }, _ => { }, _ => { });
        try
        {
            var grid = Grid(window);
            Check(!grid.Columns.Any(column => Equals(column.Header, "Gruppierungsparameter")),
                "Only Gruppierungswert, not a redundant parameter column, belongs in the grid.");
            Choose(window, "Keine Gruppierung");
            Check(Equals(Value(grid.Items[0], "GroupingValue"), ""), "No grouping must show an empty grouping value, not the initial parameter value.");
            Choose(window, "OZ");
            Check(Equals(Value(grid.Items[0], "GroupingValue"), "0010"), "OZ grouping must show the ordinal in Gruppierungswert.");
            Choose(window, "Familie und Typ");
            Check(Equals(Value(grid.Items[0], "GroupingValue"), "Familie — Typ"), "Family grouping must show family/type.");
            Choose(window, "Gewerk (Typ)");
            Check(Equals(Value(grid.Items[0], "GroupingValue"), "Rohbau"), "Parameter grouping must show its own snapshot value.");
            Console.WriteLine("PASS: current grouping value and single grouping column (real WPF, no live Revit).");
        }
        finally { window.Close(); }
    }
}
