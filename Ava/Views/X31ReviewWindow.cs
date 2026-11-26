using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BIMassist.Ava.Core;
using WpfBrushes = System.Windows.Media.Brushes;

namespace BIMassist.Ava.Views;

/// <summary>Modeless table for reviewing an exported X31 file. Revit work is posted through the injected boundary.</summary>
internal sealed class X31ReviewWindow : Window
{
    private readonly Action<X31SelectionPlan, bool> postSelectionRequest;
    private readonly TextBlock statusText;
    internal DataGrid ReviewGrid { get; }
    internal Button SelectButton { get; }
    internal Button IsolateButton { get; }
    internal Button SelectMissingButton { get; }
    internal Button IsolateMissingButton { get; }

    internal X31ReviewWindow(X31ReviewDocument document, Action<X31SelectionPlan, bool> postSelectionRequest,
        Action<bool>? postMissingRequest = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.postSelectionRequest = postSelectionRequest ?? throw new ArgumentNullException(nameof(postSelectionRequest));
        Title = "Prüfung der Mengenermittlung";
        Width = 1500;
        Height = 680;
        MinWidth = 760;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock
        {
            Text = "Prüfung der Mengenermittlung",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        header.Children.Add(new TextBlock
        {
            Text = "Datei: " + document.SourceName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = document.SourceName,
            Margin = new Thickness(0, 0, 0, 6)
        });
        header.Children.Add(new TextBlock
        {
            Text = "ElementIDs werden im aktuell aktiven Revit-Dokument aufgelöst."
                + (document.TemplateSource.Length == 0 ? "" : "\nLV-Angaben ergänzt aus: " + document.TemplateSource),
            TextWrapping = TextWrapping.Wrap,
            Foreground = WpfBrushes.DimGray,
            Margin = new Thickness(0, 0, 0, 6)
        });
        if (document.Warnings.Count > 0)
        {
            var shown = string.Join(Environment.NewLine, document.Warnings.Take(3));
            if (document.Warnings.Count > 3) shown += $"{Environment.NewLine}… und {document.Warnings.Count - 3} weitere Hinweise.";
            header.Children.Add(new TextBlock
            {
                Text = $"Hinweise ({document.Warnings.Count}):{Environment.NewLine}{shown}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = WpfBrushes.DarkGoldenrod,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        SelectButton = new Button { Content = "Selektieren", MinWidth = 110, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0) };
        IsolateButton = new Button { Content = "Isolieren", MinWidth = 110, MinHeight = 30 };
        SelectButton.Click += (_, _) => ApplySelectedRows(false);
        IsolateButton.Click += (_, _) => ApplySelectedRows(true);
        actionPanel.Children.Add(SelectButton);
        actionPanel.Children.Add(IsolateButton);
        header.Children.Add(actionPanel);
        var missingPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        SelectMissingButton = new Button { Content = "Nicht enthaltene Elemente selektieren", MinHeight = 30,
            Margin = new Thickness(0, 0, 8, 0), IsEnabled = postMissingRequest != null };
        IsolateMissingButton = new Button { Content = "Nicht enthaltene Elemente isolieren", MinHeight = 30,
            IsEnabled = postMissingRequest != null };
        SelectMissingButton.Click += (_, _) => postMissingRequest?.Invoke(false);
        IsolateMissingButton.Click += (_, _) => postMissingRequest?.Invoke(true);
        missingPanel.Children.Add(SelectMissingButton);
        missingPanel.Children.Add(IsolateMissingButton);
        header.Children.Add(new TextBlock { Text = "Modellelemente im Ansichtsbereich (Bounding-Box geprüft, nicht pixelgenau), die in keiner X31-Zeile stehen (ohne Zeilenauswahl):",
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        header.Children.Add(missingPanel);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        ReviewGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            ItemsSource = document.Rows,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = true,
            CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            AlternationCount = 2,
            Margin = new Thickness(0, 0, 0, 8)
        };
        ReviewGrid.Columns.Add(TextColumn("OZ", nameof(X31ReviewRow.Position), 125));
        ReviewGrid.Columns.Add(TextColumn("Kurztext", nameof(X31ReviewRow.ShortText), 260));
        ReviewGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Zeilentyp",
            Binding = new Binding(nameof(X31ReviewRow.Kind)) { Converter = new RowKindConverter() },
            Width = new DataGridLength(115)
        });
        ReviewGrid.Columns.Add(TextColumn("Bemerkung", nameof(X31ReviewRow.Comment), 280));
        ReviewGrid.Columns.Add(TextColumn("Formelnummer", nameof(X31ReviewRow.FormulaNumber), 100));
        ReviewGrid.Columns.Add(TextColumn("Berechnungsansatz", nameof(X31ReviewRow.Formula), 280));
        ReviewGrid.Columns.Add(TextColumn("Ergebnis", nameof(X31ReviewRow.Result), 110));
        ReviewGrid.Columns.Add(TextColumn("Einheit", nameof(X31ReviewRow.Unit), 75));
        var groupStyle = new Style(typeof(DataGridRow));
        groupStyle.Triggers.Add(new DataTrigger
        {
            Binding = new Binding(nameof(X31ReviewRow.Kind)),
            Value = X31ReviewRowKind.GroupComment,
            Setters = { new Setter(Control.FontWeightProperty, FontWeights.SemiBold), new Setter(Control.BackgroundProperty, WpfBrushes.LemonChiffon) }
        });
        ReviewGrid.RowStyle = groupStyle;
        ReviewGrid.SelectionChanged += (_, _) =>
            ShowStatus(ReviewGrid.SelectedItems.Count == 0
                ? "Keine Zeile ausgewählt. Wählen Sie Zeilen und klicken Sie auf Selektieren oder Isolieren."
                : $"{ReviewGrid.SelectedItems.Count} Zeile(n) ausgewählt. Klicken Sie auf Selektieren oder Isolieren.");
        Grid.SetRow(ReviewGrid, 1);
        layout.Children.Add(ReviewGrid);

        statusText = new TextBlock
        {
            Text = $"{document.Rows.Count} X31-Zeilen geladen. Mehrfachauswahl mit Strg/Umschalt möglich.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(statusText, 2);
        layout.Children.Add(statusText);
        Content = layout;
    }

    internal void ShowStatus(string message)
    {
        if (Dispatcher.CheckAccess()) statusText.Text = message;
        else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(new Action(() => statusText.Text = message));
    }

    private void ApplySelectedRows(bool isolate)
    {
        var selected = ReviewGrid.SelectedItems.OfType<X31ReviewRow>().ToArray();
        if (selected.Length == 0)
        {
            ShowStatus("Keine Zeile ausgewählt; die aktuelle Revit-Auswahl bleibt unverändert.");
            return;
        }
        var plan = X31ElementSelectionPlanner.FromRows(selected);
        if (plan.ElementIds.Count == 0)
        {
            ShowStatus("Für die ausgewählten Zeilen ist keine gültige Revit-ElementID vorhanden.");
            return;
        }
        if (isolate && plan.SelectionIncomplete)
            ShowStatus("Die Zuordnung ist unvollständig. Es werden nur vorhandene IDs selektiert; eine Teil-Isolation wird verhindert.");
        postSelectionRequest(plan, isolate);
    }

    private static DataGridTextColumn TextColumn(string header, string path, double width) => new()
    {
        Header = header,
        Binding = new Binding(path),
        Width = width == 2 ? new DataGridLength(2, DataGridLengthUnitType.Star) : new DataGridLength(width)
    };


    private sealed class RowKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is X31ReviewRowKind kind ? kind switch
            {
                X31ReviewRowKind.Quantity => "Menge",
                X31ReviewRowKind.GroupComment => "Bereich / Gewerk",
                X31ReviewRowKind.ElementIdComment => "ElementID-Kommentar",
                X31ReviewRowKind.Continuation => "Fortsetzung",
                _ => "Kommentar"
            } : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
