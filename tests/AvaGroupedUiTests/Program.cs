using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
            var bgkDialog = new BgkEntryEditorWindow(new("44-40", "Flansche", "", [new("110", "Losflansch")])) ;
            var bgkDialogErrors = new List<Exception>(); bgkDialog.ErrorReporter = bgkDialogErrors.Add;
            var addType = Field<Button>(bgkDialog, "addTypeButton");
            addType.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Field<TextBox>(bgkDialog, "typeNumber").Text = "110"; addType.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Field<TextBox>(bgkDialog, "typeNumber").Text = "120"; addType.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Field<Button>(bgkDialog, "updateTypeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Field<Button>(bgkDialog, "removeTypeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(bgkDialogErrors.Count == 4 && Field<DataGrid>(bgkDialog, "types").Items.Count == 2,
                "BGK type editor did not guard blank/duplicate additions and missing update/remove selections");
            var saveTypeDialog = new BgkEntryEditorWindow(new("44-40", "Flansche", "", [new("110", "Alter Text")]));
            Field<DataGrid>(saveTypeDialog, "types").SelectedIndex = 0;
            Field<TextBox>(saveTypeDialog, "typeDescription").Text = "Direkt mit Speichern übernommen";
            try { typeof(BgkEntryEditorWindow).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(saveTypeDialog, null); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            Check(saveTypeDialog.Result?.Types.Single().Description == "Direkt mit Speichern übernommen",
                "BGK dialog Save dropped the selected type description when the separate apply button was not pressed");
            var sources = new ParameterSourceEditor(Array.Empty<SharedParameter>(), new ParameterSource
            {
                Kind = ParameterKind.LocalFamily, Name = "Durchmesser", Spec = "LENGTH", Scope = ParameterScope.Instance
            });
            var sourceKinds = Field<ComboBox>(sources, "kind").Items.Cast<ComboBoxItem>()
                .Select(item => (ParameterKind)item.Tag).ToArray();
            Check(sourceKinds.SequenceEqual([ParameterKind.SharedGuid, ParameterKind.ByName]),
                "parameter source selection should expose only shared GUID and universal exact-name lookup, not LocalFamily/BuiltIn modes");
            Check(Field<ComboBox>(sources, "kind").SelectedItem is ComboBoxItem { Tag: ParameterKind.ByName }
                && sources.ReadSource() is { Kind: ParameterKind.ByName, Name: "Durchmesser", Spec: "LENGTH", Scope: ParameterScope.Instance },
                "opening a legacy named source did not simplify it to universal name lookup");
            var preselectedTypeDialog = new BgkEntryEditorWindow(new("44-40", "Flansche", "", [new("110", "Losflansch")]), "110");
            Check(Field<DataGrid>(preselectedTypeDialog, "types").SelectedItem is BgkType { Number: "110" }
                && Field<TextBox>(preselectedTypeDialog, "typeDescription").Text == "Losflansch",
                "opening a type node did not preselect its type text for immediate editing");
            var reservedBgkDialog = new BgkEntryEditorWindow(null) { ErrorReporter = bgkDialogErrors.Add };
            Field<TextBox>(reservedBgkDialog, "key").Text = "///"; Field<TextBox>(reservedBgkDialog, "description").Text = "Reserved";
            Field<Button>(reservedBgkDialog, "saveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(bgkDialogErrors.Count == 5 && reservedBgkDialog.Result == null, "reserved invisible BGK key was accepted");
            t.LvCatalog.Entries.Add(new("nested", "b", ["01", "10", "010"], "01.10.010", "Ventil", "Detail", "St"));
            t.LvCatalog = CatalogWithTitleSnapshot(t.LvCatalog.Entries);
            var rule = new MappingRule { BgkKey = "44-40", TypeNumber = "110", Positions = [first] }; t.Mappings.Add(rule);
            var duplicateSource = new MappingRule { BgkKey = "44-40", TypeNumber = "110", Conditions = [new RuleCondition
            {
                Source = new() { Kind = ParameterKind.LocalFamily, Name = "AVA_DN", Spec = "LENGTH", Scope = ParameterScope.Type },
                Operator = ConditionOperator.Equal, Operand = OperandKind.Numeric, Number = 200, Unit = "mm"
            }], Positions = [first, second] };
            var duplicate = (MappingRule)typeof(AvaTemplateWindow).GetMethod("DuplicateMappingRule", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [duplicateSource])!;
            Check(duplicate.Id != duplicateSource.Id && duplicate.Positions.Count == 2
                && duplicate.Positions.Zip(duplicateSource.Positions).All(pair => pair.First.Id != pair.Second.Id)
                && duplicate.Conditions.Single().Number == 200
                && duplicate.Positions.Single(p => p.LvItemId == "length").Quantity?.Expression == "2 * AVA_Laenge + 0.5",
                "Duplicating a rule did not preserve its conditions/quantities with independent IDs");
            var group = new MappingEditorWindow(t, rule); group.UpsertPosition(second);
            Check(((TabControl)((DockPanel)group.Content).Children.OfType<TabControl>().Single()).SelectedIndex == 2
                && Field<DataGrid>(group, "positions").SelectedIndex == 0,
                "Opening an existing mapping must focus its assigned positions and select the first one");
            Check(group.BuildRule().Positions.Count == 2 && rule.Positions.Count == 1, "child add mutates original/loses sibling");
            var change = TemplateJson.Copy(second); change.Quantity!.Expression = "3 * AVA_Laenge"; group.UpsertPosition(change);
            Check(group.BuildRule().Positions.Count == 2 && group.BuildRule().Positions[1].Id == second.Id, "child edit duplicates identity");
            group.RemovePosition(second.Id); Check(group.BuildRule().Positions.Count == 1, "child removal lost sibling"); group.UpsertPosition(second);
            Field<TextBox>(group, "bgkSearch").Text = "44-40"; Check(group.BuildRule().TypeNumber == "110", "BGK filter clears surviving type");
            var assignmentTree = Field<TreeView>(group, "bgk");
            Check(assignmentTree.Items.Cast<TreeViewItem>().Any(root => ((CatalogTreeNode)root.Tag).Children.Count == 1), "Mapping dialog lacks hierarchical BGK/type selection");
            Field<Button>(group, "bgkExpandAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Field<TextBox>(group, "typeSearch").Text = "110";
            Check(((TreeViewItem)Field<TreeView>(group, "bgk").Items[0]).IsExpanded,
                "BGK expansion state was lost when the filtered tree refreshed");
            Field<TextBox>(group, "typeSearch").Text = "";
            Field<Button>(group, "bgkCollapseAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var newAssignment = new MappingEditorWindow(t, null);
            var assignmentRoot = (TreeViewItem)Field<TreeView>(newAssignment, "bgk").Items[0];
            ((TreeViewItem)assignmentRoot.Items[0]).IsSelected = true;
            newAssignment.AddPositions(["pieces"]);
            Check(newAssignment.BuildRule().BgkKey == "44-40" && newAssignment.BuildRule().TypeNumber == "110",
                "Selecting a type in the hierarchical BGK tree did not populate the new assignment");
            newAssignment.Close();
            t.Mappings = [group.BuildRule()]; group.Close(); TemplateEditor.EnsureValid(t);
            var position = new PositionEditorWindow(t, second); Layout(position);
            Check(Field<ListBox>(position, "variables").DisplayMemberPath == "Name", "formula list shows class names instead of parameter names");
            Check(Field<DataGrid>(position, "testValues").Columns[0].MinWidth >= 200, "formula input name column can collapse in scroll layout");
            Check(Field<StackPanel>(position, "formulaPanel").Children.OfType<TextBlock>().Any(text => text.Text.Contains("Parameter hinzufügen", StringComparison.Ordinal)
                && text.Text.Contains("keine automatische Umrechnung", StringComparison.OrdinalIgnoreCase)), "formula editor does not explain variable binding and unit semantics");
            Check(position.BuildPosition().Quantity!.Variables.Single().Source.Name == "AVA_Laenge", "formula binding lost");
            Check(position.EvaluatePreview(new Dictionary<string, decimal> { ["AVA_Laenge"] = 2 }) == 4.5m, "arithmetic incorrect");
            var manualUnitPosition = new PositionEditorWindow(t, new MappingPosition { LvItemId = "pieces", Quantity = new()
            {
                Source = new() { Kind = ParameterKind.LocalFamily, Name = "Days", Spec = "INTEGER", Scope = ParameterScope.Instance },
                Unit = "d", AllowCustomUnit = true
            } });
            Layout(manualUnitPosition);
            Check(Field<CheckBox>(manualUnitPosition, "allowCustomUnit").IsChecked == true
                && Field<CheckBox>(manualUnitPosition, "allowCustomUnit").IsEnabled
                && manualUnitPosition.BuildPosition().Quantity is { AllowCustomUnit: true, Unit: "d" },
                "position editor did not persist the explicit unconverted unit option");
            manualUnitPosition.Close();
            var dimensionalPosition = new PositionEditorWindow(t, new MappingPosition { LvItemId = "length", Quantity = new()
            {
                Source = new() { Kind = ParameterKind.LocalFamily, Name = "Length", Spec = "LENGTH", Scope = ParameterScope.Instance },
                Unit = "m"
            } });
            Layout(dimensionalPosition);
            Check(!Field<CheckBox>(dimensionalPosition, "allowCustomUnit").IsEnabled,
                "manual-unit configuration was enabled for a dimensional Revit parameter");
            dimensionalPosition.Close();
            var volumePosition = new PositionEditorWindow(t, new MappingPosition { LvItemId = "length", Quantity = new()
            {
                Source = new() { Kind = ParameterKind.LocalFamily, Name = "Volumen", Spec = "VOLUME", Scope = ParameterScope.Instance },
                Unit = "kg"
            } });
            Layout(volumePosition);
            Check(!Field<ComboBox>(volumePosition, "quantityUnit").IsEditable
                && !((IEnumerable<string>)Field<ComboBox>(volumePosition, "quantityUnit").ItemsSource!).Contains("kg"),
                "dimensioned parameter unit editor still permits a manually typed incompatible unit");
            volumePosition.Close();
            var hierarchicalPosition = new PositionEditorWindow(t, first); Layout(hierarchicalPosition);
            var positionLvTree = Field<TreeView>(hierarchicalPosition, "lvTree");
            Check(positionLvTree.Items.Cast<TreeViewItem>().Any(x => ((TextBlock)x.Header).Text.StartsWith("LV ", StringComparison.Ordinal)),
                "Position editor does not display the LV hierarchy");
            Check(hierarchicalPosition.BuildPosition().LvItemId == "pieces", "Hierarchical LV view lost the selected position");
            Check(Field<TextBox>(hierarchicalPosition, "lvDetail").Text.Contains("Flansch", StringComparison.Ordinal),
                "Position editor does not show details for the selected LV position");
            var positionLvRoot = (TreeViewItem)positionLvTree.Items[0];
            var positionLvTitle = positionLvRoot.Items.Cast<TreeViewItem>().First(x => ((TextBlock)x.Header).Text == "01 Baustelleneinrichtung");
            Check(positionLvRoot.IsExpanded && positionLvTitle.IsExpanded
                && (positionLvTree.SelectedItem as TreeViewItem)?.Tag is CatalogTreeNode selectedLvNode && selectedLvNode.SourceId == "pieces"
                && !positionLvRoot.Items.Cast<TreeViewItem>().Single(x => ((TextBlock)x.Header).Text == "02 Weitere Leistungen").IsExpanded,
                "Editing an assigned LV position must open its collapsed hierarchy path and select the assigned position");
            Field<Button>(hierarchicalPosition, "lvCollapseAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!positionLvTitle.IsExpanded, "Position editor LV collapse-all did not collapse titles");
            Field<Button>(hierarchicalPosition, "lvExpandAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(positionLvTitle.IsExpanded, "Position editor LV expand-all did not expand titles");
            var nestedPositionTitle = FindTreeItem(positionLvTree, "10 Unter titel"); nestedPositionTitle.IsExpanded = false;
            Field<TextBox>(hierarchicalPosition, "lvSearch").Text = "Leistung";
            Field<TextBox>(hierarchicalPosition, "lvSearch").Text = "";
            nestedPositionTitle = FindTreeItem(positionLvTree, "10 Unter titel");
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
            var hierarchyTemplate = new AvaTemplate();
            hierarchyTemplate.BgkCatalog.Entries.Add(new("01", "Baustelleneinrichtung", "", []));
            hierarchyTemplate.BgkCatalog.Entries.Add(new("01-10", "Einrichten", "", [new("100", "Grundposition")]));
            var hierarchy = CatalogTreeModel.BuildBgk(hierarchyTemplate);
            Check(hierarchy.Single().Label == "01 Baustelleneinrichtung"
                && hierarchy.Single().Children.Single().Label == "01-10 Einrichten",
                "BGK view omits the first source level or does not nest descendants under it");
            var mappingHierarchy = MappingTreeModel.Build(hierarchyTemplate);
            Check(mappingHierarchy.Single().Label == "01 Baustelleneinrichtung"
                && mappingHierarchy.Single().Children.Any(x => x.Label == "01-10 Einrichten"),
                "Mapping view omits the first BGK source level");
            var lvTree = CatalogTreeModel.BuildLv(t);
            Check(lvTree.Count == 1 && lvTree[0].Children.Any(x => x.Label == "01 Baustelleneinrichtung"), "LV source tree does not show the source title short text");
            var lvTitle = lvTree[0].Children.Single(x => x.Label == "01 Baustelleneinrichtung");
            Check(lvTitle.Children.Any(x => x.Label.StartsWith("01 ", StringComparison.Ordinal)), "LV leaves do not show the OZ number without a redundant prefix");
            var nestedTitle = lvTitle.Children.Single(x => x.Label == "10 Unter titel");
            Check(nestedTitle.Children.Single().Label.StartsWith("01.10.010 ", StringComparison.Ordinal), "Nested LV position does not show its OZ number without a redundant prefix");
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
            var interactiveTitle = FindTreeItem(interactiveTree, "01 Baustelleneinrichtung");
            var titleCheck = ((StackPanel)interactiveTitle.Header).Children.OfType<CheckBox>().Single();
            titleCheck.IsChecked = true; titleCheck.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(interactivePicker.SelectedPositionIds.Count == 2 && !interactiveRoot.IsExpanded,
                "Title checkbox did not select all descendants or reset the collapsed tree");
            int filteredConfirmationCount = 0;
            var filteredPicker = new LvPositionPickerWindow(t) { TitleSelectionPrompt = count => { filteredConfirmationCount = count; return true; } };
            var filteredTopTitle = CatalogTreeModel.BuildLv(t, "01.10.010")[0].Children.Single(x => x.Label == "01 Baustelleneinrichtung");
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
                var bgkEditPrompt = typeof(AvaTemplateWindow).GetProperty("BgkEditPrompt", BindingFlags.NonPublic | BindingFlags.Instance);
                Check(bgkEditPrompt != null, "BGK tree does not expose the direct edit interaction for its double-click handler");
                if (bgkEditPrompt != null)
                {
                    int editOpens = 0;
                    bgkEditPrompt.SetValue(main, (Func<BgkEntry, BgkEntry?>)(entry => { editOpens++; return entry; }));
                    var bgkTreeForDoubleClick = Field<TreeView>(main, "bgkCatalogTree");
                    var bgkItem = (TreeViewItem)bgkTreeForDoubleClick.Items[0]; bgkItem.IsSelected = true;
                    bgkTreeForDoubleClick.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
                    Check(editOpens == 1, "double-clicking a BGK catalog entry did not open its editor");
                }
                var editorTabs = (TabControl)main.FindName("EditorTabs");
                var headers = editorTabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString()).ToArray();
                Check(headers.Any(h => h!.Contains("BGK-Katalog", StringComparison.Ordinal))
                    && headers.Any(h => h!.Contains("LV-Katalog", StringComparison.Ordinal))
                    && headers.Any(h => h!.Contains("Parameter-Katalog", StringComparison.Ordinal))
                    && !headers.Any(h => h!.Contains("Quellkataloge", StringComparison.Ordinal)),
                    "BGK, LV and parameter catalogs must have separate top-level tabs");
                var tree = (TreeView)main.FindName("MappingsTree"); var root = (TreeViewItem)tree.Items[0]; var type = (TreeViewItem)root.Items[0]; var row = (TreeViewItem)type.Items[0];
                row.IsExpanded = false; type.IsSelected = true; main.RefreshMappings(); Layout(main);
                root = (TreeViewItem)tree.Items[0]; type = (TreeViewItem)root.Items[0]; row = (TreeViewItem)type.Items[0];
                Check(!row.IsExpanded && row.Items.Count == 2, "collapsed state/children lost on refresh");
                Check((tree.SelectedItem as TreeViewItem)?.Tag is MappingTreeNode node && node.Key == ((MappingTreeNode)type.Tag).Key, "selection lost on refresh");
                var mappedRule = Field<TemplateEditor>(main, "editor").Document.Mappings.Single();
                var updatedBgk = BgkCatalogEditor.Update(Field<TemplateEditor>(main, "editor").Document.BgkCatalog,
                    "44-40", new("44-40", "Flansche aktualisiert", "", [new("110", "Losflansch aktualisiert")]),
                    Field<TemplateEditor>(main, "editor").Document.Mappings);
                Field<TemplateEditor>(main, "editor").ReplaceBgk(updatedBgk);
                main.GetType().GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, null); Layout(main);
                root = (TreeViewItem)tree.Items[0]; type = (TreeViewItem)root.Items[0]; row = (TreeViewItem)type.Items[0];
                Check(((MappingTreeNode)root.Tag).Label == "44-40 Flansche aktualisiert"
                    && ((MappingTreeNode)type.Tag).Label == "Typ 110 — Losflansch aktualisiert"
                    && ((MappingTreeNode)row.Tag).Rule is { } refreshedRule
                    && refreshedRule.Id == mappedRule.Id && refreshedRule.BgkKey == mappedRule.BgkKey
                    && refreshedRule.TypeNumber == mappedRule.TypeNumber
                    && refreshedRule.Positions.Select(p => p.Id).SequenceEqual(mappedRule.Positions.Select(p => p.Id)),
                    "editing BGK descriptions did not refresh mapped labels while preserving the existing mapping and its positions");
                var catalogTree = (TreeView)main.FindName("CatalogTree");
                editorTabs.SelectedIndex = Array.FindIndex(headers, h => h!.Contains("BGK-Katalog", StringComparison.Ordinal)); Layout(main);
                var bgkActions = ((DockPanel)editorTabs.SelectedContent).Children.OfType<WrapPanel>().First().Children.OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
                Check(new[] { "BGK hinzufügen…", "BGK bearbeiten…", "BGK / Typ entfernen…", "Vorlage mit BGK-Katalog exportieren…" }.All(label => bgkActions.Contains(label)),
                    "BGK catalog CRUD and portable-template export actions are not available");
                Check(((TreeViewItem)catalogTree.Items[0]).Items.Count == 1, "BGK catalog is not displayed as a hierarchy");
                var bgkRoot = (TreeViewItem)catalogTree.Items[0]; var bgkType = (TreeViewItem)bgkRoot.Items[0];
                bgkType.IsExpanded = false; ((Button)main.FindName("BgkCatalogExpandAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(bgkType.IsExpanded, "BGK expand-all did not expand types");
                ((Button)main.FindName("BgkCatalogCollapseAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!bgkType.IsExpanded, "BGK collapse-all did not collapse types");
                editorTabs.SelectedIndex = Array.FindIndex(headers, h => h!.Contains("LV-Katalog", StringComparison.Ordinal)); Layout(main);
                                var mainLvTree = (TreeView)main.FindName("LvCatalogTree");
                                                var lvRoot = (TreeViewItem)mainLvTree.Items[0];
                Check(lvRoot.Header is TextBlock rootLabel && rootLabel.Text.StartsWith("LV ", StringComparison.Ordinal), "LV source tree root missing");
                Check(lvRoot.Items.Count >= 1, "LV category hierarchy not rendered");
                var category = lvRoot.Items.Cast<TreeViewItem>().Single(x => ((TextBlock)x.Header).Text == "01 Baustelleneinrichtung");
                Check(category.Header is TextBlock categoryLabel && categoryLabel.Text == "01 Baustelleneinrichtung", "LV title source short text not rendered");
                category.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                Check(ReferenceEquals(mainLvTree.SelectedItem, category), "Clicking a catalog row did not select it before expansion");
                category.IsExpanded = false;
                var catalogSearch = Field<TextBox>(main, "lvCatalogSearch");
                catalogSearch.Text = "02 Leistung";
                catalogSearch.Text = "";
                category = ((TreeViewItem)mainLvTree.Items[0]).Items.Cast<TreeViewItem>()
                    .Single(x => ((TextBlock)x.Header).Text == "01 Baustelleneinrichtung");
                Check(!category.IsExpanded, "Catalog title expansion state was lost after filtering and refreshing the tree");
                category.IsExpanded = false;
                ((Button)main.FindName("LvCatalogExpandAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(category.IsExpanded, "Catalog expand-all did not expand LV titles");
                ((Button)main.FindName("LvCatalogCollapseAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!category.IsExpanded, "Catalog collapse-all did not collapse LV titles");
                Check(main.FindName("BgkCatalogExpandAll") is Button && main.FindName("BgkCatalogCollapseAll") is Button
                    && main.FindName("LvCatalogExpandAll") is Button && main.FindName("LvCatalogCollapseAll") is Button,
                    "BGK and LV catalogs lack independent expand-all / collapse-all controls");
                Check(main.FindName("TemplateFilePath") is TextBlock pathLabel && pathLabel.Text.Contains(temp, StringComparison.OrdinalIgnoreCase),
                    "Current AVA template path is not shown below the toolbar");
                editorTabs.SelectedIndex = Array.FindIndex(headers, h => h!.Contains("Parameter-Katalog", StringComparison.Ordinal)); Layout(main);
                Check(main.FindName("ParameterCatalogGrid") is DataGrid, "Parameter catalog is not an independent tab with its own grid");
                if (args.Length > 1)
                {
                    row.IsExpanded = true;
                    bool renderCatalog = args[0] == "--render";
                    if (renderCatalog) ((Button)main.FindName("BgkCatalogExpandAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
            TestValidationNavigation();
            TestBgkSubtreeRemoval();
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
    private static void TestValidationNavigation()
    {
        var template = new AvaTemplate { ReviewScope = "Flansche" };
        template.BgkCatalog.Entries.Add(new("44-40", "Flansche", "", [new("110", "Losflansch")]));
        template.LvCatalog.Entries.Add(new("unquantified", "b", ["01"], "01", "Flansch", "", "St"));
        var rule = new MappingRule { BgkKey = "44-40", TypeNumber = "110", Positions = [new MappingPosition { LvItemId = "unquantified" }] };
        template.Mappings.Add(rule);
        template.Mappings.Add(new MappingRule { BgkKey = "44-40", TypeNumber = "110", Positions =
            [new MappingPosition { LvItemId = "unquantified", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" } }] });
        TemplateEditor.EnsureValid(template);
        var path = Path.Combine(Path.GetTempPath(), "ava-validation-ui-" + Guid.NewGuid() + ".json");
        try
        {
            TemplateJson.Save(path, template);
            var window = new AvaTemplateWindow();
            Field<TemplateEditor>(window, "editor").Open(path);
            window.GetType().GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Layout(window);
            var diagnostics = Field<DataGrid>(window, "diagnostics");
            var warning = diagnostics.ItemsSource.Cast<Diagnostic>().Single(d => d.Code == "QUANTITY_MISSING");
            MappingRule? opened = null;
            var editorHook = typeof(AvaTemplateWindow).GetProperty("DiagnosticMappingEditor", BindingFlags.NonPublic | BindingFlags.Instance)!;
            editorHook.SetValue(window, (Func<MappingRule, MappingRule?>)(selected => { opened = selected; return TemplateJson.Copy(selected); }));
            void DoubleClick(Diagnostic selected)
            {
                diagnostics.SelectedItem = selected;
                diagnostics.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Control.MouseDoubleClickEvent });
            }
            DoubleClick(warning);
            Check(opened?.Id == rule.Id && diagnostics.ItemsSource.Cast<Diagnostic>().Any(d => d.Code == "QUANTITY_MISSING"),
                "Validation warning double-click did not open its exact mapping or retain an unresolved warning");
            editorHook.SetValue(window, (Func<MappingRule, MappingRule?>)(selected =>
            {
                var corrected = TemplateJson.Copy(selected);
                corrected.Positions[0].Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" };
                return corrected;
            }));
            DoubleClick(diagnostics.ItemsSource.Cast<Diagnostic>().Single(d => d.Code == "QUANTITY_MISSING"));
            Check(!diagnostics.ItemsSource.Cast<Diagnostic>().Any(d => d.Code == "QUANTITY_MISSING"),
                "Validation warning remained after its mapping was corrected and accepted");
            editorHook.SetValue(window, (Func<MappingRule, MappingRule?>)(selected => { opened = selected; return TemplateJson.Copy(selected); }));
            opened = null;
            DoubleClick(diagnostics.ItemsSource.Cast<Diagnostic>().Single(d => d.Code == "MULTIPLE_RULES"));
            Check(opened != null && opened.BgkKey == rule.BgkKey && opened.TypeNumber == rule.TypeNumber,
                "Group-level duplicate-rule warning did not open one exact affected mapping");
            window.Close();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void TestBgkSubtreeRemoval()
    {
        var template = new AvaTemplate();
        template.BgkCatalog.Entries.Add(new("44", "Rohrleitungsbau", "", []));
        template.BgkCatalog.Entries.Add(new("44-20", "Rohranschlüsse", "", [new("100", "Standard") ]));
        template.BgkCatalog.Entries.Add(new("44-30", "Sonderanschlüsse", "", [new("110", "Sonder") ]));
        template.LvCatalog.Entries.Add(new("delete-lv", "boq", ["01"], "01", "Leistung", "", "St"));
        foreach (var (key, number) in new[] { ("44-20", "100"), ("44-30", "110") })
            template.Mappings.Add(new MappingRule { BgkKey = key, TypeNumber = number,
                Conditions = key == "44-30" ? [new RuleCondition { Source = new ParameterSource
                { Kind = ParameterKind.BuiltIn, Name = "Baugruppenkennzeichen", Spec = "TEXT", Scope = ParameterScope.Type, BuiltInId = "ASSEMBLY_CODE" },
                    Operator = ConditionOperator.Equal, Operand = OperandKind.Text, Text = "x" }] : [],
                Positions = [new MappingPosition { LvItemId = "delete-lv", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" } }] });
        TemplateEditor.EnsureValid(template);
        var path = Path.Combine(Path.GetTempPath(), "ava-delete-ui-" + Guid.NewGuid() + ".json");
        try
        {
            TemplateJson.Save(path, template);
            var window = new AvaTemplateWindow();
            Field<TemplateEditor>(window, "editor").Open(path);
            window.RefreshMappings(); Layout(window);
            var tree = Field<TreeView>(window, "mappings");
            var lastRule = template.Mappings.Single(rule => rule.BgkKey == "44-30");
            var lastPosition = lastRule.Positions.Single();
            var positionNode = FindMappingTreeItem(tree, $"position:{lastRule.Id}:{lastPosition.Id}"); positionNode.IsSelected = true;
            string positionPrompt = "";
            var removalPrompt = typeof(AvaTemplateWindow).GetProperty("RemovalPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!;
            removalPrompt.SetValue(window, (Func<string, bool>)(text => { positionPrompt = text; return false; }));
            typeof(AvaTemplateWindow).GetMethod("RemoveSelectedMapping", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(positionPrompt.Contains("gesamte Zuordnung", StringComparison.Ordinal) && positionPrompt.Contains("1 Bedingung", StringComparison.Ordinal)
                && Field<TemplateEditor>(window, "editor").Document.Mappings.Single(rule => rule.Id == lastRule.Id).Positions.Count == 1,
                "Deleting a rule's last position did not disclose that its conditions/rule would also be deleted or honor cancellation");
            Check(positionPrompt.Contains("Mengenangabe", StringComparison.Ordinal),
                "Deleting the last position did not identify its associated quantity data");
            var root = FindMappingTreeItem(tree, "bgk:44"); root.IsSelected = true;
            string prompt = "";
            removalPrompt.SetValue(window, (Func<string, bool>)(text => { prompt = text; return false; }));
            typeof(AvaTemplateWindow).GetMethod("RemoveSelectedMapping", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(Field<TemplateEditor>(window, "editor").Document.Mappings.Count == 2
                && prompt.Contains("2 Zuordnungen", StringComparison.Ordinal) && prompt.Contains("2 Leistungspositionen", StringComparison.Ordinal),
                "BGK-subtree deletion did not ask a counted confirmation or respect cancellation");
            positionNode = FindMappingTreeItem(tree, $"position:{lastRule.Id}:{lastPosition.Id}"); positionNode.IsSelected = true;
            string confirmedPrompt = "";
            removalPrompt.SetValue(window, (Func<string, bool>)(text => { confirmedPrompt = text; return true; }));
            typeof(AvaTemplateWindow).GetMethod("RemoveSelectedMapping", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(confirmedPrompt.Contains("gesamte Zuordnung", StringComparison.Ordinal)
                && confirmedPrompt.Contains("Mengenangabe", StringComparison.Ordinal)
                && confirmedPrompt.Contains("1 Bedingung", StringComparison.Ordinal)
                && Field<TemplateEditor>(window, "editor").Document.Mappings.Count == 1
                && Field<TemplateEditor>(window, "editor").Document.Mappings.Single().BgkKey == "44-20",
                "Confirming last-position deletion did not remove exactly its rule, conditions and quantity mapping");
            root = FindMappingTreeItem(tree, "bgk:44"); root.IsSelected = true;
            removalPrompt.SetValue(window, (Func<string, bool>)(_ => true));
            typeof(AvaTemplateWindow).GetMethod("RemoveSelectedMapping", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Check(Field<TemplateEditor>(window, "editor").Document.Mappings.Count == 0,
                "Confirmed parent-BGK deletion did not remove every descendant mapping");
            window.Close();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private static TreeViewItem FindMappingTreeItem(ItemsControl parent, string key)
    {
        foreach (TreeViewItem item in parent.Items)
        {
            if (item.Tag is MappingTreeNode node && node.Key == key) return item;
            var nested = TryFind(item, key); if (nested != null) return nested;
        }
        throw new InvalidOperationException("Mapping tree item not found: " + key);
        static TreeViewItem? TryFind(ItemsControl current, string target)
        {
            foreach (TreeViewItem item in current.Items)
            {
                if (item.Tag is MappingTreeNode node && node.Key == target) return item;
                var nested = TryFind(item, target); if (nested != null) return nested;
            }
            return null;
        }
    }
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
    private static Catalog<LvEntry> CatalogWithTitleSnapshot(List<LvEntry> entries)
    {
        const string xml = "<GAEB xmlns=\"http://www.gaeb.de/GAEB_DA_XML/DA82/3.3\"><BoQ ID=\"b\"><BoQInfo><Name>Test-LV</Name></BoQInfo><BoQBody><BoQCtgy ID=\"title01\" RNoPart=\"01\"><LblTx><p>Baustelleneinrichtung</p></LblTx><BoQBody><Itemlist><Item ID=\"pieces\" RNoPart=\"01\"/></Itemlist><BoQCtgy ID=\"title10\" RNoPart=\"10\"><LblTx><div>Unter</div><br/><div>titel</div></LblTx><BoQBody><Itemlist><Item ID=\"nested\" RNoPart=\"010\"/></Itemlist></BoQBody></BoQCtgy></BoQBody></BoQCtgy><BoQCtgy ID=\"title02\" RNoPart=\"02\"><LblTx><p>Weitere Leistungen</p></LblTx><BoQBody><Itemlist><Item ID=\"length\" RNoPart=\"02\"/></Itemlist></BoQBody></BoQCtgy></BoQBody></BoQ></GAEB>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        return new(new("test.x82", "GAEB DA82/3.3", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), Convert.ToBase64String(bytes)), entries);
    }
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
