using System.IO;
using System.Windows;
using Binding = System.Windows.Data.Binding;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

/// <summary>Modal, file-only AVA editor. No Revit API objects, model access, binding or transactions.</summary>
public sealed class AvaTemplateWindow : Window
{
    private readonly TemplateEditor editor = new();
    private readonly W.TextBox profileName = EditorUi.Input();
    private readonly W.TextBox profileVersion = EditorUi.Input();
    private readonly W.TextBox reviewScope = EditorUi.Input();
    private readonly W.TextBlock identity = EditorUi.Text("");
    private readonly W.TextBlock bgkSource = EditorUi.Text("");
    private readonly W.TextBlock typeSource = EditorUi.Text("");
    private readonly W.TextBlock sources = EditorUi.Text("");
    private readonly W.TextBlock status = EditorUi.Text("");
    private readonly W.DataGrid mappings = Table();
    private readonly W.DataGrid checklist = Table();
    private readonly W.DataGrid diagnostics = Table();
    private readonly W.DataGrid catalog = Table();
    private readonly W.ComboBox catalogKind = new() { ItemsSource = new[] { "BGK und Typen", "LV-Positionen", "Gemeinsame Parameter" }, SelectedIndex = 0, Width = 240, MinHeight = 30, Margin = new Thickness(0, 0, 10, 8) };
    private readonly W.TextBox catalogSearch = EditorUi.Input();
    private bool refreshing;

