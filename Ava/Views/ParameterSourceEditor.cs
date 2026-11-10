using System.IO;
using System.Windows;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

internal static class EditorUi
{
    public static W.TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 7) };
    public static W.Button Button(string text, Action action)
    {
        var button = new W.Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 7, 7), MinHeight = 30 };
        button.Click += (_, _) => action(); return button;
    }
    public static W.TextBox Input(string text = "") => new() { Text = text, MinHeight = 29, Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 8) };
    public static void Field(W.Panel panel, string label, UIElement input) { panel.Children.Add(Text(label)); panel.Children.Add(input); }
    public static W.ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = W.ScrollBarVisibility.Disabled };
    public static W.ComboBox Choice<T>(IEnumerable<(T Value, string Label)> choices, T selected) where T : notnull
    {
        var combo = new W.ComboBox { MinHeight = 29, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (value, label) in choices)
        {
            var item = new W.ComboBoxItem { Content = label, Tag = value }; combo.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, selected)) combo.SelectedItem = item;
        }
        return combo;
    }
    public static T Selected<T>(W.ComboBox combo) => (T)((W.ComboBoxItem)combo.SelectedItem).Tag;
    public static void Error(Window owner, Exception ex) => System.Windows.MessageBox.Show(owner, ex.Message, "AVA – Eingabe / Datei prüfen", MessageBoxButton.OK, MessageBoxImage.Warning);
    internal static bool IsInputError(Exception error) => error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException;
    public static void Guard(Window owner, Action action) { try { action(); } catch (Exception ex) when (IsInputError(ex)) { Error(owner, ex); } }
}

/// <summary>Explicit GUID/BuiltIn identity or unique exact-name lookup on the selected scope.</summary>
internal sealed class ParameterSourceEditor : W.StackPanel
{
    private readonly W.ComboBox kind;
    private readonly W.ComboBox scope;
    private readonly W.ComboBox shared;
    private readonly W.ComboBox spec;
    private readonly W.ComboBox builtIn;
    private readonly W.TextBox name;
    private readonly W.TextBox search;
    private readonly W.StackPanel sharedPanel = new();
    private readonly W.StackPanel localPanel = new();
    private readonly W.StackPanel builtInPanel = new();
    private readonly W.TextBlock localHint;
    private readonly W.TextBlock nameHint;
    private readonly IReadOnlyList<SharedParameter> catalog;
    public event Action? SourceChanged;

