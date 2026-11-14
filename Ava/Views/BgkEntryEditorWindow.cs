using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

/// <summary>Edits the descriptive fields of one BGK entry and manages its type-number list.</summary>
internal sealed class BgkEntryEditorWindow : Window
{
    private readonly W.TextBox key = EditorUi.Input();
    private readonly W.TextBox description = EditorUi.Input();
    private readonly W.TextBox category = EditorUi.Input();
    private readonly W.TextBox typeNumber = EditorUi.Input();
    private readonly W.TextBox typeDescription = EditorUi.Input();
    private readonly W.DataGrid types = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = W.DataGridSelectionMode.Single, MinHeight = 160 };
    private readonly W.Button saveButton;
    private readonly W.Button addTypeButton;
    private readonly W.Button updateTypeButton;
    private readonly W.Button removeTypeButton;
    private readonly ObservableCollection<BgkType> typeRows;
    private readonly bool editing;
    private BgkType? selectedType;
    internal BgkEntry? Result { get; private set; }
    internal Action<Exception>? ErrorReporter { get; set; }

    internal BgkEntryEditorWindow(BgkEntry? existing, string? selectedTypeNumber = null)
    {
        editing = existing != null;
        Title = editing ? "BGK-Eintrag bearbeiten" : "BGK-Eintrag hinzufügen";
        Width = 700; Height = 650; MinWidth = 560; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        key.Text = existing?.Key ?? ""; key.IsReadOnly = editing;
        description.Text = existing?.Description ?? ""; category.Text = existing?.Category ?? "";
        typeRows = new ObservableCollection<BgkType>(existing?.Types.Select(type => type with { }).ToList() ?? []);
        var root = new W.DockPanel { Margin = new Thickness(14) };
        var footer = new W.WrapPanel(); W.DockPanel.SetDock(footer, W.Dock.Bottom); root.Children.Add(footer);
        saveButton = EditorUi.Button("Übernehmen", () => Guard(Save)); footer.Children.Add(saveButton);
        footer.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false));
        var panel = new W.StackPanel(); root.Children.Add(EditorUi.Scroll(panel));
        panel.Children.Add(EditorUi.Text("BGK-Schlüssel und Typnummern sind Zuordnungsschlüssel. Bestehende Schlüssel bleiben hier unverändert; zum Entfernen verknüpfter Schlüssel zuerst die betroffenen Zuordnungen entfernen."));
        EditorUi.Field(panel, "BGK-Schlüssel", key); EditorUi.Field(panel, "Beschreibung", description); EditorUi.Field(panel, "Kategorie", category);
        panel.Children.Add(EditorUi.Text("Typnummern dieser BGK"));
        types.Columns.Add(new W.DataGridTextColumn { Header = "Typnummer", Binding = new System.Windows.Data.Binding("Number"), Width = 150 });
        types.Columns.Add(new W.DataGridTextColumn { Header = "Typbezeichnung", Binding = new System.Windows.Data.Binding("Description"), Width = new W.DataGridLength(1, W.DataGridLengthUnitType.Star) });
        types.ItemsSource = typeRows; types.SelectionChanged += (_, _) => LoadSelectedType();
        if (!string.IsNullOrEmpty(selectedTypeNumber)) types.SelectedItem = typeRows.FirstOrDefault(type => type.Number == selectedTypeNumber);
        panel.Children.Add(types);
        EditorUi.Field(panel, "Typnummer (bei vorhandenen Typen unveränderlicher Schlüssel)", typeNumber);
        EditorUi.Field(panel, "Typbezeichnung", typeDescription);
        var typeButtons = new W.WrapPanel(); panel.Children.Add(typeButtons);
        addTypeButton = EditorUi.Button("Typ hinzufügen", () => Guard(AddType)); typeButtons.Children.Add(addTypeButton);
        updateTypeButton = EditorUi.Button("Typbezeichnung übernehmen", () => Guard(UpdateType)); typeButtons.Children.Add(updateTypeButton);
        removeTypeButton = EditorUi.Button("Typ entfernen", () => Guard(RemoveType)); typeButtons.Children.Add(removeTypeButton);
        Content = root;
    }

    private void LoadSelectedType()
    {
        selectedType = types.SelectedItem as BgkType;
        typeNumber.Text = selectedType?.Number ?? "";
        typeNumber.IsReadOnly = selectedType != null;
        typeDescription.Text = selectedType?.Description ?? "";
    }

    private void AddType()
    {
        var number = typeNumber.Text;
        if (string.IsNullOrWhiteSpace(number)) throw new InvalidDataException("Eine Typnummer ist erforderlich.");
        if (typeRows.Any(type => type.Number == number)) throw new InvalidDataException("Diese Typnummer ist in der BGK bereits vorhanden.");
        var added = new BgkType(number, typeDescription.Text);
        typeRows.Add(added); types.SelectedItem = null; selectedType = null;
        typeNumber.Clear(); typeDescription.Clear(); typeNumber.IsReadOnly = false;
    }

    private void UpdateType()
    {
        if (selectedType == null) throw new InvalidDataException("Zuerst einen Typ auswählen.");
        var index = typeRows.IndexOf(selectedType);
        var updated = selectedType with { Description = typeDescription.Text };
        typeRows[index] = updated; types.SelectedItem = updated;
    }

    private void RemoveType()
    {
        if (selectedType == null) throw new InvalidDataException("Zuerst einen Typ auswählen.");
        typeRows.Remove(selectedType); selectedType = null; typeNumber.Clear(); typeDescription.Clear(); typeNumber.IsReadOnly = false;
    }

    private void Save()
    {
        if (selectedType != null) UpdateType();
        else if (!string.IsNullOrWhiteSpace(typeNumber.Text)) AddType();
        if (string.IsNullOrWhiteSpace(key.Text) || string.IsNullOrWhiteSpace(description.Text))
            throw new InvalidDataException("BGK-Schlüssel und Beschreibung sind erforderlich.");
        if (!editing && key.Text == "///")
            throw new InvalidDataException("Der BGK-Schlüssel '///' ist reserviert und wird im Katalogbaum nicht angezeigt.");
        Result = new BgkEntry(key.Text, description.Text, category.Text, typeRows.ToList());
        DialogResult = true;
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (EditorUi.IsInputError(ex))
        {
            if (ErrorReporter != null) ErrorReporter(ex); else EditorUi.Error(this, ex);
        }
    }
}
