using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var t = new AvaTemplate { ReviewScope = "Flansche" };
            t.BgkCatalog.Entries.Add(new("44-40", "Flansche", "", [new("110", "Losflansch")]));
            t.LvCatalog.Entries.Add(new("pieces", "b", ["01"], "01", "Flansch", "", "St"));
            t.LvCatalog.Entries.Add(new("length", "b", ["02"], "02", "Leistung", "", "m"));
            var first = new MappingPosition { LvItemId = "pieces", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" } };
            var second = new MappingPosition { LvItemId = "length", Quantity = new() { Kind = QuantityKind.Formula, Source = null, Unit = "m", Expression = "2 * AVA_Laenge + 0.5", Variables = [new() { Name = "AVA_Laenge", Source = new() { Kind = ParameterKind.LocalFamily, Name = "AVA_Laenge", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" }] } };
            var rule = new MappingRule { BgkKey = "44-40", TypeNumber = "110", Positions = [first] }; t.Mappings.Add(rule);
            var group = new MappingEditorWindow(t, rule); group.UpsertPosition(second);
            Check(group.BuildRule().Positions.Count == 2 && rule.Positions.Count == 1, "child add mutates original/loses sibling");
            var change = TemplateJson.Copy(second); change.Quantity!.Expression = "3 * AVA_Laenge"; group.UpsertPosition(change);
            Check(group.BuildRule().Positions.Count == 2 && group.BuildRule().Positions[1].Id == second.Id, "child edit duplicates identity");
            group.RemovePosition(second.Id); Check(group.BuildRule().Positions.Count == 1, "child removal lost sibling"); group.UpsertPosition(second);
            Field<TextBox>(group, "bgkSearch").Text = "44-40"; Check(group.BuildRule().TypeNumber == "110", "BGK filter clears surviving type");
            t.Mappings = [group.BuildRule()]; group.Close(); TemplateEditor.EnsureValid(t);
            var position = new PositionEditorWindow(t, second); Layout(position);
            Check(Field<ListBox>(position, "variables").DisplayMemberPath == "Name", "formula list shows class names instead of parameter names");
            Check(Field<DataGrid>(position, "testValues").Columns[0].MinWidth >= 200, "formula input name column can collapse in scroll layout");
            Check(position.BuildPosition().Quantity!.Variables.Single().Source.Name == "AVA_Laenge", "formula binding lost");
            Check(position.EvaluatePreview(new Dictionary<string, decimal> { ["AVA_Laenge"] = 2 }) == 4.5m, "arithmetic incorrect");
            var inputRow = Field<DataGrid>(position, "testValues").ItemsSource.Cast<object>().Single();
            var inputValue = inputRow.GetType().GetProperty("Value")!; inputValue.SetValue(inputRow, "2");
            Field<StackPanel>(position, "formulaPanel").Children.OfType<Button>().Single(b => (string)b.Content == "Mit Testwerten berechnen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<TextBlock>(position, "preview").Text.Contains("4,5"), "preview button does not calculate current test input");
            inputValue.SetValue(inputRow, "3");
            Check(Field<TextBlock>(position, "preview").Text.Length == 0, "changed test value leaves stale formula result visible");
            if (args.Length > 1) Render(position, args[1], "formula.png", 1);
            Field<TextBox>(position, "expression").Text = "AVA_Laenge / 0"; Reject(() => position.EvaluatePreview(new Dictionary<string, decimal> { ["AVA_Laenge"] = 2 }));
            Field<TextBox>(position, "expression").Text = "Unknown + 1"; Reject(() => position.EvaluatePreview(new Dictionary<string, decimal> { ["AVA_Laenge"] = 2 })); position.Close();
            Check(MappingTreeModel.Build(t).Single().Children.Single().Children.Single().Children.Count == 2, "wrong BGK/type/group/position tree");
            var temp = Path.Combine(Path.GetTempPath(), "ava-group-ui-" + Guid.NewGuid() + ".json");
            try
            {
                TemplateJson.Save(temp, t); var main = new AvaTemplateWindow(); Field<TemplateEditor>(main, "editor").Open(temp); main.RefreshMappings(); Layout(main);
                var tree = (TreeView)main.FindName("MappingsTree"); var root = (TreeViewItem)tree.Items[0]; var type = (TreeViewItem)root.Items[0]; var row = (TreeViewItem)type.Items[0];
                row.IsExpanded = false; type.IsSelected = true; main.RefreshMappings(); Layout(main);
                root = (TreeViewItem)tree.Items[0]; type = (TreeViewItem)root.Items[0]; row = (TreeViewItem)type.Items[0];
                Check(!row.IsExpanded && row.Items.Count == 2, "collapsed state/children lost on refresh");
                Check((tree.SelectedItem as TreeViewItem)?.Tag is MappingTreeNode node && node.Key == ((MappingTreeNode)type.Tag).Key, "selection lost on refresh");
                if (args.Length > 1) { row.IsExpanded = true; Render(main, args[1], "grouped.png", 2); }
                main.Close();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            if (args.Length > 0)
            {
                var old = TemplateJson.Load(args[0]); TemplateEditor.EnsureValid(old); var nodes = MappingTreeModel.Build(old);
                Check(nodes.Single().Children.Single().Children.Count == 13 && nodes.Single().Children.Single().Children.Sum(n => n.Children.Count) == 13, "old DN rules merged or lost");
            }
            Console.WriteLine("PASS multi-position isolation, formula preview/errors, BGK filtering, tree hierarchy, expansion/selection and old template"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Layout(Window w) { w.Measure(new Size(1200, 850)); w.Arrange(new Rect(0, 0, 1200, 850)); w.UpdateLayout(); }
    private static void Render(Window w, string directory, string name, int tab)
    {
        Directory.CreateDirectory(directory);
        var root = (DockPanel)w.Content; root.Children.OfType<TabControl>().Single().SelectedIndex = tab;
        Layout(w); root.Measure(new Size(1200, 850)); root.Arrange(new Rect(0, 0, 1200, 850)); root.UpdateLayout();
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen()) { drawing.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, 1200, 850)); drawing.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, 1200, 850)); }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 850, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
    }
    private static T Field<T>(object o, string name) => (T)(o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o) ?? throw new Exception("Missing UI field: " + name));
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid formula accepted"); }
}
