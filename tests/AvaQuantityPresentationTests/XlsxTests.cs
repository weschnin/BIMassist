using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using BIMassist.Ava.Core;
using static Program;

internal static class XlsxTests
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly string Output = Directory.CreateDirectory(
        Environment.GetEnvironmentVariable("AVA_PRESENTATION_TEST_OUTPUT")
        ?? Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch"),
            "ava-presentation-" + Guid.NewGuid().ToString("N"))).FullName;

    internal static void Run()
    {
        Test("XLSX rejects invalid tables before writing and leaves existing bytes intact", InvalidTables);
        Test("XLSX collision and filesystem failure preserve bytes and remove temporary siblings", FailureSafety);
        Test("XLSX scalars stay invariant and Result is numeric across supported types", ScalarTypes);
        Test("XLSX headers-only and beyond-Z cell coordinates remain valid", EmptyAndWide);
        Test("XLSX new package has ordered safe text, numeric results, styles freeze filter widths", ValidPackage);
    }

    private static AvaQuantityTable Sample() => new(
        [new("RevitElementId", "Revit ElementID", 145), new("Result", "Ergebnis", 110),
         new("FormulaWithParameters", "Formel", 250), new("Kurztext", "Kurztext", 300), new("Unit", "Einheit", 75)],
        [new object?[] { "00012345678901234567", 1.234567890123456789m, "=SUM(1,2)", "<Wand & Tür> 🏠", "m²" },
         new object?[] { "42", -2.3455m, "+cmd|' /C calc'!A0", "Zeile\nzwei", "m2" },
         new object?[] { "9007199254740993", 0m, "@SUM(A1)", "_x0041_ / Deutsch 中文", "St" }]);

    private static void ValidPackage()
    {
        var path = Path.Combine(Output, "quantity-presentation-sample.xlsx");
        var table = Sample();
        AvaQuantityXlsxWriter.Save(path, table);
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Equal(true, new[] { "[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml" }
            .OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(names));
        foreach (var entry in zip.Entries) { using var input = entry.Open(); _ = XDocument.Load(input); }
        XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string relationshipBase = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        Equal(relationshipBase + "officeDocument", (string?)Read(zip, "_rels/.rels").Root!.Element(relationships + "Relationship")!.Attribute("Type"));
        Equal(true, Read(zip, "xl/_rels/workbook.xml.rels").Root!.Elements(relationships + "Relationship")
            .Select(element => (string?)element.Attribute("Type")).SequenceEqual([relationshipBase + "worksheet", relationshipBase + "styles"]));
        var sheet = Read(zip, "xl/worksheets/sheet1.xml");
        var cells = sheet.Descendants(Sheet + "c").ToDictionary(c => (string)c.Attribute("r")!);
        Equal("Revit ElementID,Ergebnis,Formel,Kurztext,Einheit", string.Join(",", Enumerable.Range(0, 5).Select(i => Text(cells[$"{(char)('A' + i)}1"]))));
        Equal("inlineStr", (string?)cells["A2"].Attribute("t"));
        Equal("00012345678901234567", Text(cells["A2"]));
        Equal("n", (string?)cells["B2"].Attribute("t"));
        Equal("1.235", (string?)cells["B2"].Element(Sheet + "v"));
        Equal("-2.346", (string?)cells["B3"].Element(Sheet + "v"));
        Equal("=SUM(1,2)", Text(cells["C2"]));
        Equal("<Wand & Tür> 🏠", Text(cells["D2"]));
        Equal("m²", Text(cells["E2"]));
        Equal("m2", Text(cells["E3"]));
        Equal("_x005F_x0041_ / Deutsch 中文", Text(cells["D4"]));
        Equal(0, sheet.Descendants(Sheet + "f").Count());
        Equal("1", (string?)sheet.Descendants(Sheet + "pane").Single().Attribute("ySplit"));
        Equal("frozen", (string?)sheet.Descendants(Sheet + "pane").Single().Attribute("state"));
        Equal("A1:E4", (string?)sheet.Descendants(Sheet + "autoFilter").Single().Attribute("ref"));
        var widths = sheet.Descendants(Sheet + "col").Select(c => double.Parse((string)c.Attribute("width")!, CultureInfo.InvariantCulture)).ToArray();
        Equal(5, widths.Length);
        Equal(true, widths[3] > widths[2] && widths[2] > widths[0] && widths[0] > widths[1] && widths[1] > widths[4]);
        var styles = Read(zip, "xl/styles.xml");
        Equal("0.###", (string?)styles.Descendants(Sheet + "numFmt").Single().Attribute("formatCode"));
        Equal("2", (string?)cells["B2"].Attribute("s"));
        Equal("1", (string?)cells["A1"].Attribute("s"));
        Equal(1.234567890123456789m, table.Rows[0][1]);
        Console.WriteLine("XLSX_SAMPLE=" + path);
    }
    private static void InvalidTables()
    {
        var sentinel = Path.Combine(Output, "invalid-destination.xlsx");
        var bytes = new byte[] { 0, 1, 2, 255, 5 };
        File.WriteAllBytes(sentinel, bytes);
        var emptyRows = Array.Empty<IReadOnlyList<object?>>();
        var column = new AvaQuantityTableColumn("Result", "Ergebnis", 100);
        var invalid = new List<AvaQuantityTable>
        {
            new([], []), new(null!, []), new([column], null!), new([null!], []),
            new([column, column], []), new([column with { Key = "" }], []),
            new([column with { Header = null! }], []), new([column], [null!]),
            new([column], [Array.Empty<object?>()]), new([column], [new object?[] { 1m, 2m }]),
            new([column], [new object?[] { "1.23456" }]), new([column], [new object?[] { true }]),
            new([column], [new object?[] { double.NaN }]), new([column], [new object?[] { double.PositiveInfinity }]),
            new([column], [new object?[] { float.NegativeInfinity }]),
            new([column with { Key = "Text" }], [new object?[] { new object() }]),
            new([column with { Header = "invalid\0header" }], []),
            new([column with { Key = "Text" }], [new object?[] { "bad\0xml" }]),
            new([column with { Key = "Text" }], [new object?[] { new string('x', 32768) }]),
            new(new CountOnly<AvaQuantityTableColumn>(16385), emptyRows),
            new([column], new CountOnly<IReadOnlyList<object?>>(1048576))
        };
        foreach (var width in new[] { 0, -1, double.NaN, double.PositiveInfinity, 10001 })
            invalid.Add(new([column with { Width = width }], []));
        foreach (var table in invalid)
        {
            Reject<InvalidDataException>(() => AvaQuantityXlsxWriter.Save(sentinel, table));
            Equal(true, bytes.SequenceEqual(File.ReadAllBytes(sentinel)));
            var nonexistent = Path.Combine(Output, "must-not-exist.xlsx");
            Reject<InvalidDataException>(() => AvaQuantityXlsxWriter.Save(nonexistent, table));
            Equal(false, File.Exists(nonexistent));
        }
        Equal(0, Directory.GetFiles(Output, ".ava-quantity-*.tmp").Length);
    }

    private static void FailureSafety()
    {
        var target = Path.Combine(Output, "collision.xlsx");
        var original = new byte[] { 4, 5, 6, 0, 255 };
        File.WriteAllBytes(target, original);
        Reject<IOException>(() => AvaQuantityXlsxWriter.Save(target, Sample()));
        Equal(true, original.SequenceEqual(File.ReadAllBytes(target)));
        var directory = Path.Combine(Output, "directory.xlsx");
        Directory.CreateDirectory(directory);
        try { AvaQuantityXlsxWriter.Save(directory, Sample()); throw new Exception("Expected filesystem failure"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Equal(true, Directory.Exists(directory));
        Equal(0, Directory.GetFiles(Output, ".ava-quantity-*.tmp").Length);
    }

    private static void ScalarTypes()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var path = Path.Combine(Output, "scalar-types.xlsx");
            object[] numbers = [1.23456m, -2.3455d, 3.4567f, (byte)4, (sbyte)-5, (short)6, (ushort)7, 8, (uint)9, 9007199254740993L, (ulong)11];
            AvaQuantityXlsxWriter.Save(path, new([new("Result", "Ergebnis", 100), new("RevitElementId", "ID", 100), new("Text", "Text", 100)],
                numbers.Select(number => (IReadOnlyList<object?>)new object?[] { number, 9007199254740993L, "a\rb\r\nc\td\n e" }).Append(new object?[] { null, "=1+1", "-1+1" }).ToArray()));
            using var zip = ZipFile.OpenRead(path);
            var cells = Read(zip, "xl/worksheets/sheet1.xml").Descendants(Sheet + "c").ToDictionary(c => (string)c.Attribute("r")!);
            Equal("1.235", (string?)cells["A2"].Element(Sheet + "v"));
            Equal("-2.346", (string?)cells["A3"].Element(Sheet + "v"));
            Equal("3.457", (string?)cells["A4"].Element(Sheet + "v"));
            for (int i = 0; i < numbers.Length; i++) Equal("n", (string?)cells[$"A{i + 2}"].Attribute("t"));
            Equal("inlineStr", (string?)cells["B2"].Attribute("t"));
            Equal("9007199254740993", Text(cells["B2"]));
            Equal("a_x000D_b_x000D_\nc\td\n e", Text(cells["C2"]));
            Equal("", Text(cells[$"A{numbers.Length + 2}"]));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void EmptyAndWide()
    {
        var path = Path.Combine(Output, "wide.xlsx");
        var columns = Enumerable.Range(0, 28).Select(i => new AvaQuantityTableColumn("K" + i, "Header " + i, 100)).ToArray();
        AvaQuantityXlsxWriter.Save(path, new(columns, []));
        using var zip = ZipFile.OpenRead(path);
        var sheet = Read(zip, "xl/worksheets/sheet1.xml");
        Equal("A1:AB1", (string?)sheet.Descendants(Sheet + "autoFilter").Single().Attribute("ref"));
        Equal("AB1", (string?)sheet.Descendants(Sheet + "c").Last().Attribute("r"));
    }

    private sealed class CountOnly<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new Exception("Bounds must be checked before enumerating");
        public IEnumerator<T> GetEnumerator() => throw new Exception("Bounds must be checked before enumerating");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static string Text(XElement cell) => (string?)cell.Element(Sheet + "is")?.Element(Sheet + "t") ?? "";
    private static XDocument Read(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }
}
