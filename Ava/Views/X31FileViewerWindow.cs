using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Views;

/// <summary>Physical X31 file rows only. No Revit document, selection or write callbacks.</summary>
internal sealed class X31FileViewerWindow : Window
{
    private X31FileViewerWindow(X31ReviewDocument document)
    {
        Title = "X31 Viewer – schreibgeschützt";
        Width = 1440; Height = 740; MinWidth = 850; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "X31 Viewer – " + document.SourceName
            + "\nNur Dateiansicht; keine Modellprüfung oder Modelländerung. REB91-Ergebnisse nur für unterstützte Ansätze; leere Felder sind nicht verfügbar.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        layout.Children.Add(heading);
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            CanUserDeleteRows = false, CanUserSortColumns = true, CanUserReorderColumns = true,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, ItemsSource = document.Rows };
        void Column(string header, string property, double width, IValueConverter? converter = null) => grid.Columns.Add(new DataGridTextColumn
        {
            Header = header, Binding = new Binding(property) { Mode = BindingMode.OneWay, Converter = converter },
            SortMemberPath = property, Width = width
        });
        Column("Zeile", nameof(X31ReviewRow.Number), 55);
        Column("OZ", nameof(X31ReviewRow.Position), 110);
        Column("Kurztext", nameof(X31ReviewRow.ShortText), 200);
        Column("Zeilentyp", nameof(X31ReviewRow.Kind), 105, new RowKindConverter());
        Column("Bemerkung", nameof(X31ReviewRow.Comment), 230);
        Column("Formelnummer", nameof(X31ReviewRow.FormulaNumber), 100);
        Column("Berechnungsansatz", nameof(X31ReviewRow.Formula), 260);
        Column("Ergebnis", nameof(X31ReviewRow.Result), 95);
        Column("Einheit", nameof(X31ReviewRow.Unit), 70);
        Column("BVBS-Erklärung", nameof(X31ReviewRow.Explanation), 220);
        Column("REB-Rohzeile", nameof(X31ReviewRow.RawRow), 600);
        Grid.SetRow(grid, 1); layout.Children.Add(grid);
        var warnings = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 130, Margin = new Thickness(0, 8, 0, 0),
            Text = $"{document.Rows.Count} physische REB-Zeilen. " + (document.Warnings.Count == 0 ? "Keine Lesehinweise." : string.Join(Environment.NewLine, document.Warnings.Take(100)))
                + (document.Warnings.Count > 100 ? $"\n… {document.Warnings.Count - 100} weitere Hinweise." : "") };
        Grid.SetRow(warnings, 2); layout.Children.Add(warnings);
        Content = layout;
    }

    internal static X31FileViewerWindow Create(string path, IReadOnlyDictionary<string, X31ReviewPosition>? positions = null)
        => new(X31ReviewReader.ReadFileForViewer(path, positions, X31Writer.EvaluateReb91));

    private sealed class RowKindConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value switch
        {
            X31ReviewRowKind.Quantity => "Menge", X31ReviewRowKind.Continuation => "Fortsetzung", _ => "Kommentar"
        };
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}