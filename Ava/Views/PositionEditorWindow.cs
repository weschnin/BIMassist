using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Binding = System.Windows.Data.Binding;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

/// <summary>LV selection and one independent quantity prescription. Preview values are never saved.</summary>
internal sealed class PositionEditorWindow : Window
{
    private readonly AvaTemplate template;
    private readonly MappingPosition draft;
    private readonly W.TreeView lvTree = new() { Margin = new Thickness(4) };
    private readonly W.TextBox lvSearch = EditorUi.Input();
    private readonly W.Button lvExpandAll;
    private readonly W.Button lvCollapseAll;
    private readonly W.TextBox lvDetail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 180,
        VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
    private readonly W.CheckBox hasQuantity = new() { Content = "Menge ausdrücklich definieren", Margin = new Thickness(0, 8, 0, 12) };
    private readonly W.CheckBox allowCustomUnit = new() { Content = "Freie Einheit manuell interpretieren", ToolTip = "Nur für NUMBER/INTEGER. Der Rohwert wird unverändert und ohne Umrechnung übernommen.", Margin = new Thickness(0, 0, 0, 8) };
    private readonly ParameterSourceEditor quantity;
    private readonly W.ComboBox quantityKind;
    private readonly W.TextBox constantValue;
    private readonly W.ComboBox quantityUnit = new() { MinHeight = 30, IsEditable = true, IsTextSearchEnabled = false };
    private readonly W.TextBox expression = EditorUi.Input();
    private readonly W.ListBox variables = new() { MinHeight = 75, MaxHeight = 140, DisplayMemberPath = "Name" };
    private readonly List<FormulaVariable> bindings;
    private readonly W.StackPanel formulaPanel = new();
    private readonly W.DataGrid testValues = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 70, MaxHeight = 160 };
    private readonly W.TextBlock preview = EditorUi.Text("");
    private List<PreviewRow> previewRows = [];
    private bool syncingLvTree;
    private LvEntry? selectedLv;
    private readonly Dictionary<string, bool> lvTreeExpansion = new(StringComparer.Ordinal);
    public MappingPosition? Result { get; private set; }

    public PositionEditorWindow(AvaTemplate template, MappingPosition? existing)
    {
        this.template = template; draft = existing == null ? new() : TemplateJson.Copy(existing);
        lvExpandAll = EditorUi.Button("Alles aufklappen", () => ExpandLv(true));
        lvCollapseAll = EditorUi.Button("Alles zuklappen", () => ExpandLv(false));
        bindings = TemplateJson.Copy(draft.Quantity?.Variables ?? new List<FormulaVariable>());
        Title = existing == null ? "Leistungsposition hinzufügen" : "Leistungsposition / Menge bearbeiten";
        Width = 1050; Height = 850; MinWidth = 850; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new W.DockPanel { Margin = new Thickness(16) };
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Bottom); root.Children.Add(buttons);
        buttons.Children.Add(EditorUi.Button("Position übernehmen", () => EditorUi.Guard(this, () => {
            var candidate = BuildPosition(); EnsurePositionValid(candidate); Result = candidate; DialogResult = true;
        })));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false));
        var tabs = new W.TabControl(); root.Children.Add(tabs);
        var lvPanel = new W.DockPanel { Margin = new Thickness(12) };
        var hint = EditorUi.Text("LV hierarchisch nach Titel und OZ auswählen. Titel dienen der Orientierung; nur eine einzelne OZ kann dieser Leistungsposition zugewiesen werden.");
        W.DockPanel.SetDock(hint, W.Dock.Top); lvPanel.Children.Add(hint);
        var lvToolbar = new W.WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        lvToolbar.Children.Add(lvExpandAll); lvToolbar.Children.Add(lvCollapseAll);
        lvSearch.ToolTip = "LV nach OZ oder Leistungstext filtern";
        lvToolbar.Children.Add(lvSearch);
        W.DockPanel.SetDock(lvToolbar, W.Dock.Top); lvPanel.Children.Add(lvToolbar);
        W.DockPanel.SetDock(lvDetail, W.Dock.Bottom); lvPanel.Children.Add(lvDetail); lvPanel.Children.Add(lvTree);
        lvSearch.Width = 380;
        bool preserveUnit = draft.Quantity != null;
        bool updatingUnits = false;
        string lastAssignedUnit = "";
        var quantityUnitLabel = EditorUi.Text("Einheit der Menge");
        allowCustomUnit.IsChecked = draft.Quantity?.AllowCustomUnit == true;
        lvTree.SelectedItemChanged += (_, _) => {
            if (syncingLvTree) return;
            syncingLvTree = true;
            try
            {
                selectedLv = lvTree.SelectedItem is W.TreeViewItem { Tag: CatalogTreeNode node }
                    && node.Key.StartsWith("item:", StringComparison.Ordinal)
                    ? template.LvCatalog.Entries.FirstOrDefault(item => item.Id == node.SourceId)
                    : null;
                lvDetail.Text = selectedLv is { } selected ? $"{selected.Ordinal} | {selected.Unit} | {selected.Id}\n{selected.Text}\n{selected.Description}" : "";
                preview.Text = "";
                RefreshQuantity();
            }
            finally { syncingLvTree = false; }
        };
        lvSearch.TextChanged += (_, _) => RefreshLvTree();
        selectedLv = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == draft.LvItemId);
        RefreshLvTree();
        Loaded += (_, _) => (lvTree.SelectedItem as W.TreeViewItem)?.Focus();
        lvDetail.Text = selectedLv is { } initial ? $"{initial.Ordinal} | {initial.Unit} | {initial.Id}\n{initial.Text}\n{initial.Description}" : "";
        tabs.Items.Add(new W.TabItem { Header = "1  Leistungsposition", Content = lvPanel });
        var panel = new W.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(EditorUi.Text("Die Menge gilt je passendem Element. Auswahl: feste Zahl, Parameterwert oder Kombination aus Zahlen und Parametern als Formel."));
        panel.Children.Add(hasQuantity); hasQuantity.IsChecked = draft.Quantity != null;
        quantityKind = EditorUi.Choice(new[] { (QuantityKind.Constant, "Feste Zahl je Element"), (QuantityKind.Parameter, "Parameterwert lesen"), (QuantityKind.Formula, "Formel aus Zahlen und Parametern") }, draft.Quantity?.Kind ?? QuantityKind.Constant);
        EditorUi.Field(panel, "Art der Mengenberechnung", quantityKind);
        constantValue = EditorUi.Input((draft.Quantity?.ConstantValue ?? 1m).ToString(CultureInfo.InvariantCulture));
        EditorUi.Field(panel, "Feste Zahl (positiv, für St ganzzahlig)", constantValue);
        quantity = new ParameterSourceEditor(template.ParameterCatalog.Entries, draft.Quantity?.Source ?? new()); panel.Children.Add(quantity);
        panel.Children.Add(quantityUnitLabel); panel.Children.Add(quantityUnit);
        panel.Children.Add(allowCustomUnit);
        panel.Children.Add(EditorUi.Text("Standardmäßig muss die Leseeinheit eines Parameters zu seiner Revit-Spezifikation passen. Für NUMBER/INTEGER kann eine freie Einheit ausdrücklich freigegeben werden: Der Zahlenwert wird dann unverändert und ohne Umrechnung mit dieser Einheit interpretiert. Nur verwenden, wenn dies fachlich stimmt. LV-Auswahl und Mengenvorschrift bleiben unabhängig; abweichende Einheiten lösen keine automatische Umrechnung aus."));
        expression.Text = draft.Quantity?.Expression ?? ""; expression.MinHeight = 60; expression.AcceptsReturn = true; expression.TextWrapping = TextWrapping.Wrap;
        EditorUi.Field(formulaPanel, "Formel: +, −, *, / und Klammern; z. B. 2 * AVA_Laenge + 0.5", expression);
        formulaPanel.Children.Add(EditorUi.Text("Jeder Parameter muss zuerst über 'Parameter hinzufügen…' einer Revit-Quelle und Eingabeeinheit zugeordnet werden. Einfache Namen ohne Leerzeichen dürfen direkt stehen (Volumen); Namen mit Leerzeichen müssen in [Klammern] stehen. 'Name in Formel einfügen' setzt den ausgewählten Namen ein. Keine automatische Umrechnung: Eingabe- und Ergebniseinheit müssen fachlich stimmen; eine Volumenmenge wird nicht allein durch Eingabe von 'Volumen' zu kg."));
        var variableButtons = new W.WrapPanel(); formulaPanel.Children.Add(variableButtons);
        void EditVariable(bool edit)
        {
            var selected = variables.SelectedItem as FormulaVariable; if (edit && selected == null) return;
            var dialog = new FormulaVariableWindow(template, edit ? selected : null) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } result)
            {
                if (bindings.Any(v => v.Name == result.Name && !ReferenceEquals(v, selected))) throw new InvalidDataException("Dieser Formelname ist bereits gebunden.");
                if (edit) bindings[bindings.IndexOf(selected!)] = result; else bindings.Add(result);
                RefreshBindings();
            }
        }
        variableButtons.Children.Add(EditorUi.Button("Parameter hinzufügen…", () => EditorUi.Guard(this, () => EditVariable(false))));
        variableButtons.Children.Add(EditorUi.Button("Bearbeiten…", () => EditorUi.Guard(this, () => EditVariable(true))));
        variableButtons.Children.Add(EditorUi.Button("Entfernen", () => { if (variables.SelectedItem is FormulaVariable item) { bindings.Remove(item); RefreshBindings(); } }));
        variableButtons.Children.Add(EditorUi.Button("Name in Formel einfügen", () => { if (variables.SelectedItem is FormulaVariable item) { expression.SelectedText = "[" + item.Name + "]"; expression.CaretIndex = expression.SelectionStart + expression.SelectionLength; expression.Focus(); } }));
        formulaPanel.Children.Add(variables);
        formulaPanel.Children.Add(EditorUi.Text("Optionale Testrechnung – Werte in den deklarierten Eingabeeinheiten eingeben. Diese Werte werden nicht gespeichert und stammen nicht aus Revit."));
        testValues.Columns.Add(new W.DataGridTextColumn { Header = "Formelname / Eingabeeinheit", Binding = new Binding("Label"), IsReadOnly = true, Width = 400, MinWidth = 240 });
        testValues.Columns.Add(new W.DataGridTextColumn { Header = "Testwert", Binding = new Binding("Value") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = 160 });
        formulaPanel.Children.Add(testValues);
        formulaPanel.Children.Add(EditorUi.Button("Mit Testwerten berechnen", () => EditorUi.Guard(this, () => {
            preview.Text = ""; testValues.CommitEdit(); testValues.CommitEdit();
            var inputs = previewRows.ToDictionary(r => r.Name, r => ParseNumber(r.Value), StringComparer.Ordinal);
            var value = EvaluatePreview(inputs); preview.Text = $"Testrechnung: {value.ToString(CultureInfo.GetCultureInfo("de-DE"))} {quantityUnit.Text} je Element – kein Modellwert";
        })));
        formulaPanel.Children.Add(preview); panel.Children.Add(formulaPanel); RefreshBindings();
        void RefreshQuantity()
        {
            bool enabled = hasQuantity.IsChecked == true;
            var kind = EditorUi.Selected<QuantityKind>(quantityKind);
            quantityKind.IsEnabled = enabled; constantValue.IsEnabled = enabled && kind == QuantityKind.Constant;
            quantity.IsEnabled = enabled && kind == QuantityKind.Parameter; quantity.Visibility = kind == QuantityKind.Parameter ? Visibility.Visible : Visibility.Collapsed;
            bool manualUnitEligible = false;
            if (kind == QuantityKind.Parameter)
            {
                try { manualUnitEligible = quantity.ReadSource().Spec is "INTEGER" or "NUMBER"; }
                catch (InvalidDataException) { }
            }
            allowCustomUnit.IsEnabled = enabled && kind == QuantityKind.Parameter && (manualUnitEligible || allowCustomUnit.IsChecked == true);
            allowCustomUnit.Visibility = kind == QuantityKind.Parameter ? Visibility.Visible : Visibility.Collapsed;
            allowCustomUnit.Content = manualUnitEligible ? "Freie Einheit manuell interpretieren"
                : allowCustomUnit.IsChecked == true ? "Nicht verfügbar: nur für NUMBER/INTEGER" : "Freie Einheit nur für NUMBER/INTEGER";
            allowCustomUnit.ToolTip = manualUnitEligible
                ? "Der Rohwert wird unverändert und ohne Umrechnung übernommen. Nur verwenden, wenn die Einheit fachlich stimmt."
                : "Diese Option setzt eine NUMBER- oder INTEGER-Quelle voraus. Bei bestehender ungültiger Auswahl deaktivieren Sie sie vor dem Speichern.";
            quantityUnitLabel.Text = kind == QuantityKind.Parameter
                ? allowCustomUnit.IsChecked == true ? "Manuell interpretierte Ausgabeeinheit (keine Umrechnung)" : "Parameter-Leseeinheit"
                : "Einheit der Menge / Formelergebnis";
            formulaPanel.Visibility = kind == QuantityKind.Formula ? Visibility.Visible : Visibility.Collapsed; formulaPanel.IsEnabled = enabled;
            quantityUnit.IsEnabled = enabled;
            quantityUnit.IsEditable = kind != QuantityKind.Parameter || allowCustomUnit.IsChecked == true;
            // Editable ComboBox.Text can change before its inner TextBox is realized or raises a routed event.
            if (quantityUnit.Text != lastAssignedUnit && !string.IsNullOrWhiteSpace(quantityUnit.Text)) preserveUnit = true;
            var prior = quantityUnit.Text.Length > 0 ? quantityUnit.Text : draft.Quantity?.Unit;
            // Only an untouched default follows LV selection; never relabel a saved or edited quantity.
            var selectedUnit = preserveUnit ? prior : selectedLv?.Unit ?? prior;
            updatingUnits = true;
            try
            {
                try { quantityUnit.ItemsSource = kind == QuantityKind.Parameter ? UnitCatalog.ForSpec(quantity.ReadSource().Spec)
                        .Concat(allowCustomUnit.IsChecked == true && !string.IsNullOrWhiteSpace(selectedUnit) ? new[] { selectedUnit } : [])
                        .Distinct(StringComparer.Ordinal).ToArray()
                    : new[] { "NUMBER", "LENGTH", "AREA", "VOLUME", "ANGLE", "MASS_DENSITY" }.SelectMany(UnitCatalog.ForSpec)
                        .Concat(new[] { selectedLv?.Unit ?? "", selectedUnit ?? "" }).Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToArray(); }
                catch (InvalidDataException) { quantityUnit.ItemsSource = Array.Empty<string>(); }
                quantityUnit.SelectedItem = selectedUnit;
                quantityUnit.Text = selectedUnit ?? "";
                lastAssignedUnit = quantityUnit.Text;
            }
            finally { updatingUnits = false; }
            preview.Text = "";
        }
        quantityKind.SelectionChanged += (_, _) => RefreshQuantity(); quantity.SourceChanged += RefreshQuantity;
        allowCustomUnit.Checked += (_, _) => RefreshQuantity(); allowCustomUnit.Unchecked += (_, _) => RefreshQuantity();
        hasQuantity.Checked += (_, _) => RefreshQuantity(); hasQuantity.Unchecked += (_, _) => RefreshQuantity();
        void QuantityEdited() { if (!string.IsNullOrWhiteSpace(quantityUnit.Text)) preserveUnit = true; preview.Text = ""; }
        expression.TextChanged += (_, _) => QuantityEdited(); constantValue.TextChanged += (_, _) => QuantityEdited();
        quantityUnit.SelectionChanged += (_, _) => { if (!updatingUnits) QuantityEdited(); };
        // Realizing the editable TextBox also raises TextChanged for the untouched assigned default.
        quantityUnit.AddHandler(W.Primitives.TextBoxBase.TextChangedEvent, new W.TextChangedEventHandler((_, _) => { if (!updatingUnits && quantityUnit.Text != lastAssignedUnit) QuantityEdited(); }));
        RefreshQuantity(); tabs.Items.Add(new W.TabItem { Header = "2  Eigene Menge / Formel", Content = EditorUi.Scroll(panel) }); Content = root;
    }
    private void RefreshBindings()
    {
        var prior = previewRows.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);
        variables.ItemsSource = null; variables.ItemsSource = bindings.Select(v => v).ToList();
        previewRows = bindings.Select(v => new PreviewRow(v.Name, v.Name + " [" + v.Unit + "]", prior.GetValueOrDefault(v.Name, ""), () => preview.Text = "")).ToList();
        testValues.ItemsSource = previewRows; preview.Text = "";
    }
    private void RefreshLvTree()
    {
        syncingLvTree = true;
        try
        {
            lvTree.Items.Clear();
            W.TreeViewItem? selectedItem = null;
            W.TreeViewItem Create(CatalogTreeNode node)
            {
                var item = new W.TreeViewItem
                {
                    Tag = node,
                    IsExpanded = lvTreeExpansion.GetValueOrDefault(node.Key, false),
                    Header = new W.TextBlock { Text = node.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 850,
                        Margin = new Thickness(3, 4, 3, 4) }
                };
                EditorUi.SelectTreeItemOnClick(item);
                item.Expanded += (_, _) => { if (!syncingLvTree) lvTreeExpansion[node.Key] = true; };
                item.Collapsed += (_, _) => { if (!syncingLvTree) lvTreeExpansion[node.Key] = false; };
                foreach (var child in node.Children) item.Items.Add(Create(child));
                if (node.Key.StartsWith("item:", StringComparison.Ordinal) && node.SourceId == selectedLv?.Id)
                    selectedItem = item;
                return item;
            }
            foreach (var node in CatalogTreeModel.BuildLv(template, lvSearch.Text)) lvTree.Items.Add(Create(node));
            if (selectedLv is { } assigned && selectedItem != null)
            {
                selectedItem = FindPosition(lvTree, assigned.Id) ?? selectedItem;
                selectedItem.IsSelected = true;
            }
        }
        finally { syncingLvTree = false; }
    }
    private void ExpandLv(bool expand)
    {
        void Apply(W.ItemsControl parent)
        {
            foreach (W.TreeViewItem item in parent.Items)
            {
                item.IsExpanded = expand;
                if (item.Tag is CatalogTreeNode node) lvTreeExpansion[node.Key] = expand;
                Apply(item);
            }
        }
        Apply(lvTree);
    }
    internal void SelectLvEntry(string id)
    {
        if (!string.IsNullOrWhiteSpace(lvSearch.Text)) lvSearch.Text = "";
        var target = FindPosition(lvTree, id);
        if (target == null) throw new InvalidDataException($"LV-Position {id} ist im Katalog nicht vorhanden.");
        target.IsSelected = true;
    }
    private static W.TreeViewItem? FindPosition(W.ItemsControl parent, string id)
    {
        foreach (W.TreeViewItem item in parent.Items)
        {
            if (item.Tag is CatalogTreeNode node && node.Key.StartsWith("item:", StringComparison.Ordinal) && node.SourceId == id)
                return item;
            var child = FindPosition(item, id);
            if (child != null) { item.IsExpanded = true; return child; }
        }
        return null;
    }
    internal void UpsertVariable(FormulaVariable variable)
    {
        int index = bindings.FindIndex(v => v.Name == variable.Name);
        if (index < 0) bindings.Add(TemplateJson.Copy(variable)); else bindings[index] = TemplateJson.Copy(variable);
        RefreshBindings();
    }
    internal MappingPosition BuildPosition()
    {
        if (selectedLv is not { } item) throw new InvalidDataException("Eine LV-Position auswählen.");
        var result = TemplateJson.Copy(draft); result.LvItemId = item.Id; result.Quantity = null;
        if (hasQuantity.IsChecked == true)
        {
            var kind = EditorUi.Selected<QuantityKind>(quantityKind); var unit = quantityUnit.Text;
            result.Quantity = kind switch {
                QuantityKind.Constant => new() { Kind = kind, Source = null, ConstantValue = ParseNumber(constantValue.Text), Unit = unit },
                QuantityKind.Parameter => new() { Kind = kind, Source = quantity.ReadSource(), Unit = unit, AllowCustomUnit = allowCustomUnit.IsChecked == true },
                QuantityKind.Formula => new() { Kind = kind, Source = null, Unit = unit, Expression = expression.Text, Variables = TemplateJson.Copy(bindings) },
                _ => throw new InvalidDataException("Unbekannte Mengenart.")
            };
        }
        return result;
    }
    private void EnsurePositionValid(MappingPosition position)
    {
        var bgk = template.BgkCatalog.Entries.FirstOrDefault(b => b.Types.Count > 0) ?? throw new InvalidDataException("BGK mit Typen fehlt.");
        var copy = TemplateJson.Copy(template); copy.Mappings = [new() { BgkKey = bgk.Key, TypeNumber = bgk.Types[0].Number, Positions = [position] }];
        TemplateEditor.EnsureValid(copy);
    }
    internal decimal EvaluatePreview(IReadOnlyDictionary<string, decimal> values)
    {
        var position = BuildPosition(); EnsurePositionValid(position);
        if (position.Quantity is not { Kind: QuantityKind.Formula } formula) throw new InvalidDataException("Für eine Testrechnung zuerst Formel wählen.");
        var value = QuantityFormula.Evaluate(formula.Expression, values);
        if (value < 0 || (formula.Unit == "St" && decimal.Truncate(value) != value)) throw new InvalidDataException("Formelergebnis ist negativ oder für Stückmengen nicht ganzzahlig.");
        return value;
    }
    internal static decimal ParseNumber(string text) => decimal.TryParse(text.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
        ? number : throw new InvalidDataException("Eine Zahl ohne Tausendertrennzeichen eingeben.");
    private sealed class PreviewRow(string name, string label, string value, Action changed)
    {
        public string Name { get; } = name;
        public string Label { get; } = label;
        private string currentValue = value;
        public string Value { get => currentValue; set { if (currentValue == value) return; currentValue = value; changed(); } }
    }
}

