using System.Globalization;
using System.IO;
using System.Windows;
using Binding = System.Windows.Data.Binding;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

/// <summary>One BGK/type/condition group, containing independently quantified LV positions.</summary>
internal sealed class MappingEditorWindow : Window
{
    private readonly AvaTemplate template;
    private readonly MappingRule draft;
    private readonly W.TreeView bgk = new();
    private readonly W.Button bgkExpandAll;
    private readonly W.Button bgkCollapseAll;
    private readonly HashSet<string> expandedBgkKeys = new(StringComparer.Ordinal);
    private readonly W.ListBox conditions = new();
    private readonly W.DataGrid positions = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = W.DataGridSelectionMode.Single };
    private readonly W.TextBox bgkSearch = EditorUi.Input();
    private readonly W.TextBox typeSearch = EditorUi.Input();
    private string selectedBgkKey;
    private string selectedTypeNumber;
    public MappingRule? Result { get; private set; }

    public MappingEditorWindow(AvaTemplate template, MappingRule? existing)
    {
        this.template = template;
        draft = existing == null ? new() : TemplateJson.Copy(existing);
        selectedBgkKey = draft.BgkKey; selectedTypeNumber = draft.TypeNumber;
        foreach (var entry in template.BgkCatalog.Entries) expandedBgkKeys.Add("bgk:" + entry.Key);
        bgkExpandAll = EditorUi.Button("Alles aufklappen", () => ExpandBgk(true)); bgkExpandAll.Name = "BgkExpandAll";
        bgkCollapseAll = EditorUi.Button("Alles zuklappen", () => ExpandBgk(false)); bgkCollapseAll.Name = "BgkCollapseAll";
        Title = existing == null ? "AVA-Zuordnung hinzufügen" : "AVA-Zuordnung bearbeiten";
        Width = 1140; Height = 800; MinWidth = 880; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new W.DockPanel { Margin = new Thickness(16) };
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Bottom); root.Children.Add(buttons);
        buttons.Children.Add(EditorUi.Button("Zuordnung übernehmen", () => EditorUi.Guard(this, () => {
            var candidate = BuildRule(); var copy = TemplateJson.Copy(template);
            copy.Mappings.RemoveAll(m => m.Id == candidate.Id); copy.Mappings.Add(candidate); TemplateEditor.EnsureValid(copy);
            Result = candidate; DialogResult = true;
        })));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false));
        var tabs = new W.TabControl(); root.Children.Add(tabs);
        var selector = new W.DockPanel { Margin = new Thickness(12) };
        var hint = EditorUi.Text("Eine Zuordnung gilt für BGK + Typ und gemeinsame UND-Bedingungen. Darunter können beliebig viele Leistungspositionen mit jeweils eigener Menge stehen.");
        W.DockPanel.SetDock(hint, W.Dock.Top); selector.Children.Add(hint);
        var grid = new W.Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        void Column(int index, string label, W.TextBox search, UIElement list)
        {
            var panel = new W.DockPanel { Margin = new Thickness(0, 0, 12, 0) }; W.Grid.SetColumn(panel, index);
            var text = EditorUi.Text(label); W.DockPanel.SetDock(text, W.Dock.Top); panel.Children.Add(text);
            W.DockPanel.SetDock(search, W.Dock.Top); panel.Children.Add(search); panel.Children.Add(list); grid.Children.Add(panel);
        }
        var bgkColumn = new W.DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var bgkLabel = EditorUi.Text("BGK suchen / wählen"); W.DockPanel.SetDock(bgkLabel, W.Dock.Top); bgkColumn.Children.Add(bgkLabel);
        W.DockPanel.SetDock(bgkSearch, W.Dock.Top); bgkColumn.Children.Add(bgkSearch);
        var bgkButtons = new W.WrapPanel(); W.DockPanel.SetDock(bgkButtons, W.Dock.Top); bgkColumn.Children.Add(bgkButtons);
        bgkButtons.Children.Add(bgkExpandAll); bgkButtons.Children.Add(bgkCollapseAll);
        bgkColumn.Children.Add(bgk); W.Grid.SetColumn(bgkColumn, 0); grid.Children.Add(bgkColumn);
        Column(1, "Typ suchen / wählen", typeSearch, new W.TextBlock { Text = "Typen im BGK-Baum auswählen.", Margin = new Thickness(6) });
        selector.Children.Add(grid); tabs.Items.Add(new W.TabItem { Header = "1  BGK / Typ", Content = selector });
        bgkSearch.TextChanged += (_, _) => RefreshBgkTree(); typeSearch.TextChanged += (_, _) => RefreshBgkTree();
        bgk.SelectedItemChanged += (_, _) =>
        {
            if (bgk.SelectedItem is not W.TreeViewItem { Tag: CatalogTreeNode node }) return;
            if (node.Children.Count > 0) { selectedBgkKey = node.SourceId; selectedTypeNumber = ""; }
            else if (W.ItemsControl.ItemsControlFromItemContainer((W.TreeViewItem)bgk.SelectedItem) is W.TreeViewItem parent
                && parent.Tag is CatalogTreeNode parentNode)
            { selectedBgkKey = parentNode.SourceId; selectedTypeNumber = node.SourceId; }
        };
        RefreshBgkTree();
        var conditionPanel = new W.DockPanel { Margin = new Thickness(12) };
        var conditionHint = EditorUi.Text("Alle Bedingungen gelten gemeinsam für sämtliche Leistungspositionen dieser Zuordnung (UND). Fehlende Eingänge bleiben Fehler, nicht false. Keine Priorität oder Else-Regel zwischen Zuordnungen.");
        W.DockPanel.SetDock(conditionHint, W.Dock.Top); conditionPanel.Children.Add(conditionHint);
        var conditionButtons = new W.WrapPanel(); W.DockPanel.SetDock(conditionButtons, W.Dock.Top); conditionPanel.Children.Add(conditionButtons);
        void EditCondition(bool edit)
        {
            var selected = conditions.SelectedItem as RuleCondition; if (edit && selected == null) return;
            var dialog = new ConditionEditorWindow(template, edit ? selected : null) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } result)
            {
                if (edit) draft.Conditions[draft.Conditions.IndexOf(selected!)] = result; else draft.Conditions.Add(result);
                conditions.ItemsSource = null; conditions.ItemsSource = draft.Conditions;
            }
        }
        conditionButtons.Children.Add(EditorUi.Button("Bedingung hinzufügen", () => EditCondition(false)));
        conditionButtons.Children.Add(EditorUi.Button("Bearbeiten", () => EditCondition(true)));
        conditionButtons.Children.Add(EditorUi.Button("Entfernen", () => { if (conditions.SelectedItem is RuleCondition c) { draft.Conditions.Remove(c); conditions.ItemsSource = null; conditions.ItemsSource = draft.Conditions; } }));
        conditions.ItemsSource = draft.Conditions; conditionPanel.Children.Add(conditions);
        tabs.Items.Add(new W.TabItem { Header = "2  Bedingungen (UND)", Content = conditionPanel });

        var positionPanel = new W.DockPanel { Margin = new Thickness(12) };
        var positionHint = EditorUi.Text("Jede Zeile ist eine eigene Leistungsposition mit eigener Mengenquelle: feste Zahl, Parameter oder Formel. Änderungen werden erst mit 'Zuordnung übernehmen' gespeichert.");
        W.DockPanel.SetDock(positionHint, W.Dock.Top); positionPanel.Children.Add(positionHint);
        var positionButtons = new W.WrapPanel(); W.DockPanel.SetDock(positionButtons, W.Dock.Top); positionPanel.Children.Add(positionButtons);
        void EditPosition(bool edit)
        {
            var selected = positions.SelectedItem as PositionRow; if (edit && selected == null) return;
            var dialog = new PositionEditorWindow(template, edit ? selected!.Position : null) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } result) UpsertPosition(result);
        }
        positionButtons.Children.Add(EditorUi.Button("Leistungsposition(en) hinzufügen…", AddPositionsFromCatalog));
        positionButtons.Children.Add(EditorUi.Button("Position / Menge bearbeiten…", () => EditPosition(true)));
        positionButtons.Children.Add(EditorUi.Button("Position entfernen", () => { if (positions.SelectedItem is PositionRow row) RemovePosition(row.Position.Id); }));
        positions.MouseDoubleClick += (_, _) => { if (positions.SelectedItem is PositionRow) EditPosition(true); };
        positions.Columns.Add(new W.DataGridTextColumn { Header = "OZ", Binding = new Binding("Ordinal"), Width = 130 });
        positions.Columns.Add(new W.DataGridTextColumn { Header = "Leistung", Binding = new Binding("Text"), Width = new W.DataGridLength(1, W.DataGridLengthUnitType.Star) });
        positions.Columns.Add(new W.DataGridTextColumn { Header = "Eigene Mengenvorschrift", Binding = new Binding("Quantity"), Width = 330 });
        positionPanel.Children.Add(positions); RefreshPositions();
        if (existing?.Positions.Count > 0) positions.SelectedIndex = 0;
        tabs.Items.Add(new W.TabItem { Header = "3  Leistungspositionen / Mengen", Content = positionPanel });
        if (existing != null) tabs.SelectedIndex = 2;
        Content = root;
    }
    internal void UpsertPosition(MappingPosition position)
    {
        int index = draft.Positions.FindIndex(p => p.Id == position.Id);
        if (index < 0) draft.Positions.Add(TemplateJson.Copy(position)); else draft.Positions[index] = TemplateJson.Copy(position);
        RefreshPositions();
    }
    internal int AddPositions(IEnumerable<string> lvItemIds)
    {
        var knownIds = template.LvCatalog.Entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var existingIds = draft.Positions.Select(position => position.LvItemId).ToHashSet(StringComparer.Ordinal);
        int added = 0;
        foreach (var id in lvItemIds.Distinct(StringComparer.Ordinal))
        {
            if (!knownIds.Contains(id) || !existingIds.Add(id)) continue;
            draft.Positions.Add(new MappingPosition { LvItemId = id });
            added++;
        }
        if (added > 0) RefreshPositions();
        return added;
    }
    private void AddPositionsFromCatalog()
    {
        var dialog = new LvPositionPickerWindow(template) { Owner = this };
        if (dialog.ShowDialog() == true) AddPositions(dialog.SelectedPositionIds);
    }
    internal void RemovePosition(Guid id) { draft.Positions.RemoveAll(p => p.Id == id); RefreshPositions(); }
    private void RefreshPositions() => positions.ItemsSource = draft.Positions.Select(p => {
        var lv = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == p.LvItemId);
        return new PositionRow(p, lv?.Ordinal ?? p.LvItemId, lv?.Text ?? "", p.Quantity?.ToString() ?? "Menge noch offen");
    }).ToList();
    internal MappingRule BuildRule()
    {
        if (string.IsNullOrWhiteSpace(selectedBgkKey) || string.IsNullOrWhiteSpace(selectedTypeNumber))
            throw new InvalidDataException("Im BGK-Baum einen Typ unterhalb der Baugruppe auswählen.");
        var b = template.BgkCatalog.Entries.SingleOrDefault(entry => entry.Key == selectedBgkKey);
        var t = b?.Types.SingleOrDefault(type => type.Number == selectedTypeNumber);
        if (b == null || t == null) throw new InvalidDataException("Die gewählte BGK-/Typ-Kombination ist nicht mehr im Katalog enthalten.");
        if (draft.Positions.Count == 0) throw new InvalidDataException("Mindestens eine Leistungsposition hinzufügen.");
        var result = TemplateJson.Copy(draft); result.BgkKey = b.Key; result.TypeNumber = t.Number; return result;
    }
    private void RefreshBgkTree()
    {
        var nodes = CatalogTreeModel.BuildBgk(template, string.Join(" ", new[] { bgkSearch.Text, typeSearch.Text }.Where(x => !string.IsNullOrWhiteSpace(x))));
        bgk.Items.Clear();
        W.TreeViewItem Create(CatalogTreeNode node)
        {
            var item = new W.TreeViewItem { Tag = node, Header = new W.TextBlock { Text = node.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                IsExpanded = expandedBgkKeys.Contains(node.Key) };
            EditorUi.SelectTreeItemOnClick(item);
            item.Expanded += (_, _) => expandedBgkKeys.Add(node.Key);
            item.Collapsed += (_, _) => expandedBgkKeys.Remove(node.Key);
            foreach (var child in node.Children) item.Items.Add(Create(child));
            return item;
        }
        foreach (var node in nodes) bgk.Items.Add(Create(node));
        var selected = FindBgkNode(selectedBgkKey, selectedTypeNumber);
        if (selected != null) selected.IsSelected = true;
    }
    private W.TreeViewItem? FindBgkNode(string key, string typeNumber)
    {
        W.TreeViewItem? Search(W.ItemsControl parent)
        {
            foreach (W.TreeViewItem item in parent.Items)
            {
                if (item.Tag is CatalogTreeNode node && node.Key == "typ:" + key + ":" + typeNumber) return item;
                var found = Search(item);
                if (found != null) { item.IsExpanded = true; return found; }
            }
            return null;
        }
        return Search(bgk);
    }
    private void ExpandBgk(bool expand)
    {
        void Apply(W.ItemsControl parent)
        {
            foreach (W.TreeViewItem item in parent.Items)
            {
                item.IsExpanded = expand;
                if (expand) expandedBgkKeys.Add(((CatalogTreeNode)item.Tag).Key);
                else expandedBgkKeys.Remove(((CatalogTreeNode)item.Tag).Key);
                Apply(item);
            }
        }
        Apply(bgk);
    }
    private sealed record PositionRow(MappingPosition Position, string Ordinal, string Text, string Quantity);
}

internal sealed class ConditionEditorWindow : Window
{
    public RuleCondition? Result { get; private set; }
    public ConditionEditorWindow(AvaTemplate template, RuleCondition? existing)
    {
        var draft = existing == null ? new RuleCondition() : TemplateJson.Copy(existing);
        Title = "Bedingung bearbeiten – UND"; Width = 780; Height = 790; MinWidth = 620; MinHeight = 570; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new W.DockPanel { Margin = new Thickness(16) };
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Bottom); root.Children.Add(buttons);
        var panel = new W.StackPanel(); root.Children.Add(EditorUi.Scroll(panel));
        var source = new ParameterSourceEditor(template.ParameterCatalog.Entries, draft.Source); panel.Children.Add(source);
        var op = EditorUi.Choice(new[] { (ConditionOperator.Equal, "= gleich"), (ConditionOperator.NotEqual, "≠ ungleich"), (ConditionOperator.Greater, "> größer"), (ConditionOperator.GreaterOrEqual, "≥ größer oder gleich"), (ConditionOperator.Less, "< kleiner"), (ConditionOperator.LessOrEqual, "≤ kleiner oder gleich") }, draft.Operator);
        EditorUi.Field(panel, "Operator (Text / Ja-Nein: nur gleich oder ungleich)", op);
        var value = EditorUi.Input(draft.Operand == OperandKind.Numeric ? draft.Number.ToString(CultureInfo.InvariantCulture) : draft.Operand == OperandKind.Boolean ? (draft.Boolean ? "true" : "false") : draft.Text);
        EditorUi.Field(panel, "Vergleichswert: Zahl mit Punkt oder Komma; Ja-Nein: true / false; Text exakt", value);
        var unit = new W.ComboBox { MinHeight = 30 }; EditorUi.Field(panel, "Explizite Einheit (für Text / Ja-Nein leer)", unit);
        void RefreshUnits()
        {
            var selected = unit.SelectedItem as string ?? draft.Unit;
            try { unit.ItemsSource = UnitCatalog.ForSpec(source.ReadSource().Spec); } catch (InvalidDataException) { unit.ItemsSource = Array.Empty<string>(); }
            unit.SelectedItem = selected;
        }
        source.SourceChanged += RefreshUnits; RefreshUnits();
        buttons.Children.Add(EditorUi.Button("Übernehmen", () => EditorUi.Guard(this, () => {
            var s = source.ReadSource();
            var c = new RuleCondition { Source = s, Operator = EditorUi.Selected<ConditionOperator>(op), Unit = unit.SelectedItem as string ?? "" };
            if (s.Spec == "TEXT") { c.Operand = OperandKind.Text; c.Text = value.Text; }
            else if (s.Spec == "YESNO") { c.Operand = OperandKind.Boolean; if (!bool.TryParse(value.Text, out var boolean)) throw new InvalidDataException("Ja-Nein benötigt true oder false."); c.Boolean = boolean; }
            else
            {
                c.Operand = OperandKind.Numeric;
                if (!decimal.TryParse(value.Text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) throw new InvalidDataException("Eine Zahl ohne Tausendertrennzeichen eingeben.");
                c.Number = number;
            }
            var copy = TemplateJson.Copy(template); copy.Mappings = [new MappingRule { Conditions = [c] }];
            var errors = TemplateValidator.Validate(copy).Where(d => d.Severity == DiagnosticSeverity.Error && (d.Code.StartsWith("CONDITION", StringComparison.Ordinal) || d.Path.EndsWith(" / Bedingung", StringComparison.Ordinal))).ToList();
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            Result = c; DialogResult = true;
        })));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false)); Content = root;
    }
}
