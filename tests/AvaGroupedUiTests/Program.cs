using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
            t.LvCatalog.Entries.Add(new("nested", "b", ["01", "10", "010"], "01.10.010", "Ventil", "Detail", "St"));
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
            var hierarchicalPosition = new PositionEditorWindow(t, first); Layout(hierarchicalPosition);
            var positionLvTree = Field<TreeView>(hierarchicalPosition, "lvTree");
            Check(positionLvTree.Items.Cast<TreeViewItem>().Any(x => ((TextBlock)x.Header).Text.StartsWith("LV ", StringComparison.Ordinal)),
                "Position editor does not display the LV hierarchy");
            Check(hierarchicalPosition.BuildPosition().LvItemId == "pieces", "Hierarchical LV view lost the selected position");
            Check(Field<TextBox>(hierarchicalPosition, "lvDetail").Text.Contains("Flansch", StringComparison.Ordinal),
                "Position editor does not show details for the selected LV position");
            var positionLvRoot = (TreeViewItem)positionLvTree.Items[0];
            var positionLvTitle = positionLvRoot.Items.Cast<TreeViewItem>().First(x => ((TextBlock)x.Header).Text.Contains("Titel 01"));
            Field<Button>(hierarchicalPosition, "lvCollapseAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!positionLvTitle.IsExpanded, "Position editor LV collapse-all did not collapse titles");
            Field<Button>(hierarchicalPosition, "lvExpandAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(positionLvTitle.IsExpanded, "Position editor LV expand-all did not expand titles");
            var nestedPositionTitle = FindTreeItem(positionLvTree, "Titel 01.10"); nestedPositionTitle.IsExpanded = false;
            Field<TextBox>(hierarchicalPosition, "lvSearch").Text = "Leistung";
            Field<TextBox>(hierarchicalPosition, "lvSearch").Text = "";
            nestedPositionTitle = FindTreeItem(positionLvTree, "Titel 01.10");
            Check(!nestedPositionTitle.IsExpanded, "LV title expansion state was lost after filtering and refreshing the tree");
            if (args.Length > 1 && args[0] == "--render") RenderContent(hierarchicalPosition, args[1], "position-editor.png", 1050, 850);
            hierarchicalPosition.Close();
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
            var bgkTree = CatalogTreeModel.BuildBgk(t, "110");
            Check(bgkTree.Count == 1 && bgkTree[0].Children.Single().Label.Contains("Typ 110"), "BGK source tree does not retain BGK → type hierarchy or filtering");
            var lvTree = CatalogTreeModel.BuildLv(t);
            Check(lvTree.Count == 1 && lvTree[0].Children.Any(x => x.Label.Contains("Titel 01")), "LV source tree lacks title hierarchy");
            var lvTitle = lvTree[0].Children.Single(x => x.Label.Contains("Titel 01"));
            Check(lvTitle.Children.Any(x => x.Label.StartsWith("OZ 01 —", StringComparison.Ordinal)), "LV leaves do not show the OZ number");
            var nestedTitle = lvTitle.Children.Single(x => x.Label == "Titel 01.10");
            Check(nestedTitle.Children.Single().Label.StartsWith("OZ 01.10.010 —", StringComparison.Ordinal), "Nested LV position does not show its OZ number");
            Check(!nestedTitle.Children.Single().Label.Contains("nested", StringComparison.Ordinal), "LV internal key/GUID leaked into the visible row");
            var picker = new LvPositionPickerWindow(t);
            int confirmedCount = 0; picker.TitleSelectionPrompt = count => { confirmedCount = count; return true; };
            Check(picker.SetNodeChecked(lvTitle, true) && confirmedCount == 2 && picker.SelectedPositionIds.Count == 2,
                "Selecting a title did not confirm and select all descendant LV positions");
            var rejectedPicker = new LvPositionPickerWindow(t) { TitleSelectionPrompt = _ => false };
            Check(!rejectedPicker.SetNodeChecked(lvTitle, true) && rejectedPicker.SelectedPositionIds.Count == 0,
                "Declining title confirmation still selected its positions");
            var interactivePicker = new LvPositionPickerWindow(t) { TitleSelectionPrompt = _ => true };
            var interactiveTree = Field<TreeView>(interactivePicker, "tree");
            var interactiveRoot = (TreeViewItem)interactiveTree.Items[0]; interactiveRoot.IsExpanded = false;
            var interactiveTitle = FindTreeItem(interactiveTree, "Titel 01");
            var titleCheck = ((StackPanel)interactiveTitle.Header).Children.OfType<CheckBox>().Single();
            titleCheck.IsChecked = true; titleCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(interactivePicker.SelectedPositionIds.Count == 2 && !interactiveRoot.IsExpanded,
                "Title checkbox did not select all descendants or reset the collapsed tree");
            int filteredConfirmationCount = 0;
            var filteredPicker = new LvPositionPickerWindow(t) { TitleSelectionPrompt = count => { filteredConfirmationCount = count; return true; } };
            var filteredTopTitle = CatalogTreeModel.BuildLv(t, "OZ 01.10.010")[0].Children.Single(x => x.Label.Contains("Titel 01"));
            Check(filteredPicker.SetNodeChecked(filteredTopTitle, true) && filteredConfirmationCount == 2,
                "Filtering the LV caused title selection to omit hidden descendant positions");
            Check(CatalogTreeModel.GetPositionIds(nestedTitle).SequenceEqual(new[] { "nested" }), "Nested title descendant selection is incorrect");
            var bulk = new MappingEditorWindow(t, rule);
            Check(bulk.AddPositions(new[] { "pieces", "length", "nested", "length" }) == 2
                && bulk.BuildRule().Positions.Count == 3
                && bulk.BuildRule().Positions.Single(p => p.LvItemId == "pieces").Quantity?.Kind == QuantityKind.Constant,
                "Bulk position add duplicated existing selections or changed existing quantities");
            bulk.Close();
            var mainBulk = AvaTemplateWindow.AddSelectedPositions(rule,
                new[] { "pieces", "length", "nested", "length", "not-in-catalog" }, t.LvCatalog.Entries.Select(entry => entry.Id));
            Check(mainBulk.Positions.Count == 3 && rule.Positions.Count == 1
                && mainBulk.Positions.Single(p => p.LvItemId == "pieces").Quantity?.Kind == QuantityKind.Constant,
                "Main-window bulk add duplicated LV positions, mutated the source rule, or changed an existing quantity");
            var temp = Path.Combine(Path.GetTempPath(), "ava-group-ui-" + Guid.NewGuid() + ".json");
            try
            {
                TemplateJson.Save(temp, t); var main = new AvaTemplateWindow();
                Field<TemplateEditor>(main, "editor").Open(temp);
                main.GetType().GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null);
                Layout(main);
                var tree = (TreeView)main.FindName("MappingsTree"); var root = (TreeViewItem)tree.Items[0]; var type = (TreeViewItem)root.Items[0]; var row = (TreeViewItem)type.Items[0];
                row.IsExpanded = false; type.IsSelected = true; main.RefreshMappings(); Layout(main);
                root = (TreeViewItem)tree.Items[0]; type = (TreeViewItem)root.Items[0]; row = (TreeViewItem)type.Items[0];
                Check(!row.IsExpanded && row.Items.Count == 2, "collapsed state/children lost on refresh");
                Check((tree.SelectedItem as TreeViewItem)?.Tag is MappingTreeNode node && node.Key == ((MappingTreeNode)type.Tag).Key, "selection lost on refresh");
                var catalogKind = Field<ComboBox>(main, "catalogKind");
                var catalogTree = (TreeView)main.FindName("CatalogTree");
                catalogKind.SelectedIndex = 1; catalogKind.SelectedIndex = 0; Layout(main);
                Check(((TreeViewItem)catalogTree.Items[0]).Items.Count == 1, "BGK catalog is not displayed as a hierarchy");
                var bgkRoot = (TreeViewItem)catalogTree.Items[0]; var bgkType = (TreeViewItem)bgkRoot.Items[0];
                bgkType.IsExpanded = false; ((Button)main.FindName("CatalogExpandAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(bgkType.IsExpanded, "BGK expand-all did not expand types");
                ((Button)main.FindName("CatalogCollapseAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!bgkType.IsExpanded, "BGK collapse-all did not collapse types");
                catalogKind.SelectedIndex = 1; Layout(main);
                var lvRoot = (TreeViewItem)catalogTree.Items[0];
                Check(lvRoot.Header is TextBlock rootLabel && rootLabel.Text.StartsWith("LV ", StringComparison.Ordinal), "LV source tree root missing");
                Check(lvRoot.Items.Count >= 1, "LV category hierarchy not rendered");
                var category = lvRoot.Items.Cast<TreeViewItem>().Single(x => ((TextBlock)x.Header).Text.Contains("Titel 01"));
                Check(category.Header is TextBlock categoryLabel && categoryLabel.Text.Contains("Titel"), "LV title node not rendered");
                category.IsExpanded = false;
                var catalogSearch = Field<TextBox>(main, "catalogSearch");
                catalogSearch.Text = "OZ 02 — Leistung";
                catalogSearch.Text = "";
                category = ((TreeViewItem)catalogTree.Items[0]).Items.Cast<TreeViewItem>()
                    .Single(x => ((TextBlock)x.Header).Text.Contains("Titel 01"));
                Check(!category.IsExpanded, "Catalog title expansion state was lost after filtering and refreshing the tree");
                category.IsExpanded = false;
                ((Button)main.FindName("CatalogExpandAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(category.IsExpanded, "Catalog expand-all did not expand LV titles");
                ((Button)main.FindName("CatalogCollapseAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!category.IsExpanded, "Catalog collapse-all did not collapse LV titles");
                Check(main.FindName("CatalogExpandAll") is Button && main.FindName("CatalogCollapseAll") is Button,
                    "Source catalogs lack expand-all / collapse-all controls");
                Check(main.FindName("TemplateFilePath") is TextBlock pathLabel && pathLabel.Text.Contains(temp, StringComparison.OrdinalIgnoreCase),
                    "Current AVA template path is not shown below the toolbar");
                Check(main.FindName("BgkSourceDivider") is Separator && main.FindName("LvSourceDivider") is Separator,
                    "Source metadata sections are not separated by horizontal rules");
                if (args.Length > 1)
                {
                    row.IsExpanded = true;
                    bool renderCatalog = args[0] == "--render";
                    if (renderCatalog) ((Button)main.FindName("CatalogExpandAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Render(main, args[1], renderCatalog ? "catalog.png" : "grouped.png", renderCatalog ? 1 : 2);
                    if (renderCatalog)
                    {
                        var pickerWindow = new LvPositionPickerWindow(t);
                        RenderContent(pickerWindow, args[1], "lv-picker.png", 1000, 760);
                        pickerWindow.Close();
                    }
                }
                main.Close();
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            if (args.Length > 0 && args[0] != "--render")
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
    private static void RenderContent(Window w, string directory, string name, int width, int height)
    {
        Directory.CreateDirectory(directory);
        var content = (UIElement)w.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen()) { drawing.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, width, height)); drawing.DrawRectangle(new System.Windows.Media.VisualBrush(content), null, new Rect(0, 0, width, height)); }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name)); png.Save(file);
    }
    private static T Field<T>(object o, string name) => (T)(o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o) ?? throw new Exception("Missing UI field: " + name));
    private static TreeViewItem FindTreeItem(ItemsControl parent, string label)
    {
        TreeViewItem? Search(ItemsControl current)
        {
            foreach (TreeViewItem item in current.Items)
            {
                if (item.Tag is CatalogTreeNode node && node.Label == label) return item;
                var nested = Search(item);
                if (nested != null) return nested;
            }
            return null;
        }
        return Search(parent) ?? throw new InvalidOperationException("Tree item not found: " + label);
    }
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid formula accepted"); }
}
