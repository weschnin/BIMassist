using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
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

    private readonly Action<AvaQuantityViewSettings>? postSettings;
    private readonly CheckBox visibleOnly;
    private readonly ComboBox useRevitId;
    private readonly string? initialGrouping;
    private bool suppressSettings = true;
    private bool hasRun;
    private readonly List<(DependencyPropertyDescriptor Descriptor, DataGridColumn Column, EventHandler Handler)> columnObservers = [];

    internal AvaQuantityRunWindow(AvaQuantityRun run, IReadOnlyList<GroupingParameterOption> groupingOptions,
        Action<IReadOnlyList<string>, bool> postSelection, Action<bool, bool> postX31, Action postX82,
        Action<GroupingParameterOption?> postGrouping, Action<bool> postRecalculate, bool actionsEnabled = true,
        bool selectionActionsEnabled = true, bool x31ExportEnabled = false, bool x82ExportEnabled = false,
        AvaQuantityViewSettings? settings = null, Action<AvaQuantityViewSettings>? postSettings = null,
        Action<bool>? postMissing = null, Action<AvaQuantityTable>? postExcel = null)
    {
        this.actionsEnabled = actionsEnabled;
        this.postSettings = postSettings;
        initialGrouping = settings?.Grouping;
        this.groupingOptions = groupingOptions;

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
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        heading.Children.Add(new TextBlock { Text = "Mengenermittlung", FontSize = 18, FontWeight = FontWeights.SemiBold });
        runHeading = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = run.ProjectPath,
            Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 0, 0)
        };
        heading.Children.Add(runHeading);
        const string missingExplanation = "Fehlende Elemente: Kandidaten in der aktiven Ansicht, die im aktuellen Lauf nicht enthalten sind (nicht zwingend sichtbar).";
        heading.Children.Add(new TextBlock { Text = missingExplanation, TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetRow(heading, 0);
        layout.Children.Add(heading);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        toolbar.Children.Add(new TextBlock { Text = "Gruppierung:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        groupingSelector = new ComboBox { MinWidth = 230, MinHeight = 28, Margin = new Thickness(0, 0, 12, 0), DisplayMemberPath = "DisplayName",
            IsEnabled = true };

        groupingSelector.SelectionChanged += (_, _) =>
        {
            if (!initializingGrouping && groupingSelector.SelectedItem is GroupingChoice choice)
            {
                ApplyGrouping(choice);
                this.postGrouping(choice.Option);
                SettingsChanged();
            }
        };
        toolbar.Children.Add(groupingSelector);

        useRevitId = new ComboBox { MinWidth = 220, MinHeight = 28, Margin = new Thickness(0, 0, 8, 0) };
        useRevitId.Items.Add("X31-Bemerkung: BIMassistID");
        useRevitId.Items.Add("X31-Bemerkung: Revit-ElementID");
        useRevitId.SelectedIndex = 0;
        visibleOnly = new CheckBox { Content = "Nur sichtbare Elemente berücksichtigen", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), IsThreeState = false, IsChecked = run.VisibleOnly };

        var recalculateButton = Button("Neu berechnen", () => this.postRecalculate(visibleOnly.IsChecked == true), 112);
        var selectButton = Button("Selektieren", () => ApplySelection(false), 100);
        var isolateButton = Button("Isolieren", () => ApplySelection(true), 100);
        var x31Button = Button("X31 exportieren", () => this.postX31(useRevitId.SelectedIndex == 1, false), 120);
        var x82Button = Button("X82 exportieren", this.postX82, 120);
        var selectMissing = Button("Fehlende selektieren", () => postMissing?.Invoke(false), 145);
        var isolateMissing = Button("Fehlende isolieren", () => postMissing?.Invoke(true), 140);
        selectMissing.ToolTip = missingExplanation;
        isolateMissing.ToolTip = missingExplanation;
        selectMissing.IsEnabled = selectionActionsEnabled && postMissing != null;
        isolateMissing.IsEnabled = selectionActionsEnabled && postMissing != null;
        toolbar.Children.Add(recalculateButton);
        toolbar.Children.Add(selectButton);
        toolbar.Children.Add(isolateButton);
        toolbar.Children.Add(selectMissing);
        toolbar.Children.Add(isolateMissing);
        toolbar.Children.Add(visibleOnly);
        var exports = new WrapPanel { Margin = new Thickness(0, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        exports.Children.Add(useRevitId);
        exports.Children.Add(x31Button);
        exports.Children.Add(x82Button);
        var excelButton = Button("Excel exportieren", () =>
        {
            var table = CaptureTable();
            if (table.Columns.Count == 0) { ShowStatus("Keine sichtbaren Spalten für den Excel-Export. Spaltenauswahl per Rechtsklick in die Tabelle öffnen."); return; }
            if (table.Rows.Count == 0) { ShowStatus("Keine Datensätze für den Excel-Export vorhanden."); return; }
            postExcel?.Invoke(table);
        }, 120);
        excelButton.IsEnabled = postExcel != null;
        exports.Children.Add(excelButton);
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
        Grid.SetRow(exports, 2);
        layout.Children.Add(exports);

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
        // Remain recoverable even after the user hides every column header.
        grid.ContextMenu = columnMenu;
        rowsView = CollectionViewSource.GetDefaultView(rows);
        grid.ItemsSource = rowsView;
        Grid.SetRow(grid, 3);
        layout.Children.Add(grid);

        status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(status, 4);
        layout.Children.Add(status);
        Content = layout;
        SetRun(run);
        RestoreSettings(settings);
        foreach (var column in grid.Columns)
        {
            foreach (var property in new[] { DataGridColumn.WidthProperty, DataGridColumn.VisibilityProperty })
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(property, typeof(DataGridColumn));
                EventHandler handler = (_, _) => SettingsChanged();
                descriptor.AddValueChanged(column, handler);
                columnObservers.Add((descriptor, column, handler));
            }
        }
        grid.ColumnDisplayIndexChanged += (_, _) => SettingsChanged();
        ((INotifyCollectionChanged)rowsView.SortDescriptions).CollectionChanged += (_, _) => SettingsChanged();
        visibleOnly.Checked += (_, _) => SettingsChanged();
        visibleOnly.Unchecked += (_, _) => SettingsChanged();
        useRevitId.SelectionChanged += (_, _) => SettingsChanged();
        Closed += (_, _) =>
        {
            SettingsChanged();
            foreach (var observer in columnObservers)
                observer.Descriptor.RemoveValueChanged(observer.Column, observer.Handler);
            columnObservers.Clear();
        };
        suppressSettings = false;
    }

    internal int GroupingDescriptionCount => rowsView.GroupDescriptions.Count;

    internal void SetRun(AvaQuantityRun run)
    {
        var selectedParameter = hasRun ? SelectedGrouping() : initialGrouping
            ?? run.Records.Select(record => record.GroupingParameter).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        var previousSuppression = suppressSettings;
        suppressSettings = true;
        initializingGrouping = true;
        try
        {
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
            groupingSelector.SelectedItem = options.FirstOrDefault(choice => choice.DisplayName == selectedParameter) ?? options[0];
            ApplyGrouping(groupingSelector.SelectedItem as GroupingChoice);
            hasRun = true;
            status.Text = $"{run.Records.Count} Mengenermittlungsdatensätze im letzten Lauf. Spaltenkopf anklicken zum Sortieren; Rechtsklick in die Tabelle zeigt die Spaltenauswahl."
                + (actionsEnabled ? "" : " Neuberechnung ist in dieser Ansicht noch nicht angeboten.");
        }
        finally
        {
            initializingGrouping = false;
            suppressSettings = previousSuppression;
        }
    }

    private string SelectedGrouping() => groupingSelector.SelectedItem is GroupingChoice choice && choice.GroupingProperty.Length > 0
        ? choice.DisplayName : "";

    internal AvaQuantityViewSettings CaptureSettings() => new()
    {
        Columns = grid.Columns.Select(column => new AvaQuantityColumnSetting(column.SortMemberPath, column.DisplayIndex,
            column.Visibility == Visibility.Visible, DisplayWidth(column))).ToArray(),
        Sorts = rowsView.SortDescriptions.Select(sort => new AvaQuantitySortSetting(sort.PropertyName,
            sort.Direction == ListSortDirection.Descending)).ToArray(),
        Grouping = SelectedGrouping(),
        VisibleOnly = visibleOnly.IsChecked == true,
        X31UseRevitElementId = useRevitId.SelectedIndex == 1
    };

    internal AvaQuantityTable CaptureTable()
    {
        var columns = grid.Columns.Where(column => column.Visibility == Visibility.Visible).OrderBy(column => column.DisplayIndex).ToArray();
        return new AvaQuantityTable(columns.Select(column => new AvaQuantityTableColumn(column.SortMemberPath,
            column.Header?.ToString() ?? "", DisplayWidth(column))).ToArray(),
            rowsView.Cast<RunRow>().Select(row => (IReadOnlyList<object?>)columns.Select(column => row.Cell(column.SortMemberPath)).ToArray()).ToArray());
    }

    private static double DisplayWidth(DataGridColumn column) => column.Width.IsAbsolute ? column.Width.Value
        : double.IsFinite(column.ActualWidth) && column.ActualWidth > 0 ? column.ActualWidth : 100;

    private void SettingsChanged()
    {
        if (!suppressSettings) postSettings?.Invoke(CaptureSettings());
    }

    private void RestoreSettings(AvaQuantityViewSettings? settings)
    {
        if (settings == null) return;
        var columns = grid.Columns.ToDictionary(column => column.SortMemberPath, StringComparer.Ordinal);
        var restored = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<DataGridColumn>();
        foreach (var setting in settings.Columns.OrderBy(column => column.DisplayIndex))
        {
            if (!columns.TryGetValue(setting.Key, out var column) || !restored.Add(setting.Key)) continue;
            column.Visibility = setting.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (double.IsFinite(setting.Width) && setting.Width > 0) column.Width = new DataGridLength(setting.Width);
            order.Add(column);
        }
        order.AddRange(grid.Columns.Where(column => !restored.Contains(column.SortMemberPath)));
        for (var index = 0; index < order.Count; index++) order[index].DisplayIndex = index;
        rowsView.SortDescriptions.Clear();
        foreach (var sort in settings.Sorts)
        {
            if (!columns.TryGetValue(sort.Key, out var column)) continue;
            var direction = sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            rowsView.SortDescriptions.Add(new SortDescription(sort.Key, direction));
            column.SortDirection = direction;
        }
        visibleOnly.IsChecked = settings.VisibleOnly;
        useRevitId.SelectedIndex = settings.X31UseRevitElementId ? 1 : 0;
        if (grid.ColumnHeaderStyle?.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == ContextMenuService.ContextMenuProperty)?.Value is ContextMenu menu)
            foreach (var item in menu.Items.OfType<MenuItem>())
                if (item.Tag is DataGridColumn column) item.IsChecked = column.Visibility == Visibility.Visible;
    }

    private void ApplyGrouping(GroupingChoice? choice)
    {
        rowsView.GroupDescriptions.Clear();
        foreach (var row in rows)
            row.SetGrouping(choice);
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
        SortMemberPath = property,
        Binding = new Binding(property) { Mode = BindingMode.OneWay, StringFormat = property == nameof(RunRow.Result) ? "0.###" : null },
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
        private GroupingChoice? selectedGrouping;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetGrouping(GroupingChoice? choice)
        {
            selectedGrouping = choice;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupingValue)));
        }
        public string BimassistId => record.BimassistId;
        public string Ordinal => record.Ordinal;
        public string Kurztext => record.Kurztext;
        public string Unit => record.Unit;
        public string RevitElementId => record.RevitElementId;
        public string FormulaWithParameters => AvaQuantityDisplay.FormulaWithParameters(record.FormulaWithParameters);
        public string FormulaWithValues => AvaQuantityDisplay.FormulaWithValues(record.FormulaWithValues);

        public string GroupingValue => selectedGrouping?.GroupingProperty switch
        {
            nameof(Ordinal) => Ordinal,
            nameof(FamilyAndType) => FamilyAndType,
            nameof(GroupingValue) => record.GroupingValues.TryGetValue(selectedGrouping.DisplayName, out var value) ? value
                : selectedGrouping.DisplayName == record.GroupingParameter ? record.GroupingValue : "",
            _ => ""
        };
        public string FamilyAndType => record.FamilyName + " — " + record.TypeName;
        public string FamilyName => record.FamilyName;
        public string TypeName => record.TypeName;
        public decimal Result => AvaQuantityDisplay.Round(record.Result);
        public object? Cell(string key) => key switch
        {
            nameof(BimassistId) => BimassistId,
            nameof(Ordinal) => Ordinal,
            nameof(Kurztext) => Kurztext,
            nameof(Unit) => Unit,
            nameof(RevitElementId) => RevitElementId,
            nameof(FormulaWithParameters) => FormulaWithParameters,
            nameof(FormulaWithValues) => FormulaWithValues,
            nameof(GroupingValue) => GroupingValue,
            nameof(FamilyName) => FamilyName,
            nameof(TypeName) => TypeName,
            nameof(Result) => Result,
            _ => throw new InvalidOperationException("Unbekannte Mengenermittlungsspalte: " + key)
        };
    }
}
