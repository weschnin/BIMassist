using System.Globalization;
using System.IO;
using System.Windows;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

internal sealed class MappingEditorWindow : Window
{
    private readonly AvaTemplate template;
    private readonly MappingRule draft;
    private readonly W.ListBox bgk = new();
    private readonly W.ListBox types = new();
    private readonly W.ListBox lv = new();
    private readonly W.ListBox conditions = new();
    private readonly W.CheckBox hasQuantity = new() { Content = "Mengenquelle ausdrücklich definieren", Margin = new Thickness(0, 8, 0, 12) };
    private readonly ParameterSourceEditor quantity;
    private readonly W.ComboBox quantityUnit = new() { MinHeight = 30, Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
    private readonly W.TextBox bgkSearch = EditorUi.Input();
    private readonly W.TextBox typeSearch = EditorUi.Input();
    private readonly W.TextBox lvSearch = EditorUi.Input();
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
            var candidate = BuildRule(); var p = TemplateJson.Copy(template); p.Mappings.RemoveAll(m => m.Id == candidate.Id); p.Mappings.Add(candidate); TemplateEditor.EnsureValid(p);
            Result = candidate; DialogResult = true;
        })));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false));
        var tabs = new W.TabControl(); root.Children.Add(tabs);
        var mappingPanel = new W.DockPanel { Margin = new Thickness(12) };
        var hint = EditorUi.Text("BGK und Typnummer werden exakt zugeordnet. Ähnliche Nummern erzeugen keine automatische LV-Zuordnung. Mehrere Regeln sind möglich; keine Priorität oder Else-Regel.");
        W.DockPanel.SetDock(hint, W.Dock.Top); mappingPanel.Children.Add(hint);
        var details = new W.TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto, Height = 150, Margin = new Thickness(0, 12, 0, 0) };
        W.DockPanel.SetDock(details, W.Dock.Bottom); mappingPanel.Children.Add(details);
        var selectionGrid = new W.Grid();
        selectionGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        selectionGrid.ColumnDefinitions.Add(new() { Width = new GridLength(0.8, GridUnitType.Star) });
        selectionGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1.7, GridUnitType.Star) });
        void Column(int index, string label, W.TextBox search, W.ListBox list)
        {
            var column = new W.DockPanel { Margin = new Thickness(0, 0, 10, 0) }; W.Grid.SetColumn(column, index);
            var title = EditorUi.Text(label); W.DockPanel.SetDock(title, W.Dock.Top); column.Children.Add(title);
            W.DockPanel.SetDock(search, W.Dock.Top); search.ToolTip = "Suche – Groß-/Kleinschreibung wird ignoriert; Schlüssel bleiben unverändert."; column.Children.Add(search);
            W.ScrollViewer.SetHorizontalScrollBarVisibility(list, W.ScrollBarVisibility.Auto); column.Children.Add(list); selectionGrid.Children.Add(column);
        }
        Column(0, "1. BGK suchen / wählen", bgkSearch, bgk); Column(1, "2. Typ suchen / wählen", typeSearch, types); Column(2, "3. LV suchen / wählen (OZ, Text, ID)", lvSearch, lv);
        mappingPanel.Children.Add(selectionGrid); tabs.Items.Add(new W.TabItem { Header = "BGK / Typ → LV", Content = mappingPanel });
        bgkSearch.TextChanged += (_, _) => { var selected = bgk.SelectedItem; bgk.ItemsSource = template.BgkCatalog.Entries.Where(b => b.ToString().Contains(bgkSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList(); if (selected != null && bgk.Items.Contains(selected)) bgk.SelectedItem = selected; };
        void FilterTypes()
        {
            var selected = types.SelectedItem;
            types.ItemsSource = (bgk.SelectedItem as BgkEntry)?.Types.Where(t => t.ToString().Contains(typeSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (selected != null && types.Items.Contains(selected)) types.SelectedItem = selected;
        }
        bgk.SelectionChanged += (_, _) => FilterTypes(); typeSearch.TextChanged += (_, _) => FilterTypes();
        lvSearch.TextChanged += (_, _) => { var selected = lv.SelectedItem; lv.ItemsSource = template.LvCatalog.Entries.Where(l => (l.ToString() + " " + l.Id).Contains(lvSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList(); if (selected != null && lv.Items.Contains(selected)) lv.SelectedItem = selected; };
        lv.SelectionChanged += (_, _) => { if (lv.SelectedItem is LvEntry item) details.Text = $"{item.Ordinal} | ID {item.Id} | BoQ {item.BoqId} | Einheit {item.Unit}\n{item.Text}\n{item.Description}"; else details.Text = ""; };
        bgk.ItemsSource = template.BgkCatalog.Entries; lv.ItemsSource = template.LvCatalog.Entries;
        bgk.SelectedItem = template.BgkCatalog.Entries.FirstOrDefault(b => b.Key == draft.BgkKey);
        types.SelectedItem = (bgk.SelectedItem as BgkEntry)?.Types.FirstOrDefault(t => t.Number == draft.TypeNumber);
        lv.SelectedItem = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == draft.LvItemId);

        var conditionPanel = new W.DockPanel { Margin = new Thickness(12) };
        var conditionHint = EditorUi.Text("Alle Bedingungen gelten gemeinsam (UND). Ohne Bedingung gilt die Zuordnung für das gewählte BGK-/Typ-Paar. Fehlende Eingänge bedeuten später nicht auswertbar / Fehler, nicht false. Keine frei ausführbaren Ausdrücke.");
        W.DockPanel.SetDock(conditionHint, W.Dock.Top); conditionPanel.Children.Add(conditionHint);
        var conditionButtons = new W.WrapPanel(); W.DockPanel.SetDock(conditionButtons, W.Dock.Top); conditionPanel.Children.Add(conditionButtons);
        void EditCondition(bool edit)
        {
            var selected = conditions.SelectedItem as RuleCondition;
            if (edit && selected == null) return;
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
        tabs.Items.Add(new W.TabItem { Header = "Bedingungen (UND)", Content = conditionPanel });

        var quantityPanel = new W.StackPanel { Margin = new Thickness(12) };
        quantityPanel.Children.Add(EditorUi.Text("Mengenparameter wird nur für erfüllte Bedingungen benötigt. Hier werden Quelle und Einheit deklariert, keine Modellwerte gelesen. Umrechnung / Rundung / Aggregation folgen erst in einem späteren Modul."));
        quantityPanel.Children.Add(hasQuantity); hasQuantity.IsChecked = draft.Quantity != null;
        quantity = new ParameterSourceEditor(template.ParameterCatalog.Entries, draft.Quantity?.Source ?? new()); quantityPanel.Children.Add(quantity);
        EditorUi.Field(quantityPanel, "Einheit des gelesenen Werts (muss zur LV-Einheit passen)", quantityUnit);
        void RefreshQuantity()
        {
            quantity.IsEnabled = hasQuantity.IsChecked == true; quantityUnit.IsEnabled = quantity.IsEnabled;
            var prior = quantityUnit.SelectedItem as string ?? draft.Quantity?.Unit;
            try { quantityUnit.ItemsSource = UnitCatalog.ForSpec(quantity.ReadSource().Spec); }
            catch (InvalidDataException) { quantityUnit.ItemsSource = Array.Empty<string>(); }
            quantityUnit.SelectedItem = prior;
        }
        quantity.SourceChanged += RefreshQuantity; hasQuantity.Checked += (_, _) => RefreshQuantity(); hasQuantity.Unchecked += (_, _) => RefreshQuantity(); RefreshQuantity();
        quantityPanel.Children.Add(EditorUi.Text("Unterstützte Mengen: NUMBER / INTEGER, LENGTH, AREA, VOLUME, ANGLE, MASS_DENSITY mit expliziten Einheiten. Zeit, Masse und freie LV-Einheiten sind noch nicht unterstützt. 'Menge nicht definiert' bleibt als Entwurf zulässig."));
        tabs.Items.Add(new W.TabItem { Header = "Mengenquelle", Content = EditorUi.Scroll(quantityPanel) });
        Content = root;
    }
    internal MappingRule BuildRule()
    {
        if (bgk.SelectedItem is not BgkEntry b || types.SelectedItem is not BgkType t || lv.SelectedItem is not LvEntry l) throw new InvalidDataException("BGK, zugehörigen Typ und LV-Position auswählen.");
        var result = TemplateJson.Copy(draft); result.BgkKey = b.Key; result.TypeNumber = t.Number; result.LvItemId = l.Id;
        result.Quantity = hasQuantity.IsChecked == true ? new() { Source = quantity.ReadSource(), Unit = quantityUnit.SelectedItem as string ?? "" } : null;
        return result;
    }
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
            var p = TemplateJson.Copy(template); p.Mappings = [new MappingRule { Conditions = [c] }];
            var errors = TemplateValidator.Validate(p).Where(d => d.Severity == DiagnosticSeverity.Error && (d.Code.StartsWith("CONDITION", StringComparison.Ordinal) || d.Path.EndsWith(" / Bedingung", StringComparison.Ordinal))).ToList();
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            Result = c; DialogResult = true;
        })));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false));
        Content = root;
    }
}
