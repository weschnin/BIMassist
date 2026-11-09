using System.IO;
using System.Windows;
using Binding = System.Windows.Data.Binding;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

/// <summary>Modal, file-only AVA editor. No Revit API objects, model access, binding or transactions.</summary>
public sealed class AvaTemplateWindow : Window
{
    private TemplateEditor editor = new();
    private readonly AvaWorkspace workspace;
    private readonly W.TextBlock workspaceWarning = EditorUi.Text("");
    private readonly W.TextBox profileName = EditorUi.Input();
    private readonly W.TextBox profileVersion = EditorUi.Input();
    private readonly W.TextBox reviewScope = EditorUi.Input();
    private readonly W.TextBlock identity = EditorUi.Text("");
    private readonly W.TextBlock bgkSource = EditorUi.Text("");
    private readonly W.TextBlock typeSource = EditorUi.Text("");
    private readonly W.TextBlock sources = EditorUi.Text("");
    private readonly W.TextBlock status = EditorUi.Text("");
    private readonly W.TreeView mappings = new() { Margin = new Thickness(6) };
    private readonly W.DataGrid checklist = Table();
    private readonly W.DataGrid diagnostics = Table();
    private readonly W.DataGrid catalog = Table();
    private readonly W.ComboBox catalogKind = new() { ItemsSource = new[] { "BGK und Typen", "LV-Positionen", "Gemeinsame Parameter" }, SelectedIndex = 0, Width = 240, MinHeight = 30, Margin = new Thickness(0, 0, 10, 8) };
    private readonly W.TextBox catalogSearch = EditorUi.Input();
    private bool refreshing;
    private bool shuttingDown;

    // Replace only human interactions in STA tests; all real editor/save/Closing paths still run.
    internal Func<MessageBoxResult>? PendingPrompt { get; set; }
    internal Func<string?>? SavePathSelector { get; set; }
    internal Func<int, bool>? DraftPrompt { get; set; }
    internal Action<Exception>? ErrorReporter { get; set; }

    public AvaTemplateWindow() : this(new AvaWorkspace()) { }

