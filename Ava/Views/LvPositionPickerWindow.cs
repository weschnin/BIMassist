using System.IO;
using System.Windows;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

/// <summary>Hierarchical multi-select for LV items. Selecting a title adds its full subtree only after confirmation.</summary>
internal sealed class LvPositionPickerWindow : Window
{
    private readonly AvaTemplate template;
    private readonly List<CatalogTreeNode> completeTree;
    private readonly Dictionary<string, CatalogTreeNode> nodesByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> selectedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> collapsedNodeKeys = new(StringComparer.Ordinal);
    private readonly W.TreeView tree = new() { Margin = new Thickness(4) };
    private readonly W.TextBox search = EditorUi.Input();
    private readonly W.TextBlock selectionCount = EditorUi.Text("0 Positionen ausgewählt");
    private bool updatingChecks;

    internal Func<int, bool>? TitleSelectionPrompt { get; set; }
    internal IReadOnlyList<string> SelectedPositionIds => template.LvCatalog.Entries
        .Where(item => selectedIds.Contains(item.Id)).Select(item => item.Id).ToArray();

    internal LvPositionPickerWindow(AvaTemplate template, IEnumerable<string>? initiallySelected = null)
    {
        this.template = template;
        completeTree = CatalogTreeModel.BuildLv(template);
        Index(completeTree);
        if (initiallySelected != null)
            foreach (var id in initiallySelected)
                if (template.LvCatalog.Entries.Any(item => item.Id == id)) selectedIds.Add(id);

        Title = "Leistungspositionen auswählen";
        Width = 1000; Height = 760; MinWidth = 700; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new W.DockPanel { Margin = new Thickness(14) };
        var buttons = new W.WrapPanel(); W.DockPanel.SetDock(buttons, W.Dock.Bottom); root.Children.Add(buttons);
        buttons.Children.Add(EditorUi.Button("Auswahl übernehmen", () => {
            if (selectedIds.Count == 0) { EditorUi.Error(this, new InvalidDataException("Mindestens eine LV-Position auswählen.")); return; }
            DialogResult = true;
        }));
        buttons.Children.Add(EditorUi.Button("Abbrechen", () => DialogResult = false));
        W.DockPanel.SetDock(selectionCount, W.Dock.Bottom); root.Children.Add(selectionCount);

        var toolbar = new W.WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        toolbar.Children.Add(EditorUi.Button("Alles aufklappen", () => ExpandAll(true)));
        toolbar.Children.Add(EditorUi.Button("Alles zuklappen", () => ExpandAll(false)));
        toolbar.Children.Add(EditorUi.Text("Filter:"));
        search.Width = 360; search.ToolTip = "LV nach OZ oder Leistungstext filtern";
        toolbar.Children.Add(search);
        W.DockPanel.SetDock(toolbar, W.Dock.Top); root.Children.Add(toolbar);
        var hint = EditorUi.Text("Mehrere OZ können über ihre Kontrollkästchen gewählt werden. Ein Titel wählt alle darunterliegenden Positionen nach Bestätigung aus.");
        W.DockPanel.SetDock(hint, W.Dock.Top); root.Children.Add(hint);
        root.Children.Add(tree);
        search.TextChanged += (_, _) => RefreshTree();
        RefreshTree();
        Content = root;
    }

    internal bool SetNodeChecked(CatalogTreeNode node, bool isChecked)
    {
        if (!nodesByKey.TryGetValue(node.Key, out var completeNode)) return false;
        var descendants = CatalogTreeModel.GetPositionIds(completeNode);
        if (descendants.Count == 0) return false;
        if (isChecked && (completeNode.Key.StartsWith("category:", StringComparison.Ordinal)
            || completeNode.Key.StartsWith("section:", StringComparison.Ordinal)))
        {
            bool confirmed = TitleSelectionPrompt?.Invoke(descendants.Count)
                ?? MessageBox.Show(this,
                    $"Der Titel „{completeNode.Label}“ enthält {descendants.Count} LV-Positionen. Sollen alle Unterelemente zur Zuordnung hinzugefügt werden?",
                    "Leistungspositionen hinzufügen", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!confirmed) return false;
        }
        foreach (var id in descendants)
        {
            if (isChecked) selectedIds.Add(id);
            else selectedIds.Remove(id);
        }
        RefreshSelectionVisuals();
        return true;
    }

    private void Index(IEnumerable<CatalogTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            nodesByKey[node.Key] = node;
            Index(node.Children);
        }
    }

    private void RefreshTree()
    {
        updatingChecks = true;
        try
        {
            tree.Items.Clear();
            foreach (var node in CatalogTreeModel.BuildLv(template, search.Text)) tree.Items.Add(CreateItem(node));
            RefreshSelectionVisuals();
        }
        finally { updatingChecks = false; }
    }

    private W.TreeViewItem CreateItem(CatalogTreeNode node)
    {
        var item = new W.TreeViewItem { Tag = node, IsExpanded = !collapsedNodeKeys.Contains(node.Key) };
        EditorUi.SelectTreeItemOnClick(item);
        item.Collapsed += (_, _) => collapsedNodeKeys.Add(node.Key);
        item.Expanded += (_, _) => collapsedNodeKeys.Remove(node.Key);
        var header = new W.StackPanel { Orientation = W.Orientation.Horizontal };
        bool selectable = node.Key.StartsWith("item:", StringComparison.Ordinal)
            || node.Key.StartsWith("category:", StringComparison.Ordinal)
            || node.Key.StartsWith("section:", StringComparison.Ordinal);
        if (selectable)
        {
            var check = new W.CheckBox { IsThreeState = true, IsChecked = GetNodeCheckState(node),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), Tag = node };
            check.Click += (_, _) => OnCheckChanged(check, check.IsChecked != false);
            header.Children.Add(check);
        }
        header.Children.Add(new W.TextBlock { Text = node.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 850,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 4, 2, 4) });
        item.Header = header;
        foreach (var child in node.Children) item.Items.Add(CreateItem(child));
        return item;
    }

    private void RefreshSelectionVisuals()
    {
        updatingChecks = true;
        try
        {
            selectionCount.Text = $"{selectedIds.Count} Position(en) ausgewählt";
            void Update(W.ItemsControl parent)
            {
                foreach (W.TreeViewItem item in parent.Items)
                {
                    if (item.Tag is CatalogTreeNode node && item.Header is W.StackPanel header
                        && header.Children.OfType<W.CheckBox>().FirstOrDefault() is { } check)
                        check.IsChecked = GetNodeCheckState(node);
                    Update(item);
                }
            }
            Update(tree);
        }
        finally { updatingChecks = false; }
    }

    private void OnCheckChanged(W.CheckBox check, bool isChecked)
    {
        if (updatingChecks || check.Tag is not CatalogTreeNode node) return;
        if (!SetNodeChecked(node, isChecked)) RefreshSelectionVisuals();
    }

    private bool? GetNodeCheckState(CatalogTreeNode node)
    {
        var ids = CatalogTreeModel.GetPositionIds(node);
        int selected = ids.Count(selectedIds.Contains);
        return selected == 0 ? false : selected == ids.Count ? true : null;
    }

    private void ExpandAll(bool expand)
    {
        void Apply(W.ItemsControl parent)
        {
            foreach (W.TreeViewItem item in parent.Items)
            {
                item.IsExpanded = expand;
                Apply(item);
            }
        }
        Apply(tree);
    }
}
