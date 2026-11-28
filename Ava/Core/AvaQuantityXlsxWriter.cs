using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BIMassist.Ava.Core;

/// <summary>Creates a new, offline OOXML workbook; no Office/Python/runtime package dependency.</summary>
public static class AvaQuantityXlsxWriter
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace C = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly Regex SpreadsheetEscape = new(@"_(?=x[0-9a-fA-F]{4}_)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Never replaces an existing destination. Writes, closes and validates a sibling temporary file before publishing it.</summary>
    public static void Save(string path, AvaQuantityTable table)
    {
        table = SnapshotAndValidate(table);
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, ".ava-quantity-" + Guid.NewGuid().ToString("N") + ".tmp");
        var parts = CreateParts(table);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                    foreach (var (name, xml) in parts)
                    {
                        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                        xml.Save(stream, SaveOptions.DisableFormatting);
                    }
                file.Flush(flushToDisk: true);
            }
            ValidatePackage(temporary, parts);
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static AvaQuantityTable SnapshotAndValidate(AvaQuantityTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Columns == null || table.Rows == null || table.Columns.Count is < 1 or > 16384 || table.Rows.Count > 1048575)
            throw new InvalidDataException("XLSX benötigt 1–16384 Spalten und höchstens 1048575 Datenzeilen.");
        var columns = table.Columns.ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column == null || string.IsNullOrWhiteSpace(column.Key) || !keys.Add(column.Key)
                || column.Header == null || !double.IsFinite(column.Width) || column.Width < 1 || column.Width > 10000)
                throw new InvalidDataException("Ungültige oder doppelte XLSX-Spalte.");
            ValidateText(column.Header);
        }
        var rows = new List<IReadOnlyList<object?>>(table.Rows.Count);
        foreach (var sourceRow in table.Rows)
        {
            if (sourceRow == null || sourceRow.Count != columns.Length)
                throw new InvalidDataException("Jede XLSX-Zeile muss genau einen Wert je Spalte enthalten.");
            var row = sourceRow.ToArray();
            for (int index = 0; index < row.Length; index++)
            {
                var value = row[index];
                if (IsResult(columns[index]) && value != null && NumericText(value) == null)
                    throw new InvalidDataException("Ergebnis muss eine endliche Zahl oder leer sein.");
                ValidateText(ScalarText(value));
            }
            rows.Add(row);
        }
        return new(columns, rows);
    }

    private static bool IsResult(AvaQuantityTableColumn column) => column.Key is "Result" or "Ergebnis";

    private static string? NumericText(object value) => value switch
    {
        decimal number => AvaQuantityDisplay.Round(number).ToString("0.###", CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => Math.Round(number, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture),
        float number when float.IsFinite(number) => Math.Round((double)number, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture),
        byte or sbyte or short or ushort or int or uint or long or ulong => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        double or float => throw new InvalidDataException("Nicht-endliche XLSX-Zahl."),
        _ => null
    };

    private static string ScalarText(object? value) => value switch
    {
        null => "",
        string text => text,
        char character => character.ToString(),
        bool boolean => boolean.ToString(),
        decimal or double or float or byte or sbyte or short or ushort or int or uint or long or ulong
            when NumericText(value) != null => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidDataException("Nicht unterstützter XLSX-Zelltyp.")
    };

    private static void ValidateText(string value)
    {
        if (value.Length > 32767) throw new InvalidDataException("XLSX-Zelltext überschreitet 32767 Zeichen.");
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException ex) { throw new InvalidDataException("Ungültiges XML-Zeichen im XLSX-Zelltext.", ex); }
    }

    private static Dictionary<string, XDocument> CreateParts(AvaQuantityTable table)
    {
        var last = CellReference(table.Columns.Count - 1, table.Rows.Count + 1);
        var data = new XElement(S + "sheetData",
            new XElement(S + "row", new XAttribute("r", 1), table.Columns.Select((column, index) => TextCell(CellReference(index, 1), column.Header, 1))));
        for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = new XElement(S + "row", new XAttribute("r", rowIndex + 2));
            for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var value = table.Rows[rowIndex][columnIndex];
                var reference = CellReference(columnIndex, rowIndex + 2);
                row.Add(IsResult(table.Columns[columnIndex]) && value != null
                    ? new XElement(S + "c", new XAttribute("r", reference), new XAttribute("s", 2), new XAttribute("t", "n"),
                        new XElement(S + "v", NumericText(value)!))
                    : TextCell(reference, ScalarText(value), 0));
            }
            data.Add(row);
        }
        return new(StringComparer.Ordinal)
        {
            ["[Content_Types].xml"] = new(new XElement(C + "Types",
                new XElement(C + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(C + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                ContentType("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"),
                ContentType("/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
                ContentType("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))),
            ["_rels/.rels"] = new(new XElement(P + "Relationships", Relationship("rId1", "officeDocument", "xl/workbook.xml"))),
            ["xl/workbook.xml"] = new(new XElement(S + "workbook", new XAttribute(XNamespace.Xmlns + "r", R),
                new XElement(S + "sheets", new XElement(S + "sheet", new XAttribute("name", "Mengenermittlung"), new XAttribute("sheetId", 1), new XAttribute(R + "id", "rId1"))))),
            ["xl/_rels/workbook.xml.rels"] = new(new XElement(P + "Relationships",
                Relationship("rId1", "worksheet", "worksheets/sheet1.xml"), Relationship("rId2", "styles", "styles.xml"))),
            ["xl/styles.xml"] = Styles(),
            ["xl/worksheets/sheet1.xml"] = new(new XElement(S + "worksheet",
                new XElement(S + "dimension", new XAttribute("ref", "A1:" + last)),
                new XElement(S + "sheetViews", new XElement(S + "sheetView", new XAttribute("workbookViewId", 0),
                    new XElement(S + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")),
                    new XElement(S + "selection", new XAttribute("pane", "bottomLeft"), new XAttribute("activeCell", "A2"), new XAttribute("sqref", "A2")))),
                new XElement(S + "sheetFormatPr", new XAttribute("defaultRowHeight", 15)),
                new XElement(S + "cols", table.Columns.Select((column, index) => new XElement(S + "col",
                    new XAttribute("min", index + 1), new XAttribute("max", index + 1),
                    new XAttribute("width", Math.Clamp((column.Width - 5) / 7, 1, 255).ToString("0.###", CultureInfo.InvariantCulture)), new XAttribute("customWidth", 1)))),
                data, new XElement(S + "autoFilter", new XAttribute("ref", "A1:" + last))))
        };
    }

    private static XElement ContentType(string name, string type) => new(C + "Override", new XAttribute("PartName", name), new XAttribute("ContentType", type));
    private static XElement Relationship(string id, string kind, string target) => new(P + "Relationship", new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/" + kind), new XAttribute("Target", target));
    private static XElement TextCell(string reference, string value, int style) => new(S + "c", new XAttribute("r", reference), new XAttribute("s", style), new XAttribute("t", "inlineStr"),
        new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), SpreadsheetEscape.Replace(value, "_x005F_").Replace("\r", "_x000D_", StringComparison.Ordinal))));

    private static XDocument Styles() => new(new XElement(S + "styleSheet",
        new XElement(S + "numFmts", new XAttribute("count", 1), new XElement(S + "numFmt", new XAttribute("numFmtId", 164), new XAttribute("formatCode", "0.###"))),
        new XElement(S + "fonts", new XAttribute("count", 2), Font(false), Font(true)),
        new XElement(S + "fills", new XAttribute("count", 3),
            new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "none"))),
            new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "gray125"))),
            new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "solid"), new XElement(S + "fgColor", new XAttribute("rgb", "FFD9EAF7")), new XElement(S + "bgColor", new XAttribute("indexed", 64))))),
        new XElement(S + "borders", new XAttribute("count", 1), new XElement(S + "border", new XElement(S + "left"), new XElement(S + "right"), new XElement(S + "top"), new XElement(S + "bottom"), new XElement(S + "diagonal"))),
        new XElement(S + "cellStyleXfs", new XAttribute("count", 1), Format(0, 0, 0)),
        new XElement(S + "cellXfs", new XAttribute("count", 3), Format(0, 0, 0), Format(0, 1, 2), Format(164, 0, 0)),
        new XElement(S + "cellStyles", new XAttribute("count", 1), new XElement(S + "cellStyle", new XAttribute("name", "Normal"), new XAttribute("xfId", 0), new XAttribute("builtinId", 0)))));

    private static XElement Font(bool bold) => new(S + "font", bold ? new XElement(S + "b") : null,
        new XElement(S + "sz", new XAttribute("val", 11)), new XElement(S + "name", new XAttribute("val", "Calibri")));
    private static XElement Format(int number, int font, int fill) => new(S + "xf", new XAttribute("numFmtId", number), new XAttribute("fontId", font),
        new XAttribute("fillId", fill), new XAttribute("borderId", 0), new XAttribute("xfId", 0), new XAttribute("applyNumberFormat", number == 0 ? 0 : 1),
        new XAttribute("applyFont", font == 0 ? 0 : 1), new XAttribute("applyFill", fill == 0 ? 0 : 1));

    private static string CellReference(int column, int row)
    {
        var letters = "";
        for (int value = column + 1; value > 0; value = (value - 1) / 26) letters = (char)('A' + (value - 1) % 26) + letters;
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    private static void ValidatePackage(string path, IReadOnlyDictionary<string, XDocument> expected)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count != expected.Count) throw new InvalidDataException("Unvollständige XLSX-Datei.");
        foreach (var (name, xml) in expected)
        {
            using var input = (archive.GetEntry(name) ?? throw new InvalidDataException("Fehlender XLSX-Bestandteil: " + name)).Open();
            var actual = XDocument.Load(input, LoadOptions.PreserveWhitespace);
            var normalized = XDocument.Parse(xml.ToString(SaveOptions.DisableFormatting), LoadOptions.PreserveWhitespace);
            if (!XNode.DeepEquals(normalized, actual)) throw new InvalidDataException("XLSX-Rückleseprüfung fehlgeschlagen: " + name);
        }
    }
}
