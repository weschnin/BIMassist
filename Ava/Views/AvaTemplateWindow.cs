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
    private readonly W.TextBlock bgkSourceInfo = EditorUi.Text("");
    private readonly W.TextBlock lvSourceInfo = EditorUi.Text("");
    private readonly W.TextBlock parameterSourceInfo = EditorUi.Text("");
    private readonly W.TextBlock activeTemplatePath = EditorUi.Text("");
    private readonly W.Button bgkExpandAll;
    private readonly W.Button bgkCollapseAll;
    private readonly W.Button lvExpandAll;
    private readonly W.Button lvCollapseAll;
    private readonly W.TextBlock status = EditorUi.Text("");
    private readonly W.TreeView mappings = new() { Margin = new Thickness(6) };
    private readonly W.TreeView bgkCatalogTree = new() { Margin = new Thickness(6) };
    private readonly W.TreeView lvCatalogTree = new() { Margin = new Thickness(6) };
    private readonly Dictionary<string, bool> bgkCatalogTreeExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> lvCatalogTreeExpansion = new(StringComparer.Ordinal);
    private readonly W.DataGrid checklist = Table();
    private readonly W.DataGrid diagnostics = Table();
    private readonly W.DataGrid catalog = Table();
    private readonly W.TextBox bgkCatalogSearch = EditorUi.Input();
    private readonly W.TextBox lvCatalogSearch = EditorUi.Input();
    private readonly W.TextBox parameterCatalogSearch = EditorUi.Input();
    private bool refreshing;
    private bool refreshingBgkCatalogTree;
    private bool refreshingLvCatalogTree;
    private bool shuttingDown;

    // Replace only human interactions in STA tests; all real editor/save/Closing paths still run.
    internal Func<MessageBoxResult>? PendingPrompt { get; set; }
    internal Func<string?>? SavePathSelector { get; set; }
    internal Func<int, bool>? DraftPrompt { get; set; }
    internal Func<string, bool>? RemovalPrompt { get; set; }
    internal Func<MappingRule, MappingRule?>? DiagnosticMappingEditor { get; set; }
    internal Action<Exception>? ErrorReporter { get; set; }

    public AvaTemplateWindow() : this(new AvaWorkspace()) { }

    /// <summary>Inject a workspace with an isolated settings path for file-only hosts and tests.</summary>
    public AvaTemplateWindow(AvaWorkspace workspace)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        bgkExpandAll = EditorUi.Button("Alles aufklappen", () => ExpandCatalogs(bgkCatalogTree, bgkCatalogTreeExpansion, true));
        bgkCollapseAll = EditorUi.Button("Alles zuklappen", () => ExpandCatalogs(bgkCatalogTree, bgkCatalogTreeExpansion, false));
        lvExpandAll = EditorUi.Button("Alles aufklappen", () => ExpandCatalogs(lvCatalogTree, lvCatalogTreeExpansion, true));
        lvCollapseAll = EditorUi.Button("Alles zuklappen", () => ExpandCatalogs(lvCatalogTree, lvCatalogTreeExpansion, false));
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
        activeTemplatePath.Name = "TemplateFilePath"; RegisterName(activeTemplatePath.Name, activeTemplatePath);
        var templatePathBorder = new W.Border
        {
            BorderBrush = System.Windows.Media.Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 0, 8),
            Child = activeTemplatePath
        };
        W.DockPanel.SetDock(templatePathBorder, W.Dock.Top); root.Children.Add(templatePathBorder);
        var notice = EditorUi.Text("Dateibasierter Vorlageneditor · keine Modellprüfung und keine Revit-Schreibzugriffe. Mengenermittlung / Mengenexport erfolgt separat über den Ribbon-Befehl. Die Prüfliste wird aus der Vorlage abgeleitet; sie ist kein bereits ausgeführter Modellcheck.");
        W.DockPanel.SetDock(notice, W.Dock.Top); root.Children.Add(notice);
        workspaceWarning.Name = "WorkspaceWarning"; RegisterName(workspaceWarning.Name, workspaceWarning);
        workspaceWarning.Visibility = Visibility.Collapsed;
        workspaceWarning.Foreground = System.Windows.Media.Brushes.DarkRed;
        W.DockPanel.SetDock(workspaceWarning, W.Dock.Top); root.Children.Add(workspaceWarning);
        W.DockPanel.SetDock(status, W.Dock.Bottom); root.Children.Add(status);
        var tabs = new W.TabControl { Name = "EditorTabs" }; RegisterName(tabs.Name, tabs); root.Children.Add(tabs);
        tabs.Items.Add(new W.TabItem { Header = "1  Profil / Prüfumfang", Content = ProfileTab() });
        tabs.Items.Add(new W.TabItem { Header = "2  BGK-Katalog", Content = CatalogTab("bgk") });
        tabs.Items.Add(new W.TabItem { Header = "3  LV-Katalog", Content = CatalogTab("lv") });
        tabs.Items.Add(new W.TabItem { Header = "4  Parameter-Katalog", Content = CatalogTab("parameters") });
        tabs.Items.Add(new W.TabItem { Header = "5  Zuordnungen", Content = MappingTab() });
        AddColumn(checklist, "Art", "Kind", 130); AddColumn(checklist, "Gilt für", "AppliesTo", 290); AddColumn(checklist, "Abgeleitete Anforderung", "Requirement", 1, true);
        tabs.Items.Add(new W.TabItem { Header = "6  Abgeleitete Prüfliste", Content = checklist });
        AddColumn(diagnostics, "Schwere", "Severity", 85); AddColumn(diagnostics, "Code", "Code", 170); AddColumn(diagnostics, "Bereich", "Path", 230); AddColumn(diagnostics, "Diagnose", "Message", 1, true);
        tabs.Items.Add(new W.TabItem { Header = "7  Validierung", Content = diagnostics });
        diagnostics.MouseDoubleClick += (_, e) =>
        {
            if (diagnostics.SelectedItem is Diagnostic diagnostic) { EditDiagnosticMapping(diagnostic); e.Handled = true; }
        };
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
        panel.Children.Add(EditorUi.Text("Standard entsprechend dem vorhandenen Übertragen-Ablauf: BGK = ASSEMBLY_CODE am Typ; Typnummer = ALL_MODEL_TYPE_COMMENTS am Typ. Diese Vorgaben sind keine Erkennung der tatsächlich vorhandenen Modellparameter. Alternativ sind exakte gemeinsame GUIDs oder eindeutige Parameter nach Name, Spezifikation und Instanz/Typ möglich."));
        panel.Children.Add(EditorUi.Text("Grenzen dieses Moduls: keine Elementsuche, Mengenberechnung, Umrechnung, Rundung, Aggregation, Bindungsprüfung oder Schreibvorgänge in Revit. Regeln sind manuell, Bedingungen ausschließlich UND; mehrere Regeln haben keine Priorität. Katalogimporte lesen nur die unterstützten Formate, keine vollständige GAEB-XSD-Prüfung. Proprietäre Quellsnapshots werden in Ihre JSON-Datei eingebettet: Weitergabe nur mit entsprechenden Rechten."));
        return EditorUi.Scroll(panel);
    }
    private UIElement CatalogTab(string kind)
    {
        var panel = new W.DockPanel { Margin = new Thickness(12) };
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Top); panel.Children.Add(buttons);
        var search = kind switch { "bgk" => bgkCatalogSearch, "lv" => lvCatalogSearch, _ => parameterCatalogSearch };
        var sourceInfo = kind switch { "bgk" => bgkSourceInfo, "lv" => lvSourceInfo, _ => parameterSourceInfo };
        var sourceLabel = kind switch { "bgk" => "BGK-Quelle", "lv" => "LV-Quelle", _ => "Parameter-Quelle" };
        buttons.Children.Add(EditorUi.Button(kind switch { "bgk" => "BGK-XML importieren…", "lv" => "LV X82 importieren…", _ => "Parameter-TXT importieren…" }, () => ImportCatalog(kind)));
        sourceInfo.Margin = new Thickness(4);
        W.DockPanel.SetDock(sourceInfo, W.Dock.Top); panel.Children.Add(sourceInfo);
        var divider = new W.Separator { Margin = new Thickness(0, 2, 0, 2) };
        W.DockPanel.SetDock(divider, W.Dock.Top); panel.Children.Add(divider);
        var searchLabel = EditorUi.Text(sourceLabel + " durchsuchen");
        W.DockPanel.SetDock(searchLabel, W.Dock.Top); panel.Children.Add(searchLabel);
        search.ToolTip = "Katalog durchsuchen – exakte Schlüssel werden nicht verändert.";
        search.Margin = new Thickness(0, 2, 0, 8);
        W.DockPanel.SetDock(search, W.Dock.Top); panel.Children.Add(search);
        search.TextChanged += (_, _) => RefreshCatalog(kind);
        if (kind is "bgk" or "lv")
        {
            var expand = kind == "bgk" ? bgkExpandAll : lvExpandAll;
            var collapse = kind == "bgk" ? bgkCollapseAll : lvCollapseAll;
            string prefix = kind == "bgk" ? "Bgk" : "Lv";
            expand.Name = prefix + "CatalogExpandAll"; RegisterName(expand.Name, expand);
            collapse.Name = prefix + "CatalogCollapseAll"; RegisterName(collapse.Name, collapse);
            var expandButtons = new W.WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
            expandButtons.Children.Add(expand); expandButtons.Children.Add(collapse);
            W.DockPanel.SetDock(expandButtons, W.Dock.Top); panel.Children.Add(expandButtons);
            var tree = kind == "bgk" ? bgkCatalogTree : lvCatalogTree;
            string treeName = kind == "bgk" ? "CatalogTree" : "LvCatalogTree";
            tree.Name = treeName; RegisterName(tree.Name, tree);
            if (kind == "bgk") panel.Children.Add(tree);
            else
            {
                var hierarchyHint = EditorUi.Text("StammLV: LV → Titel → Nummer / Leistung. Interne Schlüssel und GUIDs werden nicht als Anzeigespalte verwendet.");
                W.DockPanel.SetDock(hierarchyHint, W.Dock.Top); panel.Children.Add(hierarchyHint); panel.Children.Add(tree);
            }
        }
        else
        {
            AddColumn(catalog, "Schlüssel / GUID", "Key", 270); AddColumn(catalog, "Spezifikation", "Detail", 190); AddColumn(catalog, "Parametername", "Description", 1, true); AddColumn(catalog, "Gruppe", "Unit", 150);
            catalog.Name = "ParameterCatalogGrid"; RegisterName(catalog.Name, catalog);
            panel.Children.Add(catalog);
        }
        return panel;
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
        buttons.Children.Add(EditorUi.Button("Zuordnung duplizieren…", () => EditorUi.Guard(this, DuplicateSelectedMapping)));
        buttons.Children.Add(EditorUi.Button("Ausgewählte entfernen", () => EditorUi.Guard(this, RemoveSelectedMapping)));
        buttons.Children.Add(EditorUi.Button("Alle aufklappen", () => ExpandMappings(true)));
        buttons.Children.Add(EditorUi.Button("Alle zuklappen", () => ExpandMappings(false)));
        mappings.Name = "MappingsTree"; RegisterName(mappings.Name, mappings);
        mappings.MouseDoubleClick += (_, e) => { if (SelectedMapping?.Rule != null) { if (SelectedMapping.Position != null) EditPosition(true); else EditMapping(true); e.Handled = true; } };
        panel.Children.Add(mappings); return panel;
    }
    internal static MappingRule DuplicateMappingRule(MappingRule source)
    {
        var copy = TemplateJson.Copy(source);
        copy.Id = Guid.NewGuid();
        foreach (var position in copy.Positions) position.Id = Guid.NewGuid();
        return copy;
    }
    private bool ConfirmRemoval(string question) => RemovalPrompt?.Invoke(question)
        ?? System.Windows.MessageBox.Show(this, question, "AVA", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private void RemoveSelectedMapping()
    {
        var selected = SelectedMapping;
        if (selected == null) return;
        var mappingsToRemove = selected.Kind switch
        {
            "BGK" => editor.Document.Mappings.Where(rule => IsWithinBgk(rule.BgkKey, selected.Key["bgk:".Length..])).ToList(),
            "Typ" => editor.Document.Mappings.Where(rule => IsSameType(rule, selected.Key)).ToList(),
            "Zuordnung" when selected.Rule != null => [selected.Rule],
            "Position" when selected.Rule != null && selected.Position != null && selected.Rule.Positions.Count == 1 => [selected.Rule],
            _ => []
        };
        if (mappingsToRemove.Count > 0)
        {
            var positionCount = mappingsToRemove.Sum(rule => rule.Positions.Count);
            var conditionCount = mappingsToRemove.Sum(rule => rule.Conditions.Count);
            var isSubtree = selected.Kind is "BGK" or "Typ";
            var question = isSubtree
                ? $"Alle Elemente unterhalb von '{selected.Label}' einschließlich {conditionCount} Bedingungen, {mappingsToRemove.Count} Zuordnungen, {positionCount} Leistungspositionen und Mengen löschen?"
                : selected.Kind == "Position"
                    ? $"Diese Leistungsposition ist die letzte in ihrer Zuordnung. Beim Entfernen werden auch die gesamte Zuordnung, {conditionCount} {(conditionCount == 1 ? "Bedingung" : "Bedingungen")} und ihre Mengenangabe gelöscht. Wirklich fortfahren?"
                    : "Diese Zuordnung mit allen Bedingungen, Leistungspositionen und Mengen entfernen?";
            if (!ConfirmRemoval(question)) return;
            editor.RemoveMappings(mappingsToRemove.Select(rule => rule.Id));
            Refresh();
            return;
        }
        if (selected.Kind == "Position" && selected.Rule != null && selected.Position != null)
        {
            if (!ConfirmRemoval("Diese Leistungsposition einschließlich ihrer Mengenangabe entfernen?")) return;
            var copy = TemplateJson.Copy(selected.Rule);
            copy.Positions.RemoveAll(position => position.Id == selected.Position.Id);
            editor.UpsertMapping(copy);
            Refresh();
        }
    }
    private static bool IsWithinBgk(string candidate, string selected) =>
        candidate == selected || candidate.StartsWith(selected + "-", StringComparison.Ordinal);
    private static bool IsSameType(MappingRule rule, string typeKey)
    {
        const string marker = "/typ:";
        var separator = typeKey.LastIndexOf(marker, StringComparison.Ordinal);
        if (!typeKey.StartsWith("bgk:", StringComparison.Ordinal) || separator < 0) return false;
        var bgkKey = typeKey["bgk:".Length..separator];
        var typeNumber = typeKey[(separator + marker.Length)..];
        return rule.BgkKey == bgkKey && rule.TypeNumber == typeNumber;
    }
    private void DuplicateSelectedMapping()
    {
        if (SelectedMapping?.Rule is not { } source) return;
        var duplicate = DuplicateMappingRule(source);
        var dialog = new MappingEditorWindow(editor.Document, duplicate) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            editor.UpsertMapping(dialog.BuildRule());
            Refresh();
        }
    }
    private void EditDiagnosticMapping(Diagnostic diagnostic)
    {
        int start = diagnostic.Path.IndexOf('[');
        int end = start >= 0 ? diagnostic.Path.IndexOf(']', start + 1) : -1;
        if (start < 0 || end < 0 || !Guid.TryParse(diagnostic.Path[(start + 1)..end], out var ruleId)) return;
        var rule = editor.Document.Mappings.SingleOrDefault(candidate => candidate.Id == ruleId);
        if (rule == null) return;
        MappingRule? result;
        if (DiagnosticMappingEditor != null) result = DiagnosticMappingEditor(rule);
        else
        {
            var dialog = new MappingEditorWindow(editor.Document, rule) { Owner = this };
            result = dialog.ShowDialog() == true ? dialog.BuildRule() : null;
        }
        if (result != null) EditorUi.Guard(this, () => { editor.UpsertMapping(result); Refresh(); });
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
        if (edit)
        {
            if (selected.Position == null) return;
            var positionDialog = new PositionEditorWindow(editor.Document, selected.Position) { Owner = this };
            if (positionDialog.ShowDialog() == true && positionDialog.Result is { } edited) EditorUi.Guard(this, () => {
                var copy = TemplateJson.Copy(rule); int index = copy.Positions.FindIndex(p => p.Id == edited.Id);
                if (index >= 0) copy.Positions[index] = edited;
                editor.UpsertMapping(copy); Refresh();
            });
            return;
        }
        var picker = new LvPositionPickerWindow(editor.Document) { Owner = this };
        if (picker.ShowDialog() != true) return;
        var copyRule = AddSelectedPositions(rule, picker.SelectedPositionIds, editor.Document.LvCatalog.Entries.Select(entry => entry.Id));
        if (copyRule.Positions.Count != rule.Positions.Count) { editor.UpsertMapping(copyRule); Refresh(); }
    }
    internal static MappingRule AddSelectedPositions(MappingRule rule, IEnumerable<string> selectedIds, IEnumerable<string> validIds)
    {
        var copy = TemplateJson.Copy(rule);
        var knownIds = validIds.ToHashSet(StringComparer.Ordinal);
        var existingIds = copy.Positions.Select(position => position.LvItemId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in selectedIds)
            if (knownIds.Contains(id) && existingIds.Add(id)) copy.Positions.Add(new MappingPosition { LvItemId = id });
        return copy;
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
            EditorUi.SelectTreeItemOnClick(item);
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
    private static void ExpandCatalogs(W.TreeView tree, Dictionary<string, bool> expansion, bool expand)
    {
        void Apply(W.ItemsControl parent)
        {
            foreach (W.TreeViewItem item in parent.Items)
            {
                item.IsExpanded = expand;
                if (item.Tag is CatalogTreeNode node) expansion[node.Key] = expand;
                Apply(item);
            }
        }
        Apply(tree);
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
            bgkSourceInfo.Text = Describe("BGK-Quelle", editor.Document.BgkCatalog);
            lvSourceInfo.Text = Describe("LV-Quelle", editor.Document.LvCatalog);
            parameterSourceInfo.Text = Describe("Parameter-Quelle", editor.Document.ParameterCatalog);
            activeTemplatePath.Text = "Aktive AVA-Vorlage: " + (editor.FilePath ?? "noch nicht gespeichert");
            RefreshMappings();
            RefreshCatalogs(); RefreshDerived();
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
    private void RefreshCatalogs()
    {
        RefreshCatalog("bgk"); RefreshCatalog("lv"); RefreshCatalog("parameters");
    }
    private void RefreshCatalog(string kind)
    {
        var filter = kind switch { "bgk" => bgkCatalogSearch.Text, "lv" => lvCatalogSearch.Text, _ => parameterCatalogSearch.Text };
        if (kind is "bgk" or "lv")
        {
            var isBgk = kind == "bgk";
            var tree = isBgk ? bgkCatalogTree : lvCatalogTree;
            var expansion = isBgk ? bgkCatalogTreeExpansion : lvCatalogTreeExpansion;
            var nodes = isBgk ? CatalogTreeModel.BuildBgk(editor.Document, filter) : CatalogTreeModel.BuildLv(editor.Document, filter);
            if (isBgk) refreshingBgkCatalogTree = true; else refreshingLvCatalogTree = true;
            try
            {
                tree.Items.Clear();
                W.TreeViewItem Create(CatalogTreeNode node)
                {
                    var item = new W.TreeViewItem
                    {
                        Tag = node,
                        Header = new W.TextBlock { Text = node.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 1000, Margin = new Thickness(3, 4, 3, 4) },
                        IsExpanded = expansion.GetValueOrDefault(node.Key, true)
                    };
                    EditorUi.SelectTreeItemOnClick(item);
                    item.Expanded += (_, _) => { if (!(isBgk ? refreshingBgkCatalogTree : refreshingLvCatalogTree)) expansion[node.Key] = true; };
                    item.Collapsed += (_, _) => { if (!(isBgk ? refreshingBgkCatalogTree : refreshingLvCatalogTree)) expansion[node.Key] = false; };
                    foreach (var child in node.Children) item.Items.Add(Create(child));
                    return item;
                }
                foreach (var node in nodes) tree.Items.Add(Create(node));
            }
            finally { if (isBgk) refreshingBgkCatalogTree = false; else refreshingLvCatalogTree = false; }
            return;
        }
        var rows = editor.Document.ParameterCatalog.Entries.Select(p => new CatalogRow(p.Guid.ToString(), p.Spec, p.Name, p.Group));
        catalog.ItemsSource = rows.Where(r => r.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private static W.DataGrid Table() => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = W.DataGridSelectionMode.Single, HeadersVisibility = W.DataGridHeadersVisibility.Column, GridLinesVisibility = W.DataGridGridLinesVisibility.Horizontal, Margin = new Thickness(6), EnableRowVirtualization = true };
    private static void AddColumn(W.DataGrid grid, string header, string path, double width, bool star = false)
    {
        var style = new Style(typeof(W.TextBlock)); style.Setters.Add(new Setter(W.TextBlock.TextWrappingProperty, TextWrapping.Wrap)); style.Setters.Add(new Setter(W.TextBlock.MarginProperty, new Thickness(4)));
        grid.Columns.Add(new W.DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new W.DataGridLength(width, star ? W.DataGridLengthUnitType.Star : W.DataGridLengthUnitType.Pixel), MinWidth = 50, ElementStyle = style });
    }

    private sealed record CatalogRow(string Key, string Detail, string Description, string Unit);
}