    /// <summary>Inject a workspace with an isolated settings path for file-only hosts and tests.</summary>
    public AvaTemplateWindow(AvaWorkspace workspace)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
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
        var notice = EditorUi.Text("Dateibasierter Vorlageneditor · keine Modellprüfung und keine Revit-Schreibzugriffe. Mengenermittlung / Mengenexport erfolgt separat über den Ribbon-Befehl. Die Prüfliste wird aus der Vorlage abgeleitet; sie ist kein bereits ausgeführter Modellcheck.");
        W.DockPanel.SetDock(notice, W.Dock.Top); root.Children.Add(notice);
        workspaceWarning.Name = "WorkspaceWarning"; RegisterName(workspaceWarning.Name, workspaceWarning);
        workspaceWarning.Visibility = Visibility.Collapsed;
        workspaceWarning.Foreground = System.Windows.Media.Brushes.DarkRed;
        W.DockPanel.SetDock(workspaceWarning, W.Dock.Top); root.Children.Add(workspaceWarning);
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
        // ShutdownStarted is raised before HasShutdownStarted becomes true.
        EventHandler markShutdown = (_, _) => shuttingDown = true;
        Dispatcher.ShutdownStarted += markShutdown;
        Closed += (_, _) => Dispatcher.ShutdownStarted -= markShutdown;
        Closing += (_, e) =>
        {
            // Normal modal-editor closes are cancellable; dispatcher/process teardown
            // must not start dialogs or file writes while the host is shutting down.
            if (!shuttingDown && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished && !Environment.HasShutdownStarted && !ConfirmPending())
                e.Cancel = true;
        };
        Content = root;
        Loaded += LoadWorkspace;
        Refresh();
    }
    private void LoadWorkspace(object sender, RoutedEventArgs e)
    {
        // A window can receive Loaded again after visual-tree changes. Only the first
        // actual open may replace the fresh editor; never overwrite subsequent edits.
        Loaded -= LoadWorkspace;
        if (workspace.HasRememberedTemplate)
        {
            try { workspace.LoadLast(); editor.Open(workspace.LastTemplatePath!); }
            catch (Exception ex) when (EditorUi.IsInputError(ex))
            {
                workspaceWarning.Text = ex.Message; workspaceWarning.Visibility = Visibility.Visible;
            }
        }
        Refresh();
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
        var hint = EditorUi.Text("BGK → Typ → Zuordnung / Bedingungen → Leistungspositionen. Jede Position besitzt ihre eigene Menge. Gruppen lassen sich auf- und zuklappen; Doppelklick bearbeitet die gewählte Zuordnung oder Position.");
        W.DockPanel.SetDock(hint, W.Dock.Top); panel.Children.Add(hint);
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Top); panel.Children.Add(buttons);
        buttons.Children.Add(EditorUi.Button("Zuordnung hinzufügen…", () => EditMapping(false)));
        buttons.Children.Add(EditorUi.Button("Ausgewählte bearbeiten…", () => EditMapping(true)));
        buttons.Children.Add(EditorUi.Button("Leistungsposition hinzufügen…", () => EditPosition(false)));
        buttons.Children.Add(EditorUi.Button("Ausgewählte entfernen", () => EditorUi.Guard(this, () => {
            var selected = SelectedMapping;
            if (selected?.Rule is not { } rule) return;
            string question = selected.Position == null ? "Diese Zuordnung mit allen Leistungspositionen entfernen?" : "Diese Leistungsposition aus der Zuordnung entfernen?";
            if (System.Windows.MessageBox.Show(this, question, "AVA", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (selected.Position == null) editor.RemoveMapping(rule.Id);
            else { var copy = TemplateJson.Copy(rule); copy.Positions.RemoveAll(p => p.Id == selected.Position.Id); editor.UpsertMapping(copy); }
            Refresh();
        })));
        buttons.Children.Add(EditorUi.Button("Alle aufklappen", () => ExpandMappings(true)));
        buttons.Children.Add(EditorUi.Button("Alle zuklappen", () => ExpandMappings(false)));
        mappings.Name = "MappingsTree"; RegisterName(mappings.Name, mappings);
        mappings.MouseDoubleClick += (_, e) => { if (SelectedMapping?.Rule != null) { if (SelectedMapping.Position != null) EditPosition(true); else EditMapping(true); e.Handled = true; } };
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
        if (edit && SelectedMapping?.Rule == null) return;
        if (editor.Document.BgkCatalog.Entries.Count == 0 || editor.Document.LvCatalog.Entries.Count == 0) { EditorUi.Error(this, new InvalidDataException("Zuerst BGK-XML und LV-X82 importieren.")); return; }
        if (edit && SelectedMapping?.Position != null) { EditPosition(true); return; }
        var dialog = new MappingEditorWindow(editor.Document, edit ? SelectedMapping!.Rule : null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result) EditorUi.Guard(this, () => { editor.UpsertMapping(result); Refresh(); });
    }
    private MappingTreeNode? SelectedMapping => (mappings.SelectedItem as W.TreeViewItem)?.Tag as MappingTreeNode;
    private void EditPosition(bool edit)
    {
        var selected = SelectedMapping;
        if (selected?.Rule is not { } rule) { EditorUi.Error(this, new InvalidDataException("Zuerst eine Zuordnung oder ihre Leistungsposition auswählen.")); return; }
        if (edit && selected.Position == null) return;
        var dialog = new PositionEditorWindow(editor.Document, edit ? selected.Position : null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result) EditorUi.Guard(this, () => {
            var copy = TemplateJson.Copy(rule); int index = copy.Positions.FindIndex(p => p.Id == result.Id);
            if (index < 0) copy.Positions.Add(result); else copy.Positions[index] = result;
            editor.UpsertMapping(copy); Refresh();
        });
    }
    internal void RefreshMappings()
    {
        var expanded = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? selected = SelectedMapping?.Key;
        void Remember(W.ItemsControl parent)
        {
            foreach (W.TreeViewItem item in parent.Items) { expanded[((MappingTreeNode)item.Tag).Key] = item.IsExpanded; Remember(item); }
        }
        Remember(mappings); mappings.Items.Clear();
        W.TreeViewItem? selection = null;
        W.TreeViewItem Create(MappingTreeNode node)
        {
            var item = new W.TreeViewItem { Tag = node, IsExpanded = expanded.GetValueOrDefault(node.Key, true),
                Header = new W.TextBlock { Text = node.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 980, Margin = new Thickness(3, 5, 3, 5), FontWeight = node.Kind is "BGK" or "Typ" ? FontWeights.SemiBold : FontWeights.Normal },
                ToolTip = node.Position == null ? node.Rule?.Id.ToString() : "LV-ID: " + node.Position.LvItemId + " | Position: " + node.Position.Id };
            foreach (var child in node.Children) item.Items.Add(Create(child));
            if (node.Key == selected) selection = item;
            return item;
        }
        foreach (var node in MappingTreeModel.Build(editor.Document)) mappings.Items.Add(Create(node));
        // Selection changes reach the TreeView only after the item belongs to its hierarchy.
        if (selection != null) selection.IsSelected = true;
    }
    private void ExpandMappings(bool expand)
    {
        void Apply(W.ItemsControl parent) { foreach (W.TreeViewItem item in parent.Items) { item.IsExpanded = expand; Apply(item); } }
        Apply(mappings);
    }
    private void Open(bool import)
    {
        var dialog = AvaFileDialogs.OpenTemplate(import);
        if (dialog.ShowDialog(this) != true) return;
        OpenPath(dialog.FileName, import);
    }
    internal bool OpenPath(string path, bool import)
    {
        try
        {
            // Validate before asking to discard edits, then re-read after a pending
            // save: opening the same file must not commit its pre-save snapshot.
            TemplateEditor ReadCandidate()
            {
                var replacement = new TemplateEditor();
                if (import) replacement.ImportJson(TemplateJson.Serialize(TemplateJson.Load(path)));
                else replacement.Open(Path.GetFullPath(path));
                return replacement;
            }
            var candidate = ReadCandidate();
            bool hadPendingEdits = editor.IsDirty;
            if (!ConfirmPending()) return false;
            if (hadPendingEdits) candidate = ReadCandidate();
            if (!import) workspace.Remember(candidate.FilePath!);
            editor = candidate;
            workspaceWarning.Text = ""; workspaceWarning.Visibility = Visibility.Collapsed;
            Refresh(); return true;
        }
        catch (Exception ex) when (EditorUi.IsInputError(ex)) { ReportError(ex); return false; }
    }
    private bool Save(bool saveAs)
    {
        try
        {
            TemplateEditor.EnsureValid(editor.Document);
            var warnings = TemplateValidator.Validate(editor.Document).Count(d => d.Severity == DiagnosticSeverity.Warning);
            if (warnings > 0 && !(DraftPrompt?.Invoke(warnings) ?? (System.Windows.MessageBox.Show(this, $"{warnings} Hinweise in der Validierung. Als Entwurf speichern?", "AVA-Vorlage", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes))) return false;
            string? path = editor.FilePath;
            if (saveAs || path == null)
            {
                if (SavePathSelector != null) path = SavePathSelector();
                else { var dialog = SaveDialog("AVA-Vorlage speichern"); if (dialog.ShowDialog(this) != true) return false; path = dialog.FileName; }
                if (string.IsNullOrWhiteSpace(path)) return false;
            }
            // Keep the old document/path/baseline if either file or workspace persistence fails.
            workspace.EnsureTemplatePath(path);
            var candidate = new TemplateEditor(); candidate.ImportJson(TemplateJson.Serialize(editor.Document));
            candidate.SaveAs(path); workspace.Remember(path);
            editor = candidate;
            workspaceWarning.Text = ""; workspaceWarning.Visibility = Visibility.Collapsed;
            Refresh(); return true;
        }
        catch (Exception ex) when (EditorUi.IsInputError(ex)) { ReportError(ex); RefreshDerived(); return false; }
    }
    private void Export()
    {
        var dialog = SaveDialog("Portable JSON-Kopie exportieren"); if (dialog.ShowDialog(this) != true) return;
        EditorUi.Guard(this, () => ExportPath(dialog.FileName));
    }
    internal void ExportPath(string path)
    {
        workspace.EnsureTemplatePath(path);
        editor.Export(path);
        status.Text = "JSON-Kopie exportiert und zurückgelesen. Aktueller Speicherpfad / Änderungsstatus bleiben unverändert.";
    }
    private Microsoft.Win32.SaveFileDialog SaveDialog(string title) => AvaFileDialogs.SaveTemplate(title, editor.FilePath);
    private bool ConfirmPending()
    {
        if (!editor.IsDirty) return true;
        var answer = PendingPrompt?.Invoke() ?? System.Windows.MessageBox.Show(this, "Ungespeicherte Änderungen speichern?\nJa: speichern · Nein: verwerfen · Abbrechen: weiter bearbeiten", "AVA-Vorlage", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && Save(false);
    }
    private void ImportCatalog(string kind)
    {
        var dialog = AvaFileDialogs.OpenCatalog(kind);
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
    private void ReportError(Exception ex) { if (ErrorReporter != null) ErrorReporter(ex); else EditorUi.Error(this, ex); }
    private void MarkTitle() => Title = "AVA-Vorlage / Prüfliste – " + editor.Document.Name + (editor.IsDirty ? " *" : "");
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
            RefreshMappings();
            RefreshCatalog(); RefreshDerived();
            Title = "AVA-Vorlage / Prüfliste – " + editor.Document.Name + (editor.IsDirty ? " *" : "");
        }
        finally { refreshing = false; }
    }
    private void RefreshDerived()
    {
        var findings = TemplateValidator.Validate(editor.Document);
        diagnostics.ItemsSource = findings; checklist.ItemsSource = ChecklistBuilder.Build(editor.Document);
        status.Text = $"{editor.Document.Mappings.Count} Zuordnungen · {editor.Document.Mappings.Sum(m => m.Positions.Count)} Leistungspositionen · {findings.Count(d => d.Severity == DiagnosticSeverity.Error)} Fehler · {findings.Count(d => d.Severity == DiagnosticSeverity.Warning)} Hinweise · Prüfliste = Anforderungen, keine Modellbefunde";
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

    private sealed record CatalogRow(string Key, string Detail, string Description, string Unit);
}