    public ParameterSourceEditor(IReadOnlyList<SharedParameter> catalog, ParameterSource initial)
    {
        this.catalog = catalog;
        kind = EditorUi.Choice(new[] { (ParameterKind.SharedGuid, "Gemeinsamer Parameter – exakte GUID"), (ParameterKind.LocalFamily, "Lokaler Familienparameter – exakter Name + Spezifikation"), (ParameterKind.BuiltIn, "Revit BuiltIn – explizite Kennung"), (ParameterKind.ByName, "Parameter nach Name – eindeutig, Instanz/Typ + Spezifikation") }, initial.Kind);
        scope = EditorUi.Choice(new[] { (ParameterScope.Instance, "Instanz"), (ParameterScope.Type, "Typ") }, initial.Scope);
        EditorUi.Field(this, "Parameterquelle (eindeutiger Treffer erforderlich)", kind);
        search = EditorUi.Input(); EditorUi.Field(sharedPanel, "Parameterkatalog durchsuchen (Name, Spezifikation, GUID)", search);
        shared = new W.ComboBox { MinHeight = 30, MaxDropDownHeight = 260, ItemsSource = catalog, SelectedItem = catalog.FirstOrDefault(p => p.Guid == initial.SharedGuid), Margin = new Thickness(0, 0, 0, 8) };
        sharedPanel.Children.Add(shared);
        sharedPanel.Children.Add(EditorUi.Text("Der TXT-Katalog enthält Definitionen, keine Instanz-/Typ-Bindung. Geltung unten ausdrücklich wählen."));
        name = EditorUi.Input(initial.Name);
        spec = new W.ComboBox { ItemsSource = new[] { "TEXT", "YESNO", "NUMBER", "INTEGER", "LENGTH", "AREA", "VOLUME", "ANGLE", "MASS_DENSITY", "MATERIAL" }, SelectedItem = initial.Spec, MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
        EditorUi.Field(localPanel, "Exakter Parametername (unverändert)", name);
        EditorUi.Field(localPanel, "Explizite Spezifikation", spec);
        localHint = EditorUi.Text("Liest ausschließlich einen lokalen Familienparameter auf Instanz oder Typ. Gleichnamige gemeinsame und eingebaute Parameter werden nicht als Ersatz verwendet.");
        nameHint = EditorUi.Text("Liest einen einzigen Treffer mit diesem Namen und dieser Spezifikation aus der gewählten Instanz bzw. dem Typ – gemeinsam, lokal oder eingebaut. Bei mehreren passenden Treffern bricht die Mengenermittlung ab; dann eine exakte GUID bzw. BuiltIn-Kennung wählen. Eingebaute Namen hängen von der Revit-Sprache ab. Verfügbarkeit im Modell wird hier nicht geprüft.");
        localPanel.Children.Add(localHint);
        localPanel.Children.Add(nameHint);
        builtIn = new W.ComboBox { ItemsSource = new[] { "ASSEMBLY_CODE", "ALL_MODEL_TYPE_COMMENTS", "ALL_MODEL_DESCRIPTION" }, SelectedItem = initial.BuiltInId, MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
        EditorUi.Field(builtInPanel, "BuiltIn-Kennung (TEXT, Typ)", builtIn);
        Children.Add(sharedPanel); Children.Add(localPanel); Children.Add(builtInPanel);
        EditorUi.Field(this, "Geltung / Lesequelle", scope);
        kind.SelectionChanged += (_, _) => { RefreshVisibility(); SourceChanged?.Invoke(); };
        shared.SelectionChanged += (_, _) => SourceChanged?.Invoke();
        spec.SelectionChanged += (_, _) => SourceChanged?.Invoke();
        scope.SelectionChanged += (_, _) => SourceChanged?.Invoke();
        name.TextChanged += (_, _) => SourceChanged?.Invoke();
        builtIn.SelectionChanged += (_, _) => SourceChanged?.Invoke();
        search.TextChanged += (_, _) => {
            var selected = shared.SelectedItem;
            shared.ItemsSource = catalog.Where(p => p.ToString().Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (selected != null && shared.Items.Contains(selected)) shared.SelectedItem = selected;
        };
        RefreshVisibility();
    }
    private void RefreshVisibility()
    {
        var value = EditorUi.Selected<ParameterKind>(kind);
        sharedPanel.Visibility = value == ParameterKind.SharedGuid ? Visibility.Visible : Visibility.Collapsed;
        localPanel.Visibility = value is ParameterKind.LocalFamily or ParameterKind.ByName ? Visibility.Visible : Visibility.Collapsed;
        localHint.Visibility = value == ParameterKind.LocalFamily ? Visibility.Visible : Visibility.Collapsed;
        nameHint.Visibility = value == ParameterKind.ByName ? Visibility.Visible : Visibility.Collapsed;
        builtInPanel.Visibility = value == ParameterKind.BuiltIn ? Visibility.Visible : Visibility.Collapsed;
    }
    public ParameterSource ReadSource()
    {
        var value = EditorUi.Selected<ParameterKind>(kind);
        var selectedScope = EditorUi.Selected<ParameterScope>(scope);
        if (value == ParameterKind.SharedGuid)
        {
            if (shared.SelectedItem is not SharedParameter p) throw new InvalidDataException("Gemeinsamen Parameter aus dem Katalog auswählen.");
            return new() { Kind = value, SharedGuid = p.Guid, Name = p.Name, Spec = p.Spec, Scope = selectedScope };
        }
        if (value == ParameterKind.BuiltIn)
        {
            if (builtIn.SelectedItem is not string id) throw new InvalidDataException("BuiltIn-Kennung auswählen.");
            return new() { Kind = value, BuiltInId = id, Name = id == "ASSEMBLY_CODE" ? "Baugruppenkennzeichen" : id == "ALL_MODEL_TYPE_COMMENTS" ? "Typnummer" : "Beschreibung", Spec = "TEXT", Scope = selectedScope };
        }
        if (string.IsNullOrWhiteSpace(name.Text) || spec.SelectedItem is not string selectedSpec) throw new InvalidDataException("Exakten Parameternamen und Spezifikation angeben.");
        return new() { Kind = value, Name = name.Text, Spec = selectedSpec, Scope = selectedScope };
    }
}

internal sealed class ParameterSourceWindow : Window
{
    public ParameterSource? Result { get; private set; }
    public ParameterSourceWindow(AvaTemplate template, ParameterSource initial, Action<ParameterSource> validate)
    {
        Title = "Identifikationsquelle bearbeiten"; Width = 760; Height = 670; MinWidth = 620; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new W.DockPanel { Margin = new Thickness(18) };
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Bottom); panel.Children.Add(buttons);
        var source = new ParameterSourceEditor(template.ParameterCatalog.Entries, initial);
        buttons.Children.Add(EditorUi.Button("Übernehmen", () => EditorUi.Guard(this, () => { var result = source.ReadSource(); validate(result); Result = result; DialogResult = true; })));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false));
        panel.Children.Add(EditorUi.Scroll(source)); Content = panel;
    }
}