    public AvaTemplateWindow()
    {
        NameScope.SetNameScope(this, new NameScope());
        Title = "AVA-Vorlage / Prüfliste"; Width = 1200; Height = 820; MinWidth = 900; MinHeight = 620;
        ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 13;
        var root = new W.DockPanel { Margin = new Thickness(16) };
        var toolbar = new W.WrapPanel(); W.DockPanel.SetDock(toolbar, W.Dock.Top); root.Children.Add(toolbar);
        toolbar.Children.Add(EditorUi.Button("Neu", () => { if (ConfirmPending()) { editor.New(); Refresh(); } }));
        toolbar.Children.Add(EditorUi.Button("Öffnen…", () => Open(false)));
        toolbar.Children.Add(EditorUi.Button("Speichern", () => Save(false)));
        toolbar.Children.Add(EditorUi.Button("Speichern unter…", () => Save(true)));
        toolbar.Children.Add(EditorUi.Button("JSON importieren…", () => Open(true)));
        toolbar.Children.Add(EditorUi.Button("JSON exportieren…", Export));
        var notice = EditorUi.Text("Dateibasierter Vorlageneditor · keine Modellprüfung, keine Revit-Schreibzugriffe, kein X31. Die Prüfliste wird aus der Vorlage abgeleitet; sie ist kein bereits ausgeführter Modellcheck.");
        W.DockPanel.SetDock(notice, W.Dock.Top); root.Children.Add(notice);
        W.DockPanel.SetDock(status, W.Dock.Bottom); root.Children.Add(status);
        var tabs = new W.TabControl { Name = "EditorTabs" }; RegisterName(tabs.Name, tabs); root.Children.Add(tabs);
        tabs.Items.Add(new W.TabItem { Header = "1  Profil / Prüfumfang", Content = ProfileTab() });
        tabs.Items.Add(new W.TabItem { Header = "2  Quellkataloge", Content = CatalogTab() });
        tabs.Items.Add(new W.TabItem { Header = "3  Zuordnungen", Content = MappingTab() });
        AddColumn(checklist, "Art", "Kind", 130); AddColumn(checklist, "Gilt für", "AppliesTo", 290); AddColumn(checklist, "Abgeleitete Anforderung", "Requirement", 1, true);
        tabs.Items.Add(new W.TabItem { Header = "4  Abgeleitete Prüfliste", Content = checklist });
        AddColumn(diagnostics, "Schwere", "Severity", 85); AddColumn(diagnostics, "Code", "Code", 170); AddColumn(diagnostics, "Bereich", "Path", 230); AddColumn(diagnostics, "Diagnose", "Message", 1, true);
        tabs.Items.Add(new W.TabItem { Header = "5  Validierung", Content = diagnostics });
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, tabs)) RefreshDerived(); };
        profileName.Name = "ProfileName"; RegisterName(profileName.Name, profileName);
        reviewScope.Name = "ReviewScope"; RegisterName(reviewScope.Name, reviewScope);
        profileName.TextChanged += (_, _) => { if (!refreshing) { editor.Document.Name = profileName.Text; MarkTitle(); } };
        profileVersion.TextChanged += (_, _) => { if (!refreshing) { editor.Document.Version = profileVersion.Text; MarkTitle(); } };
        reviewScope.TextChanged += (_, _) => { if (!refreshing) { editor.Document.ReviewScope = reviewScope.Text; MarkTitle(); } };
        Closing += (_, e) => { if (!ConfirmPending()) e.Cancel = true; };
        Content = root; Refresh();
    }
    private UIElement ProfileTab()
    {
        var panel = new W.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(identity);
        EditorUi.Field(panel, "Vorlagenname", profileName); EditorUi.Field(panel, "Fachliche Vorlagenversion (JSON-Schema ist separat versioniert)", profileVersion);
        reviewScope.AcceptsReturn = true; reviewScope.TextWrapping = TextWrapping.Wrap; reviewScope.MinHeight = 85; reviewScope.VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto;
        EditorUi.Field(panel, "Unabhängiger Prüfumfang / Kategorien (auch Elemente OHNE BGK einbeziehen)", reviewScope);
        panel.Children.Add(EditorUi.Text("Dieser Text beschreibt den späteren Prüfumfang; er ist noch kein ausführbarer Kategorienfilter. Eine BGK-Auswahl allein kann fehlende Kennzeichnungen nicht finden."));
        panel.Children.Add(bgkSource); panel.Children.Add(EditorUi.Button("BGK-Identifikationsquelle bearbeiten…", () => EditIdentification(true)));
        panel.Children.Add(typeSource); panel.Children.Add(EditorUi.Button("Typnummer-Identifikationsquelle bearbeiten…", () => EditIdentification(false)));
        panel.Children.Add(EditorUi.Text("Standard entsprechend dem vorhandenen Übertragen-Ablauf: BGK = ASSEMBLY_CODE am Typ; Typnummer = ALL_MODEL_TYPE_COMMENTS am Typ. Diese Vorgaben sind keine Erkennung der tatsächlich vorhandenen Modellparameter. Alternativ sind explizite gemeinsame GUIDs oder lokale Familienparameter möglich."));
        panel.Children.Add(EditorUi.Text("Grenzen dieses Moduls: keine Elementsuche, Mengenberechnung, Umrechnung, Rundung, Aggregation, Bindungsprüfung oder Schreibvorgänge in Revit. Regeln sind manuell, Bedingungen ausschließlich UND; mehrere Regeln haben keine Priorität. Katalogimporte lesen nur die unterstützten Formate, keine vollständige GAEB-XSD-Prüfung. Proprietäre Quellsnapshots werden in Ihre JSON-Datei eingebettet: Weitergabe nur mit entsprechenden Rechten."));
        return EditorUi.Scroll(panel);
    }
    private UIElement CatalogTab()
    {
        var panel = new W.DockPanel { Margin = new Thickness(12) };
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Top); panel.Children.Add(buttons);
        buttons.Children.Add(EditorUi.Button("BGK-XML importieren…", () => ImportCatalog("bgk")));
        buttons.Children.Add(EditorUi.Button("LV X82 importieren…", () => ImportCatalog("lv")));
        buttons.Children.Add(EditorUi.Button("Parameter-TXT importieren…", () => ImportCatalog("parameters")));
        W.DockPanel.SetDock(sources, W.Dock.Top); panel.Children.Add(sources);
        var filter = new W.DockPanel(); W.DockPanel.SetDock(filter, W.Dock.Top); panel.Children.Add(filter);
        W.DockPanel.SetDock(catalogKind, W.Dock.Left); filter.Children.Add(catalogKind); filter.Children.Add(catalogSearch);
        catalogSearch.ToolTip = "Katalog durchsuchen – exakte Schlüssel werden nicht verändert.";
        catalogKind.SelectionChanged += (_, _) => RefreshCatalog(); catalogSearch.TextChanged += (_, _) => RefreshCatalog();
        AddColumn(catalog, "Schlüssel / GUID", "Key", 270); AddColumn(catalog, "Typ / OZ / Spezifikation", "Detail", 190); AddColumn(catalog, "Bezeichnung", "Description", 1, true); AddColumn(catalog, "Einheit / Gruppe", "Unit", 110);
        panel.Children.Add(catalog); return panel;
    }
    private UIElement MappingTab()
    {
        var panel = new W.DockPanel { Margin = new Thickness(12) };
        var hint = EditorUi.Text("Manuelle BGK-/Typ-zu-LV-Regeln. Zuerst Quellkataloge importieren. Mit Bearbeiten werden auch UND-Bedingungen und die optionale Mengenquelle geändert.");
        W.DockPanel.SetDock(hint, W.Dock.Top); panel.Children.Add(hint);
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Top); panel.Children.Add(buttons);
        buttons.Children.Add(EditorUi.Button("Zuordnung hinzufügen…", () => EditMapping(false)));
        buttons.Children.Add(EditorUi.Button("Ausgewählte bearbeiten…", () => EditMapping(true)));
        buttons.Children.Add(EditorUi.Button("Ausgewählte entfernen", () => EditorUi.Guard(this, () => {
            if (mappings.SelectedItem is MappingRow row && System.Windows.MessageBox.Show(this, "Diese Zuordnung entfernen?", "AVA", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { editor.RemoveMapping(row.Rule.Id); Refresh(); }
        })));
        mappings.MouseDoubleClick += (_, _) => { if (mappings.SelectedItem is MappingRow) EditMapping(true); };
        AddColumn(mappings, "BGK", "Rule.BgkKey", 100); AddColumn(mappings, "Typ", "Rule.TypeNumber", 75); AddColumn(mappings, "LV-OZ", "Ordinal", 130); AddColumn(mappings, "LV-Text", "Text", 1, true); AddColumn(mappings, "UND", "Rule.Conditions.Count", 50); AddColumn(mappings, "Mengenquelle", "Quantity", 260);
        panel.Children.Add(mappings); return panel;
    }
    private void EditIdentification(bool bgk)
    {
        var dialog = new ParameterSourceWindow(editor.Document, bgk ? editor.Document.BgkIdentification : editor.Document.TypeIdentification, source =>
        {
            var candidate = TemplateJson.Copy(editor.Document); if (bgk) candidate.BgkIdentification = source; else candidate.TypeIdentification = source; TemplateEditor.EnsureValid(candidate);
        }) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result) EditorUi.Guard(this, () => { editor.SetIdentification(bgk, result); Refresh(); });
    }
    private void EditMapping(bool edit)
    {
        if (edit && mappings.SelectedItem is not MappingRow) return;
        if (editor.Document.BgkCatalog.Entries.Count == 0 || editor.Document.LvCatalog.Entries.Count == 0) { EditorUi.Error(this, new InvalidDataException("Zuerst BGK-XML und LV-X82 importieren.")); return; }
        var dialog = new MappingEditorWindow(editor.Document, edit ? ((MappingRow)mappings.SelectedItem).Rule : null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result) EditorUi.Guard(this, () => { editor.UpsertMapping(result); Refresh(); });
    }
    private void Open(bool import)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "AVA-Vorlage (*.json)|*.json", Title = import ? "Portable Vorlage importieren" : "AVA-Vorlage öffnen" };
        if (dialog.ShowDialog(this) != true) return;
        EditorUi.Guard(this, () =>
        {
            var candidate = TemplateJson.Load(dialog.FileName); TemplateEditor.EnsureValid(candidate); // validate before discard prompt or replacement
            if (!ConfirmPending()) return;
            if (import) editor.ImportJson(TemplateJson.Serialize(candidate)); else editor.Open(dialog.FileName);
            Refresh();
        });
    }
    private bool Save(bool saveAs)
    {
        try
        {
            TemplateEditor.EnsureValid(editor.Document);
            var warnings = TemplateValidator.Validate(editor.Document).Count(d => d.Severity == DiagnosticSeverity.Warning);
            if (warnings > 0 && System.Windows.MessageBox.Show(this, $"{warnings} Hinweise in der Validierung. Als Entwurf speichern?", "AVA-Vorlage", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            string? path = editor.FilePath;
            if (saveAs || path == null)
            {
                var dialog = SaveDialog("AVA-Vorlage speichern"); if (dialog.ShowDialog(this) != true) return false; path = dialog.FileName;
            }
            editor.SaveAs(path); Refresh(); return true;
        }
        catch (Exception ex) when (EditorUi.IsInputError(ex)) { EditorUi.Error(this, ex); RefreshDerived(); return false; }
    }
    private void Export()
    {
        var dialog = SaveDialog("Portable JSON-Kopie exportieren"); if (dialog.ShowDialog(this) != true) return;
        EditorUi.Guard(this, () => { editor.Export(dialog.FileName); status.Text = "JSON-Kopie exportiert und zurückgelesen. Aktueller Speicherpfad / Änderungsstatus bleiben unverändert."; });
    }
    private Microsoft.Win32.SaveFileDialog SaveDialog(string title) => new() { Title = title, Filter = "AVA-Vorlage (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = editor.FilePath == null ? "AVA-Vorlage.json" : Path.GetFileName(editor.FilePath), OverwritePrompt = true };
    private bool ConfirmPending()
    {
        if (!editor.IsDirty) return true;
        var answer = System.Windows.MessageBox.Show(this, "Ungespeicherte Änderungen speichern?\nJa: speichern · Nein: verwerfen · Abbrechen: weiter bearbeiten", "AVA-Vorlage", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && Save(false);
    }
    private void ImportCatalog(string kind)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = kind == "bgk" ? "BGK-Katalog (*.xml)|*.xml" : kind == "lv" ? "GAEB DA82/3.3 (*.x82)|*.x82" : "Gemeinsame Parameter (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        EditorUi.Guard(this, () =>
        {
            using var stream = File.OpenRead(dialog.FileName);
            // Parse all input before changing editor state. Each setter validates references on a copy.
            Action replace = kind switch
            {
                "bgk" => Prepared(CatalogReaders.ReadBgk(stream, dialog.FileName), editor.ReplaceBgk),
                "lv" => Prepared(CatalogReaders.ReadX82(stream, dialog.FileName), editor.ReplaceLv),
                _ => Prepared(CatalogReaders.ReadSharedParameters(stream, dialog.FileName), editor.ReplaceParameters)
            };
            if (System.Windows.MessageBox.Show(this, "Den entsprechenden Quellkatalog vollständig ersetzen? Bestehende Zuordnungen werden nicht automatisch geändert; ungültige Referenzen verhindern den Import. Die Quelldatei bleibt unverändert.", "Katalog importieren", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            replace(); Refresh();
        });
    }
    private static Action Prepared<T>(T parsed, Action<T> setter) => () => setter(parsed);
    private void MarkTitle() => Title = "AVA-Vorlage / Prüfliste – " + editor.Document.Name + " *";
    private void Refresh()
    {
        refreshing = true;
        try
        {
            profileName.Text = editor.Document.Name; profileVersion.Text = editor.Document.Version; reviewScope.Text = editor.Document.ReviewScope;
            identity.Text = $"Profil-ID: {editor.Document.ProfileId}   |   Schema: {editor.Document.SchemaVersion}   |   Datei: {editor.FilePath ?? "noch nicht gespeichert"}";
            bgkSource.Text = "BGK: " + editor.Document.BgkIdentification; typeSource.Text = "Typnummer: " + editor.Document.TypeIdentification;
            string Describe<T>(string label, Catalog<T> c) => $"{label}: {c.Entries.Count} Einträge | {c.Source.FileName} | {c.Source.Format}\nSHA-256: {c.Source.Sha256}";
            sources.Text = Describe("BGK", editor.Document.BgkCatalog) + "\n" + Describe("LV", editor.Document.LvCatalog) + "\n" + Describe("Parameter", editor.Document.ParameterCatalog);
            mappings.ItemsSource = editor.Document.Mappings.Select(m => { var l = editor.Document.LvCatalog.Entries.FirstOrDefault(l => l.Id == m.LvItemId); return new MappingRow(m, l?.Ordinal ?? m.LvItemId, l?.Text ?? "", m.Quantity == null ? "nicht definiert" : m.Quantity.Source + " | " + m.Quantity.Unit); }).ToList();
            RefreshCatalog(); RefreshDerived();
            Title = "AVA-Vorlage / Prüfliste – " + editor.Document.Name + (editor.IsDirty ? " *" : "");
        }
        finally { refreshing = false; }
    }
    private void RefreshDerived()
    {
        var findings = TemplateValidator.Validate(editor.Document);
        diagnostics.ItemsSource = findings; checklist.ItemsSource = ChecklistBuilder.Build(editor.Document);
        status.Text = $"{editor.Document.Mappings.Count} Zuordnungen · {findings.Count(d => d.Severity == DiagnosticSeverity.Error)} Fehler · {findings.Count(d => d.Severity == DiagnosticSeverity.Warning)} Hinweise · Prüfliste = Anforderungen, keine Modellbefunde";
    }
    private void RefreshCatalog()
    {
        IEnumerable<CatalogRow> rows = catalogKind.SelectedIndex switch
        {
            0 => editor.Document.BgkCatalog.Entries.SelectMany(b => b.Types.Count == 0 ? new[] { new CatalogRow(b.Key, "", b.Description, "") } : b.Types.Select(t => new CatalogRow(b.Key, t.Number, b.Description + " / " + t.Description, ""))),
            1 => editor.Document.LvCatalog.Entries.Select(l => new CatalogRow(l.Id, l.Ordinal, l.Text, l.Unit)),
            _ => editor.Document.ParameterCatalog.Entries.Select(p => new CatalogRow(p.Guid.ToString(), p.Spec, p.Name, p.Group))
        };
        catalog.ItemsSource = rows.Where(r => r.ToString().Contains(catalogSearch.Text, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private static W.DataGrid Table() => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = W.DataGridSelectionMode.Single, HeadersVisibility = W.DataGridHeadersVisibility.Column, GridLinesVisibility = W.DataGridGridLinesVisibility.Horizontal, Margin = new Thickness(6), EnableRowVirtualization = true };
    private static void AddColumn(W.DataGrid grid, string header, string path, double width, bool star = false)
    {
        var style = new Style(typeof(W.TextBlock)); style.Setters.Add(new Setter(W.TextBlock.TextWrappingProperty, TextWrapping.Wrap)); style.Setters.Add(new Setter(W.TextBlock.MarginProperty, new Thickness(4)));
        grid.Columns.Add(new W.DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new W.DataGridLength(width, star ? W.DataGridLengthUnitType.Star : W.DataGridLengthUnitType.Pixel), MinWidth = 50, ElementStyle = style });
    }
    private sealed record MappingRow(MappingRule Rule, string Ordinal, string Text, string Quantity);
    private sealed record CatalogRow(string Key, string Detail, string Description, string Unit);
}
