using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            AddedPositionOpensItsQuantityEditor();
            Console.WriteLine("PASS newly added LV position opens quantity editor without reselecting");
            PickerDisplaysSourceUnit();
            Console.WriteLine("PASS picker displays immutable source LV unit");
            DoubleClickOpensExactSourceLongTextWithoutSelecting();
            Console.WriteLine("PASS double-click opens exact long text separately without confirming selection");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static AvaTemplate Fixture()
    {
        var template = new AvaTemplate { ReviewScope = "Mapping picker UI test" };
        template.BgkCatalog.Entries.Add(new("01-10", "BGK", "", [new("100", "Type")]));
        template.LvCatalog.Entries.AddRange([
            new("first", "boq", ["01"], "01", "First", "First source long text", "St"),
            new("second", "boq", ["02"], "02", "Second", "Exact source\nlong text, not a synthesized summary.", "m²"),
            new("third", "boq", ["03"], "03", "Third", "Third source long text", "m")]);
        return template;
    }

    private static void AddedPositionOpensItsQuantityEditor()
    {
        var template = Fixture();
        var saved = new MappingPosition { LvItemId = "first", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" } };
        var group = new MappingEditorWindow(template, new MappingRule { BgkKey = "01-10", TypeNumber = "100", Positions = [saved] });
        group.ShowInTaskbar = false; group.WindowStartupLocation = WindowStartupLocation.Manual; group.Left = -10000; group.Top = -10000;
        bool pickerAccepted = false, quantityTabActive = true;
        var editedPositions = new HashSet<string>(StringComparer.Ordinal);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (!pickerAccepted && group.OwnedWindows.OfType<LvPositionPickerWindow>().FirstOrDefault() is { } picker)
            {
                picker.SetNodeChecked(Descendants(CatalogTreeModel.BuildLv(template)).Single(node => node.Key == "item:second"), true);
                picker.SetNodeChecked(Descendants(CatalogTreeModel.BuildLv(template)).Single(node => node.Key == "item:third"), true);
                pickerAccepted = true;
                Click(picker, "Auswahl übernehmen");
            }
            if (group.OwnedWindows.OfType<PositionEditorWindow>().FirstOrDefault() is { } editor)
            {
                var tabs = ((DockPanel)editor.Content).Children.OfType<TabControl>().Single();
                quantityTabActive &= tabs.SelectedIndex == 1;
                editedPositions.Add(editor.BuildPosition().LvItemId);
                Click(editor, "Abbrechen");
            }
        };
        try
        {
            group.Show(); timer.Start();
            Click(group, "Leistungsposition(en) hinzufügen…");
            Check(pickerAccepted && editedPositions.SetEquals(["second", "third"]) && quantityTabActive,
                "Every newly added position must immediately open its own quantity tab, not only the first position");
            var positionTab = (TabItem)((TabControl)((DockPanel)group.Content).Children.OfType<TabControl>().Single()).Items[2];
            var grid = ((DockPanel)positionTab.Content).Children.OfType<DataGrid>().Single();
            Check(grid.SelectedItem != null && grid.SelectedItem.GetType().GetProperty("Position")!.GetValue(grid.SelectedItem) is MappingPosition { LvItemId: "third" },
                "Last newly added position was not selected after returning from its quantity editor");
            Check(group.BuildRule().Positions.Count == 3 && group.BuildRule().Positions.Single(p => p.LvItemId == "first").Quantity?.Unit == "St",
                "Adding additional positions altered the saved sibling quantity");
            Check(template.LvCatalog.Entries.Single(p => p.Id == "second").Unit == "m²", "Source LV unit was mutated");
        }
        finally { timer.Stop(); group.Close(); }
    }

    private static void DoubleClickOpensExactSourceLongTextWithoutSelecting()
    {
        var template = Fixture();
        var picker = new LvPositionPickerWindow(template) { ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        try
        {
            picker.Show();
            var row = PickerRow(picker, "item:second");
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent });
            var detail = picker.OwnedWindows.OfType<Window>().SingleOrDefault();
            Check(detail != null, "Double-click did not open a separate long-text window");
            var text = ((DockPanel)detail!.Content).Children.OfType<TextBox>().Single();
            Check(text.IsReadOnly && text.Text == template.LvCatalog.Entries.Single(p => p.Id == "second").Description,
                "Detail window did not show the exact source LV long text");
            Check(picker.SelectedPositionIds.Count == 0 && picker.IsVisible,
                "Double-click confirmed selection or dismissed the picker");
            detail.Close();
        }
        finally { picker.Close(); }
    }

    private static void PickerDisplaysSourceUnit()
    {
        var template = Fixture();
        var picker = new LvPositionPickerWindow(template);
        try
        {
            var row = PickerRow(picker, "item:second");
            var label = ((StackPanel)row.Header).Children.OfType<TextBlock>().Single().Text;
            Check(label.Contains("m²", StringComparison.Ordinal) && label.Contains("02 Second", StringComparison.Ordinal),
                "LV picker overview omits the source unit beside the position");
            Check(template.LvCatalog.Entries.Single(p => p.Id == "second").Unit == "m²", "Picker modified LV source unit");
        }
        finally { picker.Close(); }
    }

    private static TreeViewItem PickerRow(LvPositionPickerWindow picker, string key)
    {
        var tree = (TreeView)((DockPanel)picker.Content).Children.OfType<TreeView>().Single();
        TreeViewItem? Find(ItemsControl parent)
        {
            foreach (TreeViewItem item in parent.Items)
            {
                if (item.Tag is CatalogTreeNode node && node.Key == key) return item;
                if (Find(item) is { } nested) return nested;
            }
            return null;
        }
        return Find(tree) ?? throw new Exception("Missing picker position " + key);
    }

    private static void Click(Window window, string label)
    {
        var buttons = ((DockPanel)window.Content).Children.OfType<WrapPanel>().SelectMany(panel => panel.Children.OfType<Button>())
            .Concat(((DockPanel)window.Content).Children.OfType<TabControl>().SelectMany(tabs => tabs.Items.OfType<TabItem>())
                .SelectMany(tab => (tab.Content as DockPanel)?.Children.OfType<WrapPanel>() ?? [])
                .SelectMany(panel => panel.Children.OfType<Button>()));
        var button = buttons.Single(b => Equals(b.Content, label));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static IEnumerable<CatalogTreeNode> Descendants(IEnumerable<CatalogTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Descendants(node.Children)) yield return child;
        }
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