internal sealed class FormulaVariableWindow : Window
{
    public FormulaVariable? Result { get; private set; }
    public FormulaVariableWindow(AvaTemplate template, FormulaVariable? existing)
    {
        var draft = existing == null ? new FormulaVariable() : TemplateJson.Copy(existing);
        Title = "Formelparameter verknüpfen"; Width = 780; Height = 790; MinWidth = 650; MinHeight = 570; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new W.DockPanel { Margin = new Thickness(16) }; var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Bottom); root.Children.Add(buttons);
        var panel = new W.StackPanel(); root.Children.Add(EditorUi.Scroll(panel));
        var name = EditorUi.Input(draft.Name); EditorUi.Field(panel, "Name in der Formel (exakt; normalerweise der Parametername)", name);
        var source = new ParameterSourceEditor(template.ParameterCatalog.Entries, draft.Source); panel.Children.Add(source);
        panel.Children.Add(EditorUi.Button("Parametername übernehmen", () => EditorUi.Guard(this, () => name.Text = source.ReadSource().Name)));
        var unit = new W.ComboBox { MinHeight = 30 }; EditorUi.Field(panel, "Eingabeeinheit des Zahlenwerts", unit);
        void RefreshUnits()
        {
            var prior = unit.SelectedItem as string ?? draft.Unit;
            try { var value = source.ReadSource(); unit.ItemsSource = UnitCatalog.ForSpec(value.Spec); if (name.Text.Length == 0) name.Text = value.Name; }
            catch (InvalidDataException) { unit.ItemsSource = Array.Empty<string>(); }
            unit.SelectedItem = prior;
        }
        source.SourceChanged += RefreshUnits; RefreshUnits();
        buttons.Children.Add(EditorUi.Button("Übernehmen", () => EditorUi.Guard(this, () => {
            var parameter = source.ReadSource();
            var selected = unit.SelectedItem as string ?? "";
            if (!UnitCatalog.Allows(parameter.Spec, selected)) throw new InvalidDataException("Numerischen Parameter und passende Eingabeeinheit wählen.");
            var candidate = new FormulaVariable { Name = name.Text, Source = parameter, Unit = selected };
            QuantityFormula.Validate("[" + candidate.Name + "]", new[] { candidate.Name });
            Result = candidate; DialogResult = true;
        })));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false)); Content = root;
    }
}
