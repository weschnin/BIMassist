using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Views;

internal sealed class AvaQuantityRunWindow : Window
{
    private readonly ObservableCollection<RunRow> rows = [];
    private readonly ICollectionView rowsView;
    private readonly Action<IReadOnlyList<string>, bool> postSelection;
    private readonly Action<bool, bool> postX31;
    private readonly Action postX82;
    private readonly Action<GroupingParameterOption?> postGrouping;
    private readonly Action<bool> postRecalculate;
    private readonly TextBlock status;
    private readonly DataGrid grid;
    private readonly ComboBox groupingSelector;
    private readonly TextBlock runHeading;
    private readonly IReadOnlyList<GroupingParameterOption> groupingOptions;
    private bool initializingGrouping;
    private readonly bool actionsEnabled;
    private readonly bool x31ExportEnabled;
    private readonly bool x82ExportEnabled;

    internal AvaQuantityRunWindow(AvaQuantityRun run, IReadOnlyList<GroupingParameterOption> groupingOptions,
        Action<IReadOnlyList<string>, bool> postSelection, Action<bool, bool> postX31, Action postX82,
        Action<GroupingParameterOption?> postGrouping, Action<bool> postRecalculate, bool actionsEnabled = true,
        bool selectionActionsEnabled = true, bool x31ExportEnabled = false, bool x82ExportEnabled = false)
    {
        this.actionsEnabled = actionsEnabled;
        this.groupingOptions = groupingOptions;
        this.x31ExportEnabled = x31ExportEnabled;
        this.x82ExportEnabled = x82ExportEnabled;
        this.postSelection = postSelection ?? throw new ArgumentNullException(nameof(postSelection));
        this.postX31 = postX31 ?? throw new ArgumentNullException(nameof(postX31));
        this.postX82 = postX82 ?? throw new ArgumentNullException(nameof(postX82));
        this.postGrouping = postGrouping ?? throw new ArgumentNullException(nameof(postGrouping));
        this.postRecalculate = postRecalculate ?? throw new ArgumentNullException(nameof(postRecalculate));
        Title = "Mengenermittlung";
        Width = 1560;
        Height = 760;
        MinWidth = 900;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        heading.Children.Add(new TextBlock { Text = "Mengenermittlung", FontSize = 18, FontWeight = FontWeights.SemiBold });
        runHeading = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = run.ProjectPath,
            Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 3, 0, 0)
        };
        heading.Children.Add(runHeading);
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(new TextBlock { Text = "Gruppierungsparameter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        groupingSelector = new ComboBox { MinWidth = 230, MinHeight = 28, Margin = new Thickness(0, 0, 12, 0), DisplayMemberPath = "DisplayName",
            IsEnabled = true };

        groupingSelector.SelectionChanged += (_, _) =>
        {
            if (!initializingGrouping && groupingSelector.SelectedItem is GroupingChoice choice)
            {
                ApplyGrouping(choice);
                this.postGrouping(choice.Option);
            }
        };
        toolbar.Children.Add(groupingSelector);

        var useRevitId = new ComboBox { MinWidth = 220, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0) };
        useRevitId.Items.Add("X31-Bemerkung: BIMassistID");
        useRevitId.Items.Add("X31-Bemerkung: Revit-ElementID");
        useRevitId.SelectedIndex = 0;
        var visibleOnly = new CheckBox { Content = "Nur sichtbare Elemente berücksichtigen", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), IsThreeState = false };
        toolbar.Children.Add(useRevitId);

        var recalculateButton = Button("Neu berechnen", () => this.postRecalculate(visibleOnly.IsChecked == true), 112);
        var selectButton = Button("Selektieren", () => ApplySelection(false), 100);
        var isolateButton = Button("Isolieren", () => ApplySelection(true), 100);
        var x31Button = Button("X31 exportieren", () => this.postX31(useRevitId.SelectedIndex == 1, false), 120);
        var x82Button = Button("X82 exportieren", this.postX82, 120);
        toolbar.Children.Add(recalculateButton);
        toolbar.Children.Add(selectButton);
        toolbar.Children.Add(isolateButton);
        toolbar.Children.Add(x31Button);
        toolbar.Children.Add(x82Button);
        toolbar.Children.Add(visibleOnly);
        if (!actionsEnabled)
        {
            recalculateButton.IsEnabled = false;
            x31Button.IsEnabled = x31ExportEnabled;
            x82Button.IsEnabled = x82ExportEnabled;
            visibleOnly.IsEnabled = false;
            toolbar.ToolTip = "Neuberechnung wird in dieser Prüfansicht noch nicht angeboten.";
        }
        if (!selectionActionsEnabled)
        {
            selectButton.IsEnabled = false;
            isolateButton.IsEnabled = false;
        }
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);

        grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            CanUserReorderColumns = true,
            CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            AlternationCount = 2,
            Margin = new Thickness(0, 0, 0, 8)
        };
        AddColumn("ID", nameof(RunRow.BimassistId), 105);
        AddColumn("OZ", nameof(RunRow.Ordinal), 100);
        AddColumn("Kurztext", nameof(RunRow.Kurztext), 220);
        AddColumn("Einheit", nameof(RunRow.Unit), 75);
        AddColumn("Revit-ElementID", nameof(RunRow.RevitElementId), 130);
        AddColumn("Formel mit Parametern", nameof(RunRow.FormulaWithParameters), 260);
        AddColumn("Formel mit Werten", nameof(RunRow.FormulaWithValues), 280);
        AddColumn("Gruppierungsparameter", nameof(RunRow.GroupingParameter), 160);
        AddColumn("Gruppierungswert", nameof(RunRow.GroupingValue), 140);
        AddColumn("Familie", nameof(RunRow.FamilyName), 160);
        AddColumn("Typ", nameof(RunRow.TypeName), 160);
        AddColumn("Ergebnis", nameof(RunRow.Result), 100);
        var columnMenu = new ContextMenu();
        foreach (var column in grid.Columns)
        {
            var item = new MenuItem { Header = column.Header, IsCheckable = true, IsChecked = true, Tag = column };
            item.Click += (_, _) =>
            {
                if (item.Tag is DataGridColumn selectedColumn)
                    selectedColumn.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            };
            columnMenu.Items.Add(item);
        }
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(ContextMenuService.ContextMenuProperty, columnMenu));
        grid.ColumnHeaderStyle = headerStyle;
        rowsView = CollectionViewSource.GetDefaultView(rows);
        grid.ItemsSource = rowsView;
        Grid.SetRow(grid, 2);
        layout.Children.Add(grid);

        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(status, 3);
        layout.Children.Add(status);
        Content = layout;
        SetRun(run);
    }

    internal int GroupingDescriptionCount => rowsView.GroupDescriptions.Count;

    internal void SetRun(AvaQuantityRun run)
    {
        initializingGrouping = true;
        runHeading.Text = $"Projekt: {run.ProjectTitle}    Letzter Lauf: {run.CreatedUtc.ToLocalTime():g}    Umfang: {run.ScopeDescription}";
        runHeading.ToolTip = run.ProjectPath;
        var options = new List<GroupingChoice>
        {
            new("Keine Gruppierung", null, ""),
            new("OZ", null, nameof(RunRow.Ordinal)),
            new("Familie und Typ", null, nameof(RunRow.FamilyAndType))
        };
        foreach (var label in run.Records.SelectMany(record => record.GroupingValues.Keys.Append(record.GroupingParameter))
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "Familie und Typ").Distinct(StringComparer.Ordinal))
            options.Add(new GroupingChoice(label, groupingOptions.FirstOrDefault(option => option.DisplayLabel == label), nameof(RunRow.GroupingValue)));
        groupingSelector.ItemsSource = options;
        rows.Clear();
        foreach (var record in run.Records) rows.Add(new RunRow(record));
        var selectedParameter = run.Records.Select(record => record.GroupingParameter).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (selectedParameter != null && groupingSelector.ItemsSource is IEnumerable<GroupingChoice> choices)
        {
            var available = choices.ToList();
            var savedChoice = available.FirstOrDefault(choice =>
                string.Equals(choice.Option?.DisplayLabel, selectedParameter, StringComparison.Ordinal)
                || string.Equals(choice.DisplayName, selectedParameter, StringComparison.Ordinal));
            if (savedChoice == null && selectedParameter != "Familie und Typ")
            {
                savedChoice = new GroupingChoice(selectedParameter, null, nameof(RunRow.GroupingValue));
                available.Add(savedChoice);
                groupingSelector.ItemsSource = available;
            }
            groupingSelector.SelectedItem = savedChoice ?? available[0];
            ApplyGrouping(groupingSelector.SelectedItem as GroupingChoice);
        }
        else
        {
            groupingSelector.SelectedIndex = 0;
            ApplyGrouping(groupingSelector.SelectedItem as GroupingChoice);
        }
        initializingGrouping = false;
        status.Text = $"{run.Records.Count} Mengenermittlungsdatensätze im letzten Lauf. Spaltenkopf anklicken zum Sortieren; Rechtsklick auf den Spaltenkopf zeigt die Spaltenauswahl."
            + (actionsEnabled ? "" : " Neuberechnung ist in dieser Ansicht noch nicht angeboten.");
    }

    private void ApplyGrouping(GroupingChoice? choice)
    {
        rowsView.GroupDescriptions.Clear();
        foreach (var row in rows)
            row.SetGrouping(choice?.GroupingProperty == nameof(RunRow.GroupingValue) ? choice.DisplayName : null);
        if (!string.IsNullOrWhiteSpace(choice?.GroupingProperty))
            rowsView.GroupDescriptions.Add(new PropertyGroupDescription(choice.GroupingProperty));
        rowsView.Refresh();
    }

    internal void ShowStatus(string message)
    {
        if (Dispatcher.CheckAccess()) status.Text = message;
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(new Action(() => status.Text = message));
    }

    private void ApplySelection(bool isolate)
    {
        var selected = grid.SelectedItems.OfType<RunRow>().Select(row => row.RevitElementId)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) { ShowStatus("Keine Zeilen mit Revit-ElementID ausgewählt."); return; }
        postSelection(selected, isolate);
    }

    private void AddColumn(string header, string property, double width) => grid.Columns.Add(new DataGridTextColumn
    {
        Header = header,
        Binding = new Binding(property) { Mode = BindingMode.OneWay },
        Width = new DataGridLength(width)
    });

    private static Button Button(string text, Action click, double width)
    {
        var button = new Button { Content = text, MinWidth = width, MinHeight = 30, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2) };
        button.Click += (_, _) => click();
        return button;
    }

    private sealed record GroupingChoice(string DisplayName, GroupingParameterOption? Option, string GroupingProperty);

    private sealed class RunRow(AvaQuantityRecord record) : INotifyPropertyChanged
    {
        private string? selectedGrouping;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetGrouping(string? label)
        {
            selectedGrouping = label;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupingParameter)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupingValue)));
        }
        public string BimassistId => record.BimassistId;
        public string Ordinal => record.Ordinal;
        public string Kurztext => record.Kurztext;
        public string Unit => record.Unit;
        public string RevitElementId => record.RevitElementId;
        public string FormulaWithParameters => record.FormulaWithParameters;
        public string FormulaWithValues => record.FormulaWithValues;
        public string GroupingParameter => selectedGrouping ?? record.GroupingParameter;
        public string GroupingValue => selectedGrouping == null ? record.GroupingValue
            : record.GroupingValues.TryGetValue(selectedGrouping, out var value) ? value
                : selectedGrouping == record.GroupingParameter ? record.GroupingValue : "";
        public string FamilyAndType => record.FamilyName + " — " + record.TypeName;
        public string FamilyName => record.FamilyName;
        public string TypeName => record.TypeName;
        public decimal Result => record.Result;
    }
}
