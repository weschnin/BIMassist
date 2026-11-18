using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BIMassist.Ava.Core;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace BIMassist.Ava.Views;

internal sealed class LvX82ViewerWindow : Window
{
    private static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";
    private readonly TextBox longText;
    internal TreeView PositionTree { get; }
    internal TextBox LongTextView => longText;

    private LvX82ViewerWindow(string path, IReadOnlyList<ViewerBoq> boqs)
    {
        Title = "AVA – GAEB-LV-Viewer";
        Width = 1100;
        Height = 720;
        MinWidth = 780;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = "GAEB-LV-Viewer", FontSize = 18, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path, Foreground = WpfBrushes.DimGray });
        Grid.SetRow(header, 0); layout.Children.Add(header);

        var columnHeader = new Grid { MinWidth = 900, Background = WpfBrushes.Gainsboro };
        columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        columnHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
        AddCell(columnHeader, "OZ", 0, FontWeights.SemiBold);
        AddCell(columnHeader, "Kurztext", 1, FontWeights.SemiBold);
        AddCell(columnHeader, "Menge", 2, FontWeights.SemiBold);
        AddCell(columnHeader, "Einheit", 3, FontWeights.SemiBold);
        Grid.SetRow(columnHeader, 1); layout.Children.Add(columnHeader);

        PositionTree = new TreeView { BorderThickness = new Thickness(1), BorderBrush = WpfBrushes.LightGray };
        foreach (var boq in boqs)
        {
            var root = new TreeViewItem { Header = boq.Title, IsExpanded = true };
            foreach (var child in boq.Children) root.Items.Add(BuildNode(child));
            PositionTree.Items.Add(root);
        }
        Grid.SetRow(PositionTree, 2); layout.Children.Add(PositionTree);

        var label = new TextBlock { Text = "Langtext", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        Grid.SetRow(label, 3); layout.Children.Add(label);
        longText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8),
            FontFamily = new WpfFontFamily("Segoe UI")
        };
        PositionTree.SelectedItemChanged += (_, args) => longText.Text = args.NewValue is TreeViewItem selected && selected.Tag is ViewerPosition position
            ? position.Description : string.Empty;
        Grid.SetRow(longText, 4); layout.Children.Add(longText);
        Content = layout;
    }

    public static void Show(string path, IntPtr ownerHandle)
    {
        var window = Create(path);
        if (ownerHandle != IntPtr.Zero) new WindowInteropHelper(window).Owner = ownerHandle;
        window.Show();
    }

    internal static LvX82ViewerWindow Create(string path) => new(path, Read(path));

    private static IReadOnlyList<ViewerBoq> Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > CatalogReaders.MaxBytes) throw new InvalidDataException("Die X82-Datei fehlt oder überschreitet 32 MiB.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = CatalogReaders.MaxBytes, MaxCharactersFromEntities = 0 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != Ns + "GAEB") throw new InvalidDataException("Der LV-Viewer unterstützt GAEB DA82/3.3.");
        var result = new List<ViewerBoq>();
        foreach (var boq in document.Descendants(Ns + "BoQ"))
        {
            var id = (string?)boq.Attribute("ID") ?? "";
            var name = ReadText(boq.Element(Ns + "BoQInfo")?.Element(Ns + "Name"));
            var root = new ViewerBoq("LV " + (string.IsNullOrWhiteSpace(name) ? id : name));
            foreach (var body in boq.Elements(Ns + "BoQBody")) AddBody(body, root.Children);
            result.Add(root);
        }
        if (result.Count == 0) throw new InvalidDataException("Die X82-Datei enthält kein LV.");
        return result;
    }

    private static void AddBody(XElement body, List<ViewerNode> target)
    {
        foreach (var child in body.Elements())
        {
            if (child.Name == Ns + "BoQCtgy")
            {
                var part = (string?)child.Attribute("RNoPart") ?? "";
                var label = child.Elements().Where(element => element.Name == Ns + "LblTx" || element.Name == Ns + "OutlineText" || element.Name == Ns + "DetailTxt")
                    .Select(ReadText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";
                var category = new ViewerNode(part + (label.Length == 0 ? "" : " " + label));
                foreach (var nested in child.Elements(Ns + "BoQBody")) AddBody(nested, category.Children);
                target.Add(category);
            }
            else if (child.Name == Ns + "Itemlist")
            {
                foreach (var item in child.Elements(Ns + "Item"))
                {
                    var ordinal = string.Join(".", item.Ancestors(Ns + "BoQCtgy").Reverse()
                        .Select(category => (string?)category.Attribute("RNoPart") ?? "")
                        .Append((string?)item.Attribute("RNoPart") ?? "")
                        .Append((string?)item.Attribute("RNoIndex") ?? "")
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                    var shortText = ReadText(item.Descendants(Ns + "OutlineText").FirstOrDefault());
                    var description = ReadText(item.Descendants(Ns + "DetailTxt").FirstOrDefault());
                    var quantityText = item.Element(Ns + "Qty")?.Value
                        ?? item.Element(Ns + "QtyDeterm")?.Element(Ns + "Qty")?.Value ?? "";
                    var quantity = decimal.TryParse(quantityText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                        ? value.ToString("0.###", CultureInfo.CurrentCulture) : quantityText;
                    var unit = item.Element(Ns + "QU")?.Value ?? "";
                    target.Add(new ViewerNode(new ViewerPosition(ordinal, shortText, quantity, unit, description)));
                }
            }
        }
    }

    private static TreeViewItem BuildNode(ViewerNode node)
    {
        var item = new TreeViewItem { IsExpanded = true };
        if (node.Position is { } position)
        {
            item.Tag = position;
            var row = new Grid { MinWidth = 900 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(75) });
            AddCell(row, position.Ordinal, 0, FontWeights.SemiBold);
            AddCell(row, position.ShortText, 1, FontWeights.Normal);
            AddCell(row, position.Quantity, 2, FontWeights.Normal);
            AddCell(row, position.Unit, 3, FontWeights.Normal);
            item.Header = row;
        }
        else item.Header = node.Label;
        foreach (var child in node.Children) item.Items.Add(BuildNode(child));
        return item;
    }

    private static void AddCell(Grid grid, string text, int column, FontWeight weight)
    {
        var cell = new TextBlock { Text = text, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text, Margin = new Thickness(3, 2, 12, 2), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(cell, column); grid.Children.Add(cell);
    }

    private static string ReadText(XElement? element)
    {
        if (element == null) return "";
        string ReadNode(XNode node) => node switch
        {
            XText text => text.Value,
            XElement child when child.Name.LocalName == "br" => "\n",
            XElement child => (child.Name.LocalName is "p" or "div" ? "\n" : "") + string.Concat(child.Nodes().Select(ReadNode)) + (child.Name.LocalName is "p" or "div" ? "\n" : ""),
            _ => ""
        };
        var raw = string.Concat(element.Nodes().Select(ReadNode)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return string.Join(Environment.NewLine, raw.Split('\n').Select(line => string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(line => line.Length > 0));
    }

    private sealed record ViewerBoq(string Title) { public List<ViewerNode> Children { get; } = []; }
    private sealed record ViewerNode(string Label)
    {
        public ViewerNode(ViewerPosition position) : this(position.Ordinal + " " + position.ShortText + "    " + position.Quantity + " " + position.Unit) => Position = position;
        public ViewerPosition? Position { get; }
        public List<ViewerNode> Children { get; } = [];
    }
    private sealed record ViewerPosition(string Ordinal, string ShortText, string Quantity, string Unit, string Description);
}
