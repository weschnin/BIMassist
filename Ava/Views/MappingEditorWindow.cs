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
    private readonly W.ListBox bgk = new();
    private readonly W.ListBox types = new();
    private readonly W.ListBox conditions = new();
    private readonly W.DataGrid positions = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = W.DataGridSelectionMode.Single };
    private readonly W.TextBox bgkSearch = EditorUi.Input();
    private readonly W.TextBox typeSearch = EditorUi.Input();
    public MappingRule? Result { get; private set; }

    public MappingEditorWindow(AvaTemplate template, MappingRule? existing)
    {
        this.template = template;
        draft = existing == null ? new() : TemplateJson.Copy(existing);
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
        void Column(int index, string label, W.TextBox search, W.ListBox list)
        {
            var panel = new W.DockPanel { Margin = new Thickness(0, 0, 12, 0) }; W.Grid.SetColumn(panel, index);
            var text = EditorUi.Text(label); W.DockPanel.SetDock(text, W.Dock.Top); panel.Children.Add(text);
            W.DockPanel.SetDock(search, W.Dock.Top); panel.Children.Add(search); panel.Children.Add(list); grid.Children.Add(panel);
        }
        Column(0, "BGK suchen / wählen", bgkSearch, bgk); Column(1, "Typ suchen / wählen", typeSearch, types);
        selector.Children.Add(grid); tabs.Items.Add(new W.TabItem { Header = "1  BGK / Typ", Content = selector });
        void FilterTypes()
        {
            var prior = types.SelectedItem;
            types.ItemsSource = (bgk.SelectedItem as BgkEntry)?.Types.Where(t => t.ToString().Contains(typeSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prior != null && types.Items.Contains(prior)) types.SelectedItem = prior;
        }
        bgk.SelectionChanged += (_, _) => FilterTypes(); typeSearch.TextChanged += (_, _) => FilterTypes();
        bgkSearch.TextChanged += (_, _) => {
            var priorBgk = bgk.SelectedItem; var priorType = types.SelectedItem;
            bgk.ItemsSource = template.BgkCatalog.Entries.Where(b => b.ToString().Contains(bgkSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (priorBgk != null && bgk.Items.Contains(priorBgk)) { bgk.SelectedItem = priorBgk; if (priorType != null && types.Items.Contains(priorType)) types.SelectedItem = priorType; }
        };
        bgk.ItemsSource = template.BgkCatalog.Entries;
        bgk.SelectedItem = template.BgkCatalog.Entries.FirstOrDefault(b => b.Key == draft.BgkKey);
        types.SelectedItem = (bgk.SelectedItem as BgkEntry)?.Types.FirstOrDefault(t => t.Number == draft.TypeNumber);

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
        positionButtons.Children.Add(EditorUi.Button("Leistungsposition hinzufügen…", () => EditPosition(false)));
        positionButtons.Children.Add(EditorUi.Button("Position / Menge bearbeiten…", () => EditPosition(true)));
        positionButtons.Children.Add(EditorUi.Button("Position entfernen", () => { if (positions.SelectedItem is PositionRow row) RemovePosition(row.Position.Id); }));
        positions.MouseDoubleClick += (_, _) => { if (positions.SelectedItem is PositionRow) EditPosition(true); };
        positions.Columns.Add(new W.DataGridTextColumn { Header = "OZ", Binding = new Binding("Ordinal"), Width = 130 });
        positions.Columns.Add(new W.DataGridTextColumn { Header = "Leistung", Binding = new Binding("Text"), Width = new W.DataGridLength(1, W.DataGridLengthUnitType.Star) });
        positions.Columns.Add(new W.DataGridTextColumn { Header = "Eigene Mengenvorschrift", Binding = new Binding("Quantity"), Width = 330 });
        positionPanel.Children.Add(positions); RefreshPositions();
        tabs.Items.Add(new W.TabItem { Header = "3  Leistungspositionen / Mengen", Content = positionPanel });
        Content = root;
    }
    internal void UpsertPosition(MappingPosition position)
    {
        int index = draft.Positions.FindIndex(p => p.Id == position.Id);
        if (index < 0) draft.Positions.Add(TemplateJson.Copy(position)); else draft.Positions[index] = TemplateJson.Copy(position);
        RefreshPositions();
    }
    internal void RemovePosition(Guid id) { draft.Positions.RemoveAll(p => p.Id == id); RefreshPositions(); }
    private void RefreshPositions() => positions.ItemsSource = draft.Positions.Select(p => {
        var lv = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == p.LvItemId);
        return new PositionRow(p, lv?.Ordinal ?? p.LvItemId, lv?.Text ?? "", p.Quantity?.ToString() ?? "Menge noch offen");
    }).ToList();
    internal MappingRule BuildRule()
    {
        if (bgk.SelectedItem is not BgkEntry b || types.SelectedItem is not BgkType t) throw new InvalidDataException("BGK und zugehörigen Typ auswählen.");
        if (draft.Positions.Count == 0) throw new InvalidDataException("Mindestens eine Leistungsposition hinzufügen.");
        var result = TemplateJson.Copy(draft); result.BgkKey = b.Key; result.TypeNumber = t.Number; return result;
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
