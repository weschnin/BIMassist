using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using BIMassist.Ava.Core;

internal static class Program
{
    static int passed, failed;
    static readonly XNamespace SourceNs = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";
    static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";
    static readonly string Fixture = """
      <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA82/3.3"><GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate><Date>2026-01-01</Date></GAEBInfo><Award><DP>82</DP><BoQ ID="OriginalBoQ">
      <BoQInfo><Name>Source only</Name><LblBoQ>Original source catalog</LblBoQ><Date>2026-01-01</Date><OutlCompl>AllTxt</OutlCompl>
      <BoQBkdn><Type>BoQLevel</Type><Length>2</Length><Num>Yes</Num></BoQBkdn>
      <BoQBkdn><Type>Item</Type><Length>4</Length><Num>Yes</Num></BoQBkdn></BoQInfo>
      <BoQBody><BoQCtgy ID="OriginalCategory" RNoPart="01"><LblTx><p><span>Original Category</span></p></LblTx><BoQBody><Itemlist>
      <Item ID="FirstItem" RNoPart="0010"><Qty>999</Qty><QU>m2</QU><QtyDeterm><QDetermItem><QTakeoff Row="do not copy me"/></QDetermItem></QtyDeterm></Item>
      <Item ID="SecondItem" RNoPart="0020"><Qty>888</Qty><QU>m3</QU></Item>
      </Itemlist></BoQBody></BoQCtgy></BoQBody></BoQ></Award></GAEB>
      """;
    static int Main(string[] args)
    {
        foreach (var (file, field, freshHash) in new[] {
            ("GAEB_DA_XML_31_3.3_2023-01.xsd", "MainSchema", "179B9D8C9A93145AF63298270E44C2D1AE820BB850EDD0BA284D5B7CBEF01B56"),
            ("GAEB_DA_XML_Lib_3.3_2021-05.xsd", "LibrarySchema", "A00CAD3A5BFAF079DE936393C0827C2387D4B9407C113359CCA4B493903388EC") })
        Test("official fixture bytes equal embedded bytes and pinned fresh vendor SHA256: " + file, () =>
        {
            // Read the fixture copied from this checkout/archive, not the developer's fresh download directory.
            var fixtureBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Official", file));
            var encoded = (string)typeof(X31Writer).GetField(field,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;
            using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
            using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var decoded = new MemoryStream(); gzip.CopyTo(decoded);
            var embeddedHash = Convert.ToHexString(SHA256.HashData(decoded.ToArray()));
            var fixtureHash = Convert.ToHexString(SHA256.HashData(fixtureBytes));
            Equal(freshHash, embeddedHash); Equal(embeddedHash, fixtureHash);
            Require(fixtureBytes.SequenceEqual(decoded.ToArray()), "Official fixture changed vendor bytes: " + file);
            Console.WriteLine(file + " SHA256 " + fixtureHash + "; fixture/embedded/fresh exact byte match");
        });
        Test("X82 exact bytes survive portable template JSON; optional metadata leaves legacy readable", () =>
        {
            var template = Read(Fixture);
            var property = typeof(SourceStamp).GetProperty("X82SnapshotBase64");
            Require(property != null, "SourceStamp lacks optional X82 snapshot metadata");
            Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(Fixture)), (string?)property!.GetValue(template.LvCatalog.Source));
            var json = TemplateJson.Serialize(template);
            Equal(json, TemplateJson.Serialize(TemplateJson.Deserialize(json)));
            var legacy = JsonNode.Parse(json)!.AsObject();
            legacy["LvCatalog"]!["Source"]!.AsObject().Remove("X82SnapshotBase64");
            Require(TemplateJson.Deserialize(legacy.ToJsonString()).SchemaVersion == 3, "Legacy schema changed");
        });
        Test("X82 quantity export clones the source, reduces positions, sums quantities and preserves LV text and units", () =>
        {
            var template = Read(Fixture);
            var before = template.LvCatalog.Source.X82SnapshotBase64;
            var output = X82QuantityWriter.Serialize(template, [
                new("FirstItem", 1.25m, "m2", "", "101"), new("FirstItem", 2.25m, "m2", "", "102"),
                new("SecondItem", 4m, "m3", "", "103")]);
            var doc = XDocument.Parse(output);
            Equal(2, doc.Descendants(SourceNs + "Item").Count());
            var first = doc.Descendants(SourceNs + "Item").Single(item => (string?)item.Attribute("ID") == "FirstItem");
            Equal("999", (string?)first.Element(SourceNs + "Qty"));
            Equal("3.5", (string?)first.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
            Equal("m2", (string?)first.Element(SourceNs + "QU"));
            var second = doc.Descendants(SourceNs + "Item").Single(item => (string?)item.Attribute("ID") == "SecondItem");
            Equal("888", (string?)second.Element(SourceNs + "Qty"));
            Equal("4", (string?)second.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
            Require(!output.Contains("do not copy me", StringComparison.Ordinal), "Stale quantity calculation remained in X82 output");
            Equal(before, template.LvCatalog.Source.X82SnapshotBase64);
            using var check = new MemoryStream(Encoding.UTF8.GetBytes(output));
            Equal(2, CatalogReaders.ReadX82(check, "reduced.x82").Entries.Count);
        });
        Test("X82 export keeps every source position but only writes totals for measured positions", () =>
        {
            var template = Read(Fixture);
            var output = X82QuantityWriter.Serialize(template, [new("FirstItem", 1.25m, "m2", "", "101")]);
            var doc = XDocument.Parse(output);
            Equal(2, doc.Descendants(SourceNs + "Item").Count());
            var first = doc.Descendants(SourceNs + "Item").Single(item => (string?)item.Attribute("ID") == "FirstItem");
            var unmeasured = doc.Descendants(SourceNs + "Item").Single(item => (string?)item.Attribute("ID") == "SecondItem");
            Equal("999", (string?)first.Element(SourceNs + "Qty"));
            Equal("1.25", (string?)first.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
            Equal("888", (string?)unmeasured.Element(SourceNs + "Qty"));
            Equal("m3", (string?)unmeasured.Element(SourceNs + "QU"));
            Require(unmeasured.Element(SourceNs + "QtyDeterm") == null, "An unmeasured position received a fabricated quantity");
        });
        Test("X82 new-file export refuses an existing path without replacing its bytes", () =>
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "existing.x82");
                var original = Encoding.UTF8.GetBytes("existing target must remain untouched");
                File.WriteAllBytes(path, original);
                try
                {
                    X82QuantityWriter.Save(path, Read(Fixture), [new("FirstItem", 1m, "m2", "", "101")]);
                    throw new Exception("X82 export replaced a file instead of requiring a new target");
                }
                catch (IOException) { }
                Require(original.SequenceEqual(File.ReadAllBytes(path)), "Existing X82 target bytes were changed");
                Equal(1, Directory.GetFiles(directory).Length);
            });
        });
        Test("saved X82 exposes measured quantities as GAEB REB takeoff rows, not only an optional total", () =>
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "measured.x82");
                var source = Read(Fixture);
                X82QuantityWriter.Save(path, source, [new("FirstItem", 1.25m, "m2", "", "101"),
                    new("FirstItem", 2.25m, "m2", "", "102")]);
                var saved = XDocument.Load(path);
                var item = saved.Descendants(SourceNs + "Item").Single(candidate => candidate.Element(SourceNs + "QtyDeterm") != null);
                Equal("FirstItem", (string?)item.Attribute("ID"));
                Equal("999", (string?)item.Element(SourceNs + "Qty"));
                Equal("m2", (string?)item.Element(SourceNs + "QU"));
                Equal("3.5", (string?)item.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
                Equal("REB23003-2009", (string?)saved.Descendants(SourceNs + "BoQInfo")
                    .Single().Element(SourceNs + "QtyDetermInfo")?.Element(SourceNs + "MethodDescription"));
                var lines = item.Element(SourceNs + "QtyDeterm")!.Elements(SourceNs + "QDetermItem")
                    .Select(line => (string?)line.Element(SourceNs + "QTakeoff")?.Attribute("Row") ?? "").ToArray();
                Equal(2, lines.Length);
                Require(lines.All(line => line.Length == 80 && line.Substring(29, 2) == "91"), "Missing GAEB REB numeric takeoff lines");
                Require(lines.Select(line => line.Substring(31, 38).Trim()).SequenceEqual(["1,25=", "2,25="]),
                    "Saved GAEB takeoff lines do not carry the measured values");
                Require(lines.Select(line => line.Substring(69, 6)).Distinct().Count() == 2, "Takeoff row addresses must be distinct");
                ValidateX82(saved);
            });
        });
        Test("X82 positive half-thousandth rows allocate rounded total without a negative REB row", () =>
        {
            var saved = XDocument.Parse(X82QuantityWriter.Serialize(Read(Fixture),
                Enumerable.Range(0, 4).Select(i => new QuantityTakeoffRow("FirstItem", 0.0005m, "m2", "", i.ToString())).ToArray()));
            var item = saved.Descendants(SourceNs + "Item").Single(candidate => candidate.Element(SourceNs + "QtyDeterm") != null);
            Equal("999", (string?)item.Element(SourceNs + "Qty"));
            Equal("m2", (string?)item.Element(SourceNs + "QU"));
            Equal("0.002", (string?)item.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
            var values = item.Descendants(SourceNs + "QTakeoff")
                .Select(line => decimal.Parse(((string)line.Attribute("Row")!).Substring(31, 38).Trim().TrimEnd('=').Replace(',', '.'),
                    CultureInfo.InvariantCulture)).ToArray();
            Equal(4, values.Length);
            Require(values.All(value => value >= 0m && value <= 0.001m), "Nonnegative measured inputs produced a negative or overstated REB row");
            Equal(0.002m, values.Sum());
            ValidateX82(saved);
        });
        Test("X82 rounding boundaries preserve row signs and the rounded total across positive and mixed inputs", () =>
        {
            foreach (var inputs in new[] {
                new[] { 0.0004m, 0.0004m, 0.0004m, 0.0004m },
                new[] { 0.00049m, 0.00049m },
                new[] { -0.0005m, -0.0005m, -0.0005m, -0.0005m },
                new[] { 0.0005m, -0.0004m, 0.0005m, -0.0006m },
                new[] { 1.0005m, -0.0005m }
            })
            {
                var saved = XDocument.Parse(X82QuantityWriter.Serialize(Read(Fixture),
                    inputs.Select((value, i) => new QuantityTakeoffRow("FirstItem", value, "m2", "", i.ToString())).ToArray()));
                var item = saved.Descendants(SourceNs + "Item").Single(candidate => candidate.Element(SourceNs + "QtyDeterm") != null);
                Equal("999", (string?)item.Element(SourceNs + "Qty"));
                Equal("m2", (string?)item.Element(SourceNs + "QU"));
                var expected = decimal.Round(inputs.Sum(), 3, MidpointRounding.AwayFromZero);
                Equal(expected.ToString("0.###", CultureInfo.InvariantCulture),
                    (string?)item.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
                var measured = item.Descendants(SourceNs + "QTakeoff")
                    .Select(line => decimal.Parse(((string)line.Attribute("Row")!).Substring(31, 38).Trim().TrimEnd('=').Replace(',', '.'),
                        CultureInfo.InvariantCulture)).ToArray();
                Equal(inputs.Length, measured.Length);
                Equal(expected, measured.Sum());
                for (var i = 0; i < inputs.Length; i++)
                    Require((inputs[i] >= 0m && measured[i] >= 0m || inputs[i] <= 0m && measured[i] <= 0m)
                        && measured[i] == decimal.Truncate(measured[i] * 1000m) / 1000m,
                        $"REB row {i} inverted the measured sign or exceeded 0.001 precision");
                ValidateX82(saved);
            }
        });
        if (Environment.GetEnvironmentVariable("BIMASSIST_AVA_X82_REPRO_FILE") is { Length: > 0 } historicalFile)
            Test("historical X82 with totals but no takeoff lines yields a schema-valid measured export", () =>
            {
                Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BIMASSIST_AVA_X82_SCHEMA")),
                    "The historical-file acceptance requires the official DA82 XSD path");
                var originalBytes = File.ReadAllBytes(historicalFile);
                var original = XDocument.Load(new MemoryStream(originalBytes));
                var originalItems = original.Descendants(SourceNs + "Item").ToArray();
                Require(originalItems.Length > 0 && originalItems.All(item =>
                    item.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty") != null
                    && !item.Descendants(SourceNs + "QTakeoff").Any()),
                    "Historical X82 is not the reported QtyDeterm/Qty-only export");
                var template = new AvaTemplate { LvCatalog = CatalogReaders.ReadX82(new MemoryStream(originalBytes), historicalFile) };
                var rows = originalItems.Select(item => new QuantityTakeoffRow((string)item.Attribute("ID")!,
                    decimal.Parse(item.Element(SourceNs + "QtyDeterm")!.Element(SourceNs + "Qty")!.Value,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                    item.Element(SourceNs + "QU")?.Value ?? "", "historical file, not a live Revit takeoff")).ToArray();
                WithDirectory(directory =>
                {
                    var path = Path.Combine(directory, "historical-reexport.x82");
                    X82QuantityWriter.Save(path, template, rows);
                    var exported = XDocument.Load(path);
                    var actualItems = exported.Descendants(SourceNs + "Item").ToArray();
                    Equal(originalItems.Length, actualItems.Length);
                    foreach (var item in actualItems)
                    {
                        var before = originalItems.Single(candidate => (string?)candidate.Attribute("ID") == (string?)item.Attribute("ID"));
                        Equal((string?)before.Element(SourceNs + "Qty"), (string?)item.Element(SourceNs + "Qty"));
                        Equal((string?)before.Element(SourceNs + "QU"), (string?)item.Element(SourceNs + "QU"));
                        Equal((string?)before.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"),
                            (string?)item.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
                        Equal(1, item.Descendants(SourceNs + "QTakeoff").Count());
                    }
                    ValidateX82(exported);
                    Console.WriteLine($"  historical file: {actualItems.Length} measured items; source Item/Qty unchanged; {actualItems.Length} REB rows; official DA82 schema valid");
                });
                Require(originalBytes.SequenceEqual(File.ReadAllBytes(historicalFile)), "Historical reference file was changed");
            });
        Test("X82 quantity export preserves unmeasured positions and refuses unknown IDs or unit changes", () =>
        {
            var template = Read(Fixture);
            var output = X82QuantityWriter.Serialize(template, [new("FirstItem", 5m, "m2", "", "101")]);
            var doc = XDocument.Parse(output);
            Equal(2, doc.Descendants(SourceNs + "Item").Count());
            var first = doc.Descendants(SourceNs + "Item").Single(item => (string?)item.Attribute("ID") == "FirstItem");
            Equal("FirstItem", (string?)first.Attribute("ID"));
            Equal("999", (string?)first.Element(SourceNs + "Qty"));
            Equal("5", (string?)first.Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
            var unmeasured = doc.Descendants(SourceNs + "Item").Single(item => (string?)item.Attribute("ID") == "SecondItem");
            Equal("888", (string?)unmeasured.Element(SourceNs + "Qty"));
            Require(unmeasured.Element(SourceNs + "QtyDeterm") == null, "An unmeasured position retained stale quantity data");
            Reject(() => X82QuantityWriter.Serialize(template, [new("MissingItem", 1m, "m2", "")]));
            Reject(() => X82QuantityWriter.Serialize(template, [new("FirstItem", 1m, "m", "")]));
        });
        Test("X82 export retains all source positions, hierarchy and text while calculating only measured quantities", () =>
        {
            var source = XDocument.Parse(Fixture);
            var ns = SourceNs;
            var list = source.Descendants(ns + "Itemlist").Single();
            var second = list.Elements(ns + "Item").Single(item => (string?)item.Attribute("ID") == "SecondItem");
            second.Remove();
            static XElement TextDescription(XNamespace ns, string text) => new(ns + "Description",
                new XElement(ns + "CompleteText", new XElement(ns + "DetailTxt",
                    new XElement(ns + "Text", new XElement(ns + "p", new XElement(ns + "span", text))))));
            static XElement MarkupPosition(XNamespace ns, string id, string ordinal, string reference, string text) =>
                new(ns + "MarkupItem", new XAttribute("ID", id), new XAttribute("RNoPart", ordinal),
                    new XElement(ns + "RefRNo", new XAttribute("IDRef", reference)), new XElement(ns + "MarkupType", "IdentAsMark"),
                    TextDescription(ns, text));
            list.AddFirst(new XElement(ns + "Remark", new XAttribute("ID", "RelevantRemark"), TextDescription(ns, "Relevanter Hinweistext")));
            list.Add(new XElement(ns + "PerfDescr", new XAttribute("ID", "RelevantPerf"),
                new XElement(ns + "PerfNo", "01"), new XElement(ns + "PerfLbl", "Ausführungsbeschreibung"),
                TextDescription(ns, "Ausführungsdetail")));
            var originalCategory = source.Descendants(ns + "BoQCtgy").Single(category => (string?)category.Attribute("ID") == "OriginalCategory");
            originalCategory.Remove();
            var parentCategory = new XElement(ns + "BoQCtgy", new XAttribute("ID", "ParentCategory"), new XAttribute("RNoPart", "01"),
                new XElement(ns + "LblTx", new XElement(ns + "p", new XElement(ns + "span", "Übergeordneter Titel"))),
                new XElement(ns + "BoQBody", originalCategory));
            source.Descendants(ns + "BoQBody").First().AddFirst(parentCategory);
            list.Add(MarkupPosition(ns, "SelectedMarkup", "0090", "FirstItem", "Zuschlag zu gemessener Position"));
            var unrelated = new XElement(ns + "BoQCtgy", new XAttribute("ID", "UnrelatedCategory"), new XAttribute("RNoPart", "02"),
                new XElement(ns + "LblTx", new XElement(ns + "p", new XElement(ns + "span", "Unbeteiligter Titel"))),
                new XElement(ns + "BoQBody", new XElement(ns + "Itemlist",
                    new XElement(ns + "Remark", new XAttribute("ID", "UnrelatedRemark"), TextDescription(ns, "Unbeteiligter Fließtext")),
                    new XElement(ns + "Item", new XAttribute("ID", "SecondItem"), new XAttribute("RNoPart", "0020"),
                        new XElement(ns + "Qty", "888"), new XElement(ns + "QU", "m3")))));
            unrelated.Descendants(ns + "Itemlist").Single().Add(MarkupPosition(ns, "UnselectedMarkup", "0091", "SecondItem", "Zuschlag zu nicht gemessener Position"));
            source.Descendants(ns + "BoQBody").First().Add(unrelated);
            var template = Read(source.ToString(SaveOptions.DisableFormatting));
            var output = X82QuantityWriter.Serialize(template, [new("FirstItem", 3.5m, "m2", "", "101")]);
            var result = XDocument.Parse(output);
            var items = result.Descendants(ns + "Item").ToArray();
            Equal(2, items.Length);
            var measuredItem = items.Single(item => (string?)item.Attribute("ID") == "FirstItem");
            var unmeasuredItem = items.Single(item => (string?)item.Attribute("ID") == "SecondItem");
            Equal("888", (string?)unmeasuredItem.Element(ns + "Qty"));
            Equal("m3", (string?)unmeasuredItem.Element(ns + "QU"));
            Require(unmeasuredItem.Element(ns + "QtyDeterm") == null, "Unmeasured position retained a calculated quantity");
            var markupItems = result.Descendants(ns + "MarkupItem").ToArray();
            Equal(2, markupItems.Length);
            Require(markupItems.Any(markup => (string?)markup.Attribute("ID") == "SelectedMarkup")
                && markupItems.Any(markup => (string?)markup.Attribute("ID") == "UnselectedMarkup"),
                "Source markup attached to a retained LV position was lost.");
            var categories = result.Descendants(ns + "BoQCtgy").ToArray();
            Equal(3, categories.Length);
            Require(categories.Select(category => (string?)category.Attribute("ID")).ToHashSet(StringComparer.Ordinal)
                .SetEquals(["ParentCategory", "OriginalCategory", "UnrelatedCategory"]), "The source LV hierarchy was not retained exactly.");
            Require(output.Contains("Relevanter Hinweistext", StringComparison.Ordinal)
                && output.Contains("Ausführungsdetail", StringComparison.Ordinal)
                && output.Contains("Übergeordneter Titel", StringComparison.Ordinal)
                && output.Contains("Original Category", StringComparison.Ordinal),
                "The measured LV hierarchy or its applicable text context was lost.");
            Require(output.Contains("SecondItem", StringComparison.Ordinal)
                && output.Contains("UnselectedMarkup", StringComparison.Ordinal)
                && output.Contains("Unbeteiligter Titel", StringComparison.Ordinal)
                && output.Contains("Unbeteiligter Fließtext", StringComparison.Ordinal),
                "A source position or its original branch text was removed from the complete X82 output.");
            Require(measuredItem.Element(ns + "QtyDeterm") != null, "Measured position has no calculated quantity.");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BIMASSIST_AVA_X82_SCHEMA")))
            {
                var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
                using var schemaReader = XmlReader.Create(Environment.GetEnvironmentVariable("BIMASSIST_AVA_X82_SCHEMA")!,
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = new XmlUrlResolver() });
                schemas.Add(null, schemaReader);
                var issues = new List<string>();
                result.Validate(schemas, (_, args) => issues.Add(args.Message), true);
                Require(issues.Count == 0, string.Join(Environment.NewLine, issues));
            }
        });
        Test("Complete X82 quantity output validates against the official DA82 schema when supplied", () =>
        {
            var schemaPath = Environment.GetEnvironmentVariable("BIMASSIST_AVA_X82_SCHEMA");
            if (string.IsNullOrWhiteSpace(schemaPath)) return;
            var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
            using (var schemaReader = XmlReader.Create(schemaPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = new XmlUrlResolver() }))
                schemas.Add(null, schemaReader);
            var template = Read(Fixture);
            var output = X82QuantityWriter.Serialize(template, [new("FirstItem", 5m, "m2", "", "101")]);
            var issues = new List<string>();
            XDocument.Parse(output).Validate(schemas, (_, args) => issues.Add(args.Message), true);
            Require(issues.Count == 0, string.Join(Environment.NewLine, issues));
        });
        Test("production X31 output validates directly against the official GAEB schema without vendor extensions", () =>
        {
            var type = typeof(AvaTemplate).Assembly.GetType("BIMassist.Ava.Core.X31Writer");
            Require(type != null, "X31Writer is missing");
            var rowType = typeof(AvaTemplate).Assembly.GetType("BIMassist.Ava.Core.QuantityTakeoffRow")!;
            var rows = Array.CreateInstance(rowType, 1);
            rows.SetValue(Activator.CreateInstance(rowType, "FirstItem", 12.345m, "m2", "Revit ElementId=42", "", null, null, null, null), 0);
            var xml = (string)type!.GetMethod("Serialize")!.Invoke(null, [Read(Fixture), rows])!;
            Validate(xml);
            Require(!XDocument.Parse(xml).Descendants().Any(element => element.Name.NamespaceName == "BVBS"),
                "Strict X31 export must not emit non-standard BVBS vendor extensions.");
            var root = XDocument.Parse(xml);
            Equal("OriginalBoQ", (string?)root.Descendants(Ns + "BoQ").Single().Attribute("ID"));
            Equal("01", (string?)root.Descendants(Ns + "BoQCtgy").Single().Attribute("RNoPart"));
            var item = root.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "FirstItem");
            Equal("0010", (string?)item.Attribute("RNoPart"));
            var numeric = item.Descendants(Ns + "QTakeoff").Select(x => x.Attribute("Row")!.Value).Single(x => x.Substring(29, 2) == "91");
            Equal(80, numeric.Length); Equal("12,345=", numeric.Substring(31, 38).Trim());
            Require(!xml.Contains("999") && !xml.Contains("do not copy me") && !xml.Contains("Available"), "Exemplar values or certificate leaked");
        });
        Test("unified X31 export uses the chosen identity and emits no grouping or formula data", () =>
        {
            var record = new AvaQuantityRecord(Guid.NewGuid(), "B0000001", "FirstItem", "01.0010",
                "Erste Position", "m2", "1234567890123", "[Länge] × [Breite]", "2,0 × 3,5", "Bauteil", "Wand",
                "BeispielFamilie", "BeispielTyp", 7m);
            var template = Read(Fixture);
            var bimassistXml = XDocument.Parse(X31Writer.SerializeMeasured(template, [record], useRevitElementId: false));
            var bimassistRows = bimassistXml.Descendants(Ns + "QTakeoff").Select(element => (string)element.Attribute("Row")!).ToArray();
            Equal(1, bimassistRows.Length);
            Equal("B0000001", bimassistRows[0].Substring(13, 9).Trim());
            Require(!bimassistXml.ToString().Contains("[Länge]", StringComparison.Ordinal)
                && !bimassistXml.ToString().Contains("Bauteil", StringComparison.Ordinal)
                && !bimassistXml.ToString().Contains("Wand", StringComparison.Ordinal),
                "Formula or grouping values leaked into the plain X31 export.");

            var revitXml = XDocument.Parse(X31Writer.SerializeMeasured(template, [record], useRevitElementId: true));
            var revitRows = revitXml.Descendants(Ns + "QTakeoff").Select(element => (string)element.Attribute("Row")!).ToArray();
            Require(revitRows.Any(row => row.Contains("1234567890123", StringComparison.Ordinal)),
                "The selected Revit ElementID is missing from X31 output.");
            Require(revitRows.All(row => !row.Contains("[Länge]", StringComparison.Ordinal)
                            && !row.Contains("Bauteil", StringComparison.Ordinal)), "Formula or grouping rows were emitted for the Revit-ID export.");
        });
        Test("persisted run X31 export uses its embedded template and only measured records", () =>
        {
            var template = Read(Fixture);
            var record = new AvaQuantityRecord(Guid.NewGuid(), "B0000001", "FirstItem", "01.0010",
                "Erste Position", "m2", "42", "A*B", "2*3", "Gruppe", "Wand", "Familie", "Typ", 6m);
            var run = new AvaQuantityRun(AvaQuantityRunCodec.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
                "Testprojekt", "", "alle Elemente", TemplateJson.Serialize(template), [record]);
            var xml = XDocument.Parse(AvaQuantityRunExport.SerializeX31(run, useRevitElementId: false));
            var rows = xml.Descendants(Ns + "QTakeoff").Select(element => (string)element.Attribute("Row")!).ToArray();
            Equal(1, rows.Length);
            Equal("B0000001", rows[0].Substring(13, 9).Trim());
            Require(!xml.ToString().Contains("A*B", StringComparison.Ordinal)
                && !xml.ToString().Contains("2*3", StringComparison.Ordinal)
                && !xml.ToString().Contains("Gruppe", StringComparison.Ordinal)
                && !xml.ToString().Contains("Wand", StringComparison.Ordinal)
                && !xml.ToString().Contains("Familie", StringComparison.Ordinal)
                && !xml.ToString().Contains("BeispielTyp", StringComparison.Ordinal),
                "Formula and grouping details from the project run must not leak into X31.");
            Require(rows[0].Substring(31, 38).Trim().StartsWith("6=", StringComparison.Ordinal),
                "The measured result was not serialized as its numeric calculation.");
            var path = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS"))!, "persisted-run.x31");
            AvaQuantityRunExport.SaveX31(path, run, useRevitElementId: false);
            Validate(File.ReadAllText(path));
        });
        Test("persisted run X82 keeps the complete source LV and saves only to a new path", () =>
        {
            var template = Read(Fixture);
            var record = new AvaQuantityRecord(Guid.NewGuid(), "B0000001", "FirstItem", "01.0010",
                "Erste Position", "m2", "42", "Konstante 5 m2", "5 m2", "", "", "", "", 5m);
            var run = new AvaQuantityRun(AvaQuantityRunCodec.CurrentSchemaVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
                "Testprojekt", "", "alle Elemente", TemplateJson.Serialize(template), [record]);
            var xml = XDocument.Parse(AvaQuantityRunExport.SerializeX82(run));
            ValidateX82(xml);
            var formulaRun = run with { Records = [record with { FormulaWithParameters = "B*H [B: Breite (Typ); H: Höhe (Instanz)]",
                FormulaWithValues = "B=2 m; H=2.5 m => 5 m2" }] };
            var formulaXml = XDocument.Parse(AvaQuantityRunExport.SerializeX82(formulaRun));
            Equal("5", (string?)formulaXml.Descendants(SourceNs + "Item").Single(item => (string?)item.Attribute("ID") == "FirstItem")
                .Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
            ValidateX82(formulaXml);
            var items = xml.Descendants(SourceNs + "Item").ToArray();
            Equal(2, items.Length);
            Equal("999", (string?)items.Single(item => (string?)item.Attribute("ID") == "FirstItem").Element(SourceNs + "Qty"));
            Equal("5", (string?)items.Single(item => (string?)item.Attribute("ID") == "FirstItem")
                .Element(SourceNs + "QtyDeterm")?.Element(SourceNs + "Qty"));
            var unmeasured = items.Single(item => (string?)item.Attribute("ID") == "SecondItem");
            Equal("888", (string?)unmeasured.Element(SourceNs + "Qty"));
            Require(unmeasured.Element(SourceNs + "QtyDeterm") == null, "Unmeasured source position retained calculated quantity data.");

            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "persisted-run.x82");
                AvaQuantityRunExport.SaveX82(path, run);
                var saved = File.ReadAllBytes(path);
                var collisionRejected = false;
                try { AvaQuantityRunExport.SaveX82(path, run); }
                catch (IOException) { collisionRejected = true; }
                Require(collisionRejected, "An existing X82 destination must be rejected.");
                Require(saved.SequenceEqual(File.ReadAllBytes(path)), "A collision overwrote the existing X82 file.");
            });
        });
        Test("Serialize fails closed on official-schema-invalid output structure", () =>
        {
            Reject(() => X31Writer.Serialize(Read(Fixture.Replace("RNoPart=\"0010\"", "RNoPart=\"bad!\"")), [new("FirstItem", 1m, "m2", "")]));
            var missingBreakdown = XDocument.Parse(Fixture);
            missingBreakdown.Descendants(SourceNs + "BoQBkdn").Remove();
            Reject(() => X31Writer.Serialize(Read(missingBreakdown.ToString()), [new("FirstItem", 1m, "m2", "")]));
        });
        Test("X31 refuses a quantity whose unit differs from the unchanged LV unit", () =>
        {
            Reject(() => X31Writer.Serialize(Read(Fixture), [new("FirstItem", 1m, "m", "unit mismatch")]));
        });
        Test("X31 saves substituted REB91 arithmetic, symbolic remark and complete compact ID without changing LV", () =>
        {
            var row = new QuantityTakeoffRow("FirstItem", 6m, "m2", "", "4294967296", "Area", "Baubereich");
            var formula = typeof(QuantityTakeoffRow).GetProperty("FormulaExpression");
            var approach = typeof(QuantityTakeoffRow).GetProperty("CalculationApproach");
            Require(formula != null && approach != null, "Export rows lose formula metadata");
            formula!.SetValue(row, "B*H"); approach!.SetValue(row, "(2*3)");
            var path = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS"))!, "formula-saved.x31");
            X31Writer.Save(path, Read(Fixture), [row]);
            var saved = File.ReadAllText(path); Validate(saved);
            var doc = XDocument.Parse(saved);
            var rows = doc.Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Require(rows.All(x => x.Length == 80), "Raw REB rows must have 80 columns");
            var calc = rows.Single(x => x.Substring(29, 2) == "91");
            Equal("B*H", calc.Substring(13, 9).TrimEnd()); Equal("(2*3)=", calc.Substring(31, 38).TrimEnd());
            Equal("(ID: 4294967296)", rows[Array.IndexOf(rows, calc) - 1].Substring(13, 56).TrimEnd());
            Require(!doc.Descendants().Any(element => element.Name.NamespaceName == "BVBS"),
                "Formula metadata must stay in standard REB rows, not vendor extension XML.");
            const string symbolic = "sqrt(Volumen/Schnittlänge)*Schnittlänge";
            var numeric = QuantityFormula.CreateReb91Approach(symbolic,
                new Dictionary<string, (decimal Value, string Unit, string Spec)> {
                    ["Volumen"] = (1.30086m, "m3", "VOLUME"), ["Schnittlänge"] = (14.454m, "m", "LENGTH") }, "m2", "m2");
            var rootPath = Path.Combine(Path.GetDirectoryName(path)!, "formula-root-saved.x31");
            X31Writer.Save(rootPath, Read(Fixture), [row with { Value = 4.3362m, FormulaExpression = symbolic, CalculationApproach = numeric }]);
            var rootSaved = File.ReadAllText(rootPath); Validate(rootSaved);
            Equal(4.3362m, X31Writer.EvaluateReb91(numeric));
            var roundedApproach = string.Concat(XDocument.Parse(rootSaved).Descendants(Ns + "QTakeoff")
                .Select(x => (string)x.Attribute("Row")!).Where(x => x.Substring(29, 2) == "91")
                .Select(x => x.Substring(31, 38).TrimEnd()));
            Equal("(((1,301/14,454)**(0,5))*14,454)=", roundedApproach);
            Equal(4.336m, decimal.Round(X31Writer.EvaluateReb91(roundedApproach), 3, MidpointRounding.AwayFromZero));
            Console.WriteLine("Saved official-schema-validated formula artifact: " + rootPath + "; rounded REB91 " + roundedApproach + "; Result 4.336 m2");
        });
        Test("X82 retains actual formula calculation instead of replacing it with a precomputed quantity", () =>
        {
            var row = new QuantityTakeoffRow("FirstItem", 4.3362m, "m2", "", "42",
                FormulaExpression: "sqrt(V/L)*L", CalculationApproach: "((1,30086/14,454)**(0,5))*14,454");
            var output = XDocument.Parse(X82QuantityWriter.Serialize(Read(Fixture), [row]));
            var item = output.Descendants(SourceNs + "Item").Single(candidate => candidate.Element(SourceNs + "QtyDeterm") != null);
            Equal("999", item.Element(SourceNs + "Qty")!.Value);
            Equal("m2", item.Element(SourceNs + "QU")!.Value);
            Equal("4.336", item.Element(SourceNs + "QtyDeterm")!.Element(SourceNs + "Qty")!.Value);
            var calculation = item.Descendants(SourceNs + "QTakeoff").Select(x => (string)x.Attribute("Row")!)
                .Where(x => x.Substring(29, 2) == "91").Select(x => x.Substring(31, 38).TrimEnd()).ToArray();
            Equal(row.CalculationApproach + "=", string.Concat(calculation));
            Equal("sqrt(V/L)*L", (string?)item.Descendants(XName.Get("Explanation", "BVBS")).Single().Attribute("Formula"));
            ValidateX82(output);
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "formula-saved.x82");
                X82QuantityWriter.Save(path, Read(Fixture), [row]);
                ValidateX82(XDocument.Load(path));
            });
        });
        Test("REB91 rejects unsupported grammar and unrepresentable literal precision instead of silently changing it", () =>
        {
            foreach (var invalid in new[] { "2*-3", "2--3", "2**1", "2**(2-1)", "2^3", "sqrt(9)",
                "0.123456789012345678901234567891", "1=" })
                Reject(() => X31Writer.Serialize(Read(Fixture), [new("FirstItem", 0m, "m2", "", "42",
                    FormulaExpression: "0", CalculationApproach: invalid)]));
            Reject(() => X31Writer.EvaluateReb91("2*-3"));
            Reject(() => X31Writer.EvaluateReb91("2**(2-1)"));
        });
        Test("REB91 continuation preserves every token, full formula and ID with exactly one terminating equals", () =>
        {
            const string number = "0,123";
            var approach = string.Join("+", Enumerable.Repeat(number, 20));
            var symbolic = string.Join("+", Enumerable.Range(1, 20).Select(i => "[Parameter " + i + "]"));
            var value = X31Writer.EvaluateReb91(approach);
            var row = new QuantityTakeoffRow("FirstItem", value, "m2", "", long.MaxValue.ToString(CultureInfo.InvariantCulture),
                FormulaExpression: symbolic, CalculationApproach: approach);
            var xml = X31Writer.Serialize(Read(Fixture), [row]); Validate(xml);
            var doc = XDocument.Parse(xml);
            var raw = doc.Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Require(raw.All(x => x.Length == 80), "Continuation crossed fixed fields");
            var numeric = raw.Where(x => x.Substring(29, 2) == "91").ToArray();
            Require(numeric.Length > 1, "The long numeric approach did not use REB continuation rows.");
            Equal(approach + "=", string.Concat(numeric.Select(x => x.Substring(31, 38).TrimEnd())));
            Equal(1, numeric.Count(x => x.Substring(31, 38).Contains('=')));
            Equal("(ID: " + long.MaxValue.ToString(CultureInfo.InvariantCulture) + ")", raw[0].Substring(13, 56).TrimEnd());
            var formulaCommentRows = raw.Where(x => x[12] == '*' && !x.Substring(13, 56).TrimEnd().StartsWith("(ID:", StringComparison.Ordinal)).ToArray();
            var formulaTail = raw.First(x => x.Substring(29, 2) == "91").Substring(13, 9).TrimEnd();
            Equal(symbolic, string.Concat(formulaCommentRows.Select(x => x.Substring(13, 56).TrimEnd())) + formulaTail);
            Require(formulaCommentRows.All(x => !x.Substring(13, 56).StartsWith("Formel:", StringComparison.Ordinal)),
                "Formula comments must not add a redundant label.");
            Require(!doc.Descendants().Any(element => element.Name.NamespaceName == "BVBS"),
                "Long formula must remain in REB comment/remark fields.");
            Reject(() => X31Writer.Serialize(Read(Fixture), [row with { CalculationApproach = approach + "+" + number }]));
            Reject(() => X31Writer.EvaluateReb91("0.123456789012345678901234567891"));
            Reject(() => X31Writer.Serialize(Read(Fixture), [row with { CalculationApproach = null }]));
        });
        Test("X82 refuses formula precision allocation that would change the saved formula or LV total", () =>
        {
            var rows = Enumerable.Range(0, 2).Select(i => new QuantityTakeoffRow("FirstItem", 0.0005m, "m2", "", i.ToString(),
                FormulaExpression: "x/2", CalculationApproach: "(0,001/2)")).ToArray();
            Reject(() => X82QuantityWriter.Serialize(Read(Fixture), rows));
        });
        Test("X31 export rounds to at most three fractional places using commercial midpoint rounding", () =>
        {
            var values = new[] { 1.2344m, 1.2345m, -1.2345m, 0.0004m, 0.0005m, -0.0005m, 25.5m };
            var rows = values.Select((value, index) => new QuantityTakeoffRow("FirstItem", value, "m2", "", (100 + index).ToString(CultureInfo.InvariantCulture))).ToArray();
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                var xml = X31Writer.Serialize(Read(Fixture), rows); Validate(xml);
                var numeric = XDocument.Parse(xml).Descendants(Ns + "QTakeoff")
                    .Select(x => x.Attribute("Row")!.Value).Where(x => x.Substring(29, 2) == "91").ToArray();
                Equal(values.Length, numeric.Length);
                for (int i = 0; i < values.Length; i++)
                {
                    var expected = decimal.Round(values[i], 3, MidpointRounding.AwayFromZero)
                        .ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',') + "=";
                    var actual = numeric[i].Substring(31, 38).Trim();
                    Equal(expected, actual);
                    var fraction = actual.Split('=')[0].Split(',').Skip(1).FirstOrDefault() ?? "";
                    Require(fraction.Length <= 3, "Export contains more than three fractional digits");
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("X31 formula approach rounds every operand and result to three decimals", () =>
        {
            var row = new QuantityTakeoffRow("FirstItem", 1.2345m, "m2", "", "1",
                FormulaExpression: "A", CalculationApproach: "1.2345");
            var xml = X31Writer.Serialize(Read(Fixture), [row]); Validate(xml);
            var doc = XDocument.Parse(xml);
            var calculation = doc.Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!)
                .Single(x => x.Substring(29, 2) == "91");
            Equal("(ID: 1) A", calculation.Substring(13, 9).TrimEnd());
            Equal("1,235=", calculation.Substring(31, 38).TrimEnd());
            Require(!doc.Descendants().Any(element => element.Name.NamespaceName == "BVBS"), "X31 formula metadata used a non-standard extension.");
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var review = X31ReviewReader.Read(input, evaluateReb91: X31Writer.EvaluateReb91);
            Equal("1,235", review.Rows.Single(x => x.Kind == X31ReviewRowKind.Quantity).Result);
        });
        Test("X31 long symbolic formulas split into plain comments and keep the tail in the calculation remark", () =>
        {
            var formula = string.Join("+", Enumerable.Repeat("A", 30));
            var approach = string.Join("+", Enumerable.Repeat("1", 30));
            var row = new QuantityTakeoffRow("FirstItem", 30m, "m2", "", "42",
                FormulaExpression: formula, CalculationApproach: approach);
            var xml = X31Writer.Serialize(Read(Fixture), [row]); Validate(xml);
            var raw = XDocument.Parse(xml).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            var calculation = raw.First(x => x.Substring(29, 2) == "91");
            var comments = raw.Where(x => x[12] == '*').Select(x => x.Substring(13, 56).TrimEnd()).ToArray();
            Equal("(ID: 42)", comments[0]);
            Require(comments.Skip(1).All(x => !x.StartsWith("Formel:", StringComparison.Ordinal)), "Formula label must not be emitted.");
            Equal(formula, string.Concat(comments.Skip(1)) + calculation.Substring(13, 9).TrimEnd());
            Require(!XDocument.Parse(xml).Descendants().Any(element => element.Name.NamespaceName == "BVBS"), "X31 formula used a non-standard extension.");
        });
        Test("X31 preserves formula whitespace at 56-character comment boundaries", () =>
        {
            var term = "[" + new string('A', 50) + " long name]";
            var formula = term + " + [B] + [C]";
            Equal(' ', formula[56]);
            var row = new QuantityTakeoffRow("FirstItem", 3m, "m2", "", "4294967296",
                FormulaExpression: formula, CalculationApproach: "1+1+1");
            var xml = X31Writer.Serialize(Read(Fixture), [row]); Validate(xml);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var review = X31ReviewReader.Read(input, evaluateReb91: X31Writer.EvaluateReb91);
            Equal(formula, review.Rows.Single(x => x.Kind == X31ReviewRowKind.Quantity).Comment);
            Equal("4294967296", review.Rows.Single(x => x.Kind == X31ReviewRowKind.Quantity).ElementId);
        });
        Test("X31 writes the compact Revit ID in the REB remark or its preceding comment", () =>
        {
            var explanation = "Revit ElementId=42 UniqueId=source&<element>; " + new string('x', 130);
            var template = Read(Fixture);
            var xml = X31Writer.Serialize(template, [new("FirstItem", 2.5m, "m2", explanation, "42", "Erdgeschoss", "Baubereich"), new("SecondItem", 0.0045m, "m3", "same Revit ElementId=42", "43")]);
            Validate(xml);
            var doc = XDocument.Parse(xml);
            var first = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "FirstItem");
            var firstItems = first.Descendants(Ns + "QDetermItem").ToArray();
            var lines = firstItems.Select(x => x.Element(Ns + "QTakeoff")!.Attribute("Row")!.Value).ToArray();
            Equal(2, lines.Length);
            Require(lines[0][12] == '*' && lines[0].Substring(13, 56).TrimEnd() == "Baubereich: Erdgeschoss", "Grouping row does not match screenshot layout");
            var firstQuantity = lines.Single(x => x[12] != '*');
            Equal("(ID: 42)", firstQuantity.Substring(13, 9).Trim());
            Equal("2,5=", firstQuantity.Substring(31, 38).Trim());

            var second = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "SecondItem");
            var secondItems = second.Descendants(Ns + "QDetermItem").ToArray();
            var secondRows = secondItems.Select(x => x.Element(Ns + "QTakeoff")!.Attribute("Row")!.Value).ToArray();
            Equal(1, secondRows.Length);
            var secondQuantity = secondRows.Single(row => row[12] != '*');
            Equal("(ID: 43)", secondQuantity.Substring(13, 9).Trim());
            Equal("0,005=", secondQuantity.Substring(31,38).Trim());

            Require(lines.All(x => x.Length == 80), "Line width changed");
            Equal("m2", template.LvCatalog.Entries[0].Unit);
            Require(!xml.Contains("QU>") && !xml.Contains(explanation), "Non-screenshot source details or reassigned LV unit leaked");
        });
        Test("X31 writes a compact labelled ID in one calculation row when it fits", () =>
        {
            var xml = X31Writer.Serialize(Read(Fixture), [new("FirstItem", 2.5m, "m2", "", "42")]);
            var rows = XDocument.Parse(xml).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Equal(1, rows.Length);
            Equal("(ID: 42)", rows[0].Substring(13, 9).Trim());
            Require(rows.All(row => row[12] != '*'), "A separate ElementID comment row was created");
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var review = X31ReviewReader.Read(input);
            Equal(1, review.Rows.Count);
            Equal("42", review.Rows[0].ElementId);
            Equal("(ID: 42)", review.Rows[0].Comment);
        });
        Test("X31 rejects an ElementID that cannot be represented by a Revit 64-bit ID", () =>
        {
            Reject(() => X31Writer.Serialize(Read(Fixture), [new("FirstItem", 2.5m, "m2", "", new string('9', 40))]));
        });
        Test("X31 retains a valid long ElementID in a preceding labelled comment", () =>
        {
            var xml = X31Writer.Serialize(Read(Fixture), [new("FirstItem", 1m, "m2", "", "4294967296")]);
            Validate(xml);
            var rows = XDocument.Parse(xml).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Equal(2, rows.Length); Equal("(ID: 4294967296)", rows[0].Substring(13, 56).TrimEnd());
            Equal("", rows[1].Substring(13, 9).Trim());
            Equal("91", rows[1].Substring(29, 2));
        });
        Test("X31 emits standard group headers per position and keeps IDs with calculations", () =>
        {
            var rows = new[]
            {
                new QuantityTakeoffRow("FirstItem", 1m, "m2", "trace 101", "101", new string('x', 56 - "Baubereich: ".Length), "Baubereich"),
                new QuantityTakeoffRow("FirstItem", 2m, "m2", "trace 102", "102", new string('x', 56 - "Baubereich: ".Length), "Baubereich"),
                new QuantityTakeoffRow("FirstItem", 3m, "m2", "trace 103", "103", "M-Technik2", "Baubereich"),
                new QuantityTakeoffRow("FirstItem", 4m, "m2", "trace 104", "104", "M-Technik2", "Baubereich"),
                new QuantityTakeoffRow("SecondItem", 5m, "m3", "trace 105", "105", new string('x', 56 - "Baubereich: ".Length), "Baubereich"),
                new QuantityTakeoffRow("SecondItem", 6m, "m3", "trace 106", "106", new string('x', 56 - "Baubereich: ".Length), "Baubereich"),
                new QuantityTakeoffRow("SecondItem", 7m, "m3", "trace 107", "107", "M-Technik2", "Baubereich"),
                new QuantityTakeoffRow("SecondItem", 8m, "m3", "trace 108", "108", "M-Technik2", "Baubereich")
            };
            var xml = X31Writer.Serialize(Read(Fixture), rows); Validate(xml);
            var doc = XDocument.Parse(xml);
            var first = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "FirstItem");
            var firstRows = first.Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            var comments = firstRows.Where(x => x[12] == '*' && x.Substring(13, 56).StartsWith("Baubereich:", StringComparison.Ordinal)).Select(x => x.Substring(13, 56).TrimEnd()).ToArray();
            Equal(2, comments.Length);
            Equal(new string('x', 56 - "Baubereich: ".Length), comments.Single(x => x.StartsWith("Baubereich: x", StringComparison.Ordinal))["Baubereich: ".Length..]);
            foreach (var row in firstRows.Where(x => x[12] != '*'))
                Require(row.Substring(13, 9).Trim() is "(ID: 101)" or "(ID: 102)" or "(ID: 103)" or "(ID: 104)", "Quantity remark lost the full labelled Revit ID");
            Require(firstRows.Where(x => x[12] != '*').Any(x => x.Substring(31, 38).Trim() == "1="), "Expected the 1-unit formula line");
            var second = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "SecondItem");
            var secondItems = second.Descendants(Ns + "QDetermItem").ToArray();
            Equal(6, secondItems.Length);
            var secondQuantities = secondItems.Where(x => x.Element(Ns + "QTakeoff")!.Attribute("Row")!.Value[12] != '*').ToArray();
            var expectedIds = new[] { "(ID: 105)", "(ID: 106)", "(ID: 107)", "(ID: 108)" };
            var actualIds = secondQuantities.Select(x => x.Element(Ns + "QTakeoff")!.Attribute("Row")!.Value.Substring(13, 9).Trim()).ToArray();
            Require(expectedIds.ToHashSet(StringComparer.Ordinal).SetEquals(actualIds.OfType<string>()), $"Second item ElementIDs changed: {string.Join(",", actualIds)}");
            var allHeaders = doc.Descendants(Ns + "QTakeoff").Select(x => x.Attribute("Row")!.Value)
                .Where(x => x[12] == '*' && x.Substring(13, 56).StartsWith("Baubereich:", StringComparison.Ordinal)).ToArray();
            Equal(4, allHeaders.Length);
            var ungrouped = X31Writer.Serialize(Read(Fixture), [new("FirstItem", 1m, "m2", "trace", "77")]);
            var ungroupedRows = XDocument.Parse(ungrouped).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Equal(1, ungroupedRows.Length);
            Equal("(ID: 77)", ungroupedRows.Single(row => row[12] != '*').Substring(13, 9).Trim());
        });
        Test("X31 review associates a group comment with all full ElementIDs across positions", () =>
        {
            var rows = new[]
            {
                new QuantityTakeoffRow("FirstItem", 1m, "m2", "", "101", "M-Technik1", "Baubereich"),
                new QuantityTakeoffRow("FirstItem", 2m, "m2", "", "102", "M-Technik1", "Baubereich"),
                new QuantityTakeoffRow("FirstItem", 3m, "m2", "", "103", "Kälte", "Gewerk"),
                new QuantityTakeoffRow("SecondItem", 1.2345m, "m3", "", "429496729", "M-Technik1", "Baubereich"),
                new QuantityTakeoffRow("SecondItem", 5m, "m3", "", "105", "Kälte", "Gewerk")
            };
            var xml = X31Writer.Serialize(Read(Fixture), rows);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var review = X31ReviewReader.Read(input, "technical-fixture.x31");
            var groups = review.Rows.Where(row => row.Kind == X31ReviewRowKind.GroupComment
                && row.GroupKind == "Baubereich" && row.GroupLabel == "M-Technik1").ToArray();
            Equal(1, groups.Length);
            var expected = new[] { "101", "102", "429496729" };
            foreach (var group in groups)
                Require(new HashSet<string>(group.ElementIdsForSelection, StringComparer.Ordinal).SetEquals(expected),
                    "Group comment did not aggregate all full ElementIDs across LV positions.");
            var quantity = review.Rows.Single(row => row.Kind == X31ReviewRowKind.Quantity && row.ElementId == "429496729");
            Equal("1,235=", quantity.Formula);
            Require(!string.IsNullOrWhiteSpace(quantity.Position), "LV position was not preserved for the table.");
            Require(quantity.ElementIdsForSelection.SequenceEqual(["429496729"]), "Quantity row did not retain its full ElementID.");
        });
        Test("X31 review evaluates the actual serialized multirow formula with the emitted-subset evaluator", () =>
        {
            var approach = string.Join("+", Enumerable.Repeat("1,123456789", 12));
            var roundedApproach = string.Join("+", Enumerable.Repeat("1,123", 12));
            var value = X31Writer.EvaluateReb91(roundedApproach);
            var xml = X31Writer.Serialize(Read(Fixture), [new("FirstItem", value, "m2", "", "4294967296", "EG", "Baubereich",
                "[Parameter mit langem Namen]*12", approach)]);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var read = typeof(X31ReviewReader).GetMethods().SingleOrDefault(m => m.Name == "Read" && m.GetParameters().Length == 4);
            Require(read != null, "The read-only review needs an optional emitted-subset evaluator injection.");
            var review = (X31ReviewDocument)read!.Invoke(null, [input, "formula.x31", null, (Func<string, decimal>)X31Writer.EvaluateReb91])!;
            var quantities = review.Rows.Where(r => r.Kind == X31ReviewRowKind.Quantity).ToArray();
            Equal(1, quantities.Length);
            Equal(decimal.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.GetCultureInfo("de-DE")), quantities[0].Result);
            Equal(roundedApproach + "=", quantities[0].LogicalApproach);
            Require(review.Rows.Any(r => r.Kind.ToString() == "Continuation"), "A physical continuation was flattened or lost.");
            Require(review.Rows.Where(r => r.FormulaNumber == "91").All(r => r.ElementId == "4294967296"
                && r.ElementIdsForSelection.SequenceEqual(["4294967296"]) && r.GroupLabel == "EG"), "Continuation selection lost provenance.");
            Require(review.Rows.Where(r => r.Kind.ToString() == "Continuation").All(r => r.Result.Length == 0), "Continuation duplicates the total.");
            Equal(0, review.Warnings.Count);
        });
        Test("X31 selection planner de-duplicates IDs and expands nested groups without cycles", () =>
        {
            var groupMembers = new Dictionary<long, long[]>
            {
                [10] = [11, 12],
                [11] = [13],
                [13] = [10]
            };
            var expanded = X31ElementSelectionPlanner.ExpandGroupMembers([10L, 12L], id =>
                groupMembers.TryGetValue(id, out var members) ? members : null);
            Require(expanded.SequenceEqual([10L, 11L, 12L, 13L]), "Nested group expansion lost members or did not stop on a cycle.");

            var first = new X31ReviewRow(1, "1", "item", X31ReviewRowKind.Quantity, "", "1=", "42", null, null, ["42"], false);
            var second = first with { Number = 2, ElementIdsForSelection = ["42", "43"], SelectionIncomplete = true };
            var plan = X31ElementSelectionPlanner.FromRows([first, second]);
            Require(plan.ElementIds.SequenceEqual(["42", "43"]) && plan.SelectionIncomplete,
                "Selection plan must de-duplicate element IDs and preserve incomplete-group state.");
        });
        Test("X31 preflight reports distinct missing active-model ElementIDs before opening the review", () =>
        {
            var row = new X31ReviewRow(1, "1", "item", X31ReviewRowKind.Quantity, "", "1=", null, null, null, ["42", "43", "42", "44"], false);
            var review = new X31ReviewDocument("preflight.x31", [row], []);
            var missing = X31ReviewReader.FindMissingElementIds(review, id => id == "42");
            Require(missing.SequenceEqual(["43", "44"]), "Missing ElementIDs must be distinct, sorted, and name every missing ID.");
        });
        if (Environment.GetEnvironmentVariable("BIMASSIST_AVA_X31_REVIEW_FILES") is { Length: > 0 } reviewFiles)
        {
            foreach (var path in reviewFiles.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Test("X31 review parses supplied real file " + Path.GetFileName(path), () =>
                {
                    var review = X31ReviewReader.ReadFile(path);
                    Require(review.Rows.Count > 0, "The real X31 file produced no table rows.");
                    var knownIds = review.Rows.SelectMany(row => row.ElementIdsForSelection).Distinct(StringComparer.Ordinal).Count();
                    Console.WriteLine($"  {review.Rows.Count} Zeilen; {knownIds} unterschiedliche ElementIDs; {review.Warnings.Count} Hinweise");
                });
        }
        Test("blank unknown null and empty row input fail with domain errors", () =>
        {
            var t = Read(Fixture);
            Reject(() => X31Writer.Serialize(t, []));
            Reject(() => X31Writer.Serialize(null!, [new("FirstItem", 1, "m", "")]));
            Reject(() => X31Writer.Serialize(t, null!));
            foreach (var id in new[] { "", " ", "unknown", null! }) Reject(() => X31Writer.Serialize(t, [new(id, 1, "m", "")]));
            Reject(() => X31Writer.Serialize(t, [null!]));
            foreach (var unit in new[] { "", " ", "kg\n91", null!, new string('u', 41) }) Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, unit, "")]));
            Require(X31Writer.Serialize(t, [new("FirstItem", 1, "m2", "", "429496729")]).Contains("(ID: 429496729)"),
                "Full Revit ID was not retained in standard REB rows");
            foreach (var explanation in new[] { null!, "a\nb", "a\tb", "\0", "\uD800", new string('e', 4097) }) Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, "m", explanation)]));
        });
        Test("embedded snapshot is mandatory bounded hash-verified safe XML and never a filename fallback", () =>
        {
            var t = Read(Fixture);
            t.LvCatalog = t.LvCatalog with { Source = t.LvCatalog.Source with { X82SnapshotBase64 = null } };
            Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, "m", "")]));
            foreach (var encoded in new[] { "!", "", Convert.ToBase64String(Encoding.UTF8.GetBytes(Fixture + " ")) })
            {
                t = Read(Fixture); t.LvCatalog = t.LvCatalog with { Source = t.LvCatalog.Source with { X82SnapshotBase64 = encoded } };
                Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, "m", "")]));
            }
            foreach (var xml in new[] { "<!DOCTYPE GAEB [<!ENTITY a SYSTEM 'file:///do-not-read'>]>" + Fixture, Fixture.Replace(SourceNs.NamespaceName, "urn:wrong"), "<broken" })
            {
                t = WithSnapshot(Read(Fixture), xml);
                Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, "m", "")]));
            }
            t = Read(Fixture); t.LvCatalog = t.LvCatalog with { Source = t.LvCatalog.Source with { Sha256 = "not a hash" } };
            Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, "m", "")]));
        });
        Test("catalog entries must remain a complete exact view of immutable snapshot identity and hierarchy", () =>
        {
            Action<AvaTemplate>[] changes = [
                t => t.LvCatalog.Entries.Add(t.LvCatalog.Entries[0]),
                t => t.LvCatalog.Entries.RemoveAt(1),
                t => t.LvCatalog.Entries[0] = t.LvCatalog.Entries[0] with { BoqId = "wrong" },
                t => t.LvCatalog.Entries[0] = t.LvCatalog.Entries[0] with { NumberParts = ["1", "0010"] },
                t => t.LvCatalog.Entries[0] = t.LvCatalog.Entries[0] with { Ordinal = "wrong" },
                t => t.LvCatalog.Entries[0] = t.LvCatalog.Entries[0] with { Unit = "kg" },
                t => t.LvCatalog.Entries[0] = null!
            ];
            foreach (var change in changes) { var t = Read(Fixture); change(t); Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, "m", "")])); }
        });
        Test("malformed source hierarchy and breakdown cannot be silently repaired or flattened", () =>
        {
            Func<XDocument, string>[] changes = [
                d => { d.Descendants(SourceNs+"BoQInfo").Remove(); return d.ToString(); },
                d => { var b=d.Descendants(SourceNs+"BoQ").Single(); b.Add(new XElement(b.Element(SourceNs+"BoQInfo")!)); return d.ToString(); },
                d => { d.Descendants(SourceNs+"BoQCtgy").Single().SetAttributeValue("ID", "FirstItem"); return d.ToString(); },
                d => { d.Descendants(SourceNs+"BoQCtgy").Single().SetAttributeValue("RNoPart", "001"); return d.ToString(); },
                d => { d.Descendants(SourceNs+"BoQCtgy").Single().SetAttributeValue("RNoPart", "AB"); return d.ToString(); },
                d => { var item=d.Descendants(SourceNs+"Item").First(); item.SetAttributeValue("RNoPart", "00010"); return d.ToString(); },
                d => { var item=d.Descendants(SourceNs+"Item").First(); item.SetAttributeValue("RNoIndex", "A"); return d.ToString(); },
                d => { var body=d.Descendants(SourceNs+"BoQBody").First(); var branch=new XElement(body.Elements().First()); branch.SetAttributeValue("ID", "SecondCategory"); branch.SetAttributeValue("RNoPart", "1"); branch.Descendants(SourceNs+"Itemlist").Remove(); body.Add(branch); return d.ToString(); },
                d => { var item=d.Descendants(SourceNs+"Item").Last(); item.SetAttributeValue("RNoPart", "10"); return d.ToString(); },
                d => { var item=d.Descendants(SourceNs+"Item").Last(); item.Remove(); d.Descendants(SourceNs+"BoQ").Single().Add(item); return d.ToString(); },
                d => { var boq=d.Descendants(SourceNs+"BoQ").Single(); var second=new XElement(boq); second.SetAttributeValue("ID", "AnotherBoQ"); second.Descendants(SourceNs+"Item").Remove(); boq.Parent!.Add(second); return d.ToString(); }
            ];
            foreach(var change in changes)
            {
                var xml=change(XDocument.Parse(Fixture));
                var t=WithSnapshot(Read(Fixture),xml);
                // Keep the imported catalog consistent so this tests structure, not catalog mismatch.
                try { t=Read(xml); } catch(InvalidDataException) { }
                Reject(() => X31Writer.Serialize(t,[new("FirstItem",1,"m","")]));
            }
        });
        Test("REB address exhaustion is rejected before enumerating an impossible number of quantities", () =>
        {
            var method = typeof(X31Writer).GetMethod("FormatAddress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Require(method != null, "Bounded address formatter is missing");
            foreach (var (ordinal, address) in new[] { (0, "   1A0"), (9, "   1A9"), (10, "   1B0"), (259, "   1Z9"), (260, "   2A0"), (2599739, "9999Z9") })
                Equal(address, (string)method!.Invoke(null, [ordinal])!);
            foreach (var ordinal in new[] { -1, 2599740, int.MaxValue })
            {
                try { method!.Invoke(null, [ordinal]); throw new Exception("Address overflow accepted"); }
                catch(System.Reflection.TargetInvocationException e) when(e.InnerException is InvalidDataException) { }
            }
            Reject(() => X31Writer.Serialize(Read(Fixture), new ImpossibleRows()));
            var xml = X31Writer.Serialize(Read(Fixture), Enumerable.Range(0,150).Select(i => new QuantityTakeoffRow("FirstItem", i, "m2", "ref")).ToArray());
            Validate(xml);
            var lines = XDocument.Parse(xml).Descendants(Ns+"QTakeoff").Select(x => x.Attribute("Row")!.Value).ToArray();
            Equal(lines.Length, lines.Select(x => x.Substring(69,6)).Distinct().Count());
            Require(lines.All(x => x.Length == 80 && x.Take(11).All(c => c == ' ') && x.Substring(75) == "     "), "Invalid fixed fields");
        });
        Test("REB literal preserves decimal extremes culture independently and text cannot become a directive", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "de-DE", "fr-FR" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    foreach (var value in new[] { decimal.MinValue, decimal.MaxValue, 0m, -0.0000000000000000000000000001m, 0.0000000000000000000000000001m })
                    {
                        var explanation = "#Bild ../../private.png & <tag> \"quoted\" " + new string('x', 4000);
                        var xml = X31Writer.Serialize(Read(Fixture), [new("FirstItem", value, "m2", explanation, "42")]);
                        Validate(xml);
                        var lines = XDocument.Parse(xml).Descendants(Ns+"QTakeoff").Select(x => x.Attribute("Row")!.Value).ToArray();
                        Require(!xml.Contains(explanation), "Internal formula explanation leaked into screenshot-oriented output");
                        var numeric = lines.Single(x => x.Substring(29, 2) == "91");
                        var numericItem = XDocument.Parse(xml).Descendants(Ns + "QDetermItem")
                            .Single(x => x.Element(Ns + "QTakeoff")!.Attribute("Row")!.Value == numeric);
                        Require(numericItem.Element(XName.Get("Explanation", "BVBS")) == null,
                            "Strict X31 quantity rows must not rely on the non-standard BVBS extension.");
                        Require(numeric[..12].All(x => x == ' ') && numeric.Substring(13, 9).Trim() == "(ID: 42)"
                            && numeric.Substring(22, 7).All(x => x == ' '), "Unexpected quantity remark/sign prefix");
                        Equal("91", numeric.Substring(29, 2));
                        Equal(decimal.Round(value, 3, MidpointRounding.AwayFromZero), decimal.Parse(numeric.Substring(31, 38).Trim().TrimEnd('='), CultureInfo.GetCultureInfo("de-DE")));
                        Require(lines.All(x => x.Length == 80), "Literal crossed field boundary");
                    }
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("long internal explanations do not consume export addresses or leak into California", () =>
        {
            var explanation = new string('x', 4096);
            var xml = X31Writer.Serialize(Read(Fixture), [new QuantityTakeoffRow("FirstItem", 1m, "m2", explanation, "42")]);
            Validate(xml);
            var rows = XDocument.Parse(xml).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Equal(1, rows.Length);
            Equal("(ID: 42)", rows.Single(row => row[12] != '*').Substring(13, 9).Trim());
            Require(!xml.Contains(explanation), "Internal explanation leaked into output");
        });
        Test("group labels that exceed the standard REB comment width are rejected, not truncated", () =>
        {
            const string groupPrefix = "Baubereich: ";
            var maxLabelLength = 56 - groupPrefix.Length;
            var exactFit = new string('x', maxLabelLength);
            var xml = X31Writer.Serialize(Read(Fixture), [new("FirstItem", 1m, "m2", "", "42", exactFit, "Baubereich")]);
            Validate(xml);
            var comments = XDocument.Parse(xml).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!)
                .Where(row => row[12] == '*' && row.Substring(13, 56).StartsWith("Baubereich:", StringComparison.Ordinal))
                .Select(row => row.Substring(13, 56)).ToArray();
            Equal(1, comments.Length);
            Equal(56, comments[0].Length);
            foreach (var suffix in new[] { "A", "B" })
            {
                var colliding = exactFit + suffix;
                Reject(() => X31Writer.Serialize(Read(Fixture), [new("FirstItem", 1m, "m2", "", "42", colliding, "Baubereich")]));
            }
        });
        Test("optional X82 metadata does not weaken strict legacy schema3 deserialization", () =>
        {
            var original = TemplateJson.Serialize(Read(Fixture));
            foreach (var mutation in new Action<JsonObject>[] {
                root => root["LvCatalog"]!["Source"]!["Unexpected"] = "not accepted",
                root => root["LvCatalog"]!["Source"]!["X82SnapshotBase64"] = null,
                root => root["LvCatalog"]!["Source"]!["X82SnapshotBase64"] = 7,
                root => root["SchemaVersion"] = 4 })
            {
                var root = JsonNode.Parse(original)!.AsObject(); mutation(root);
                Reject(() => TemplateJson.Deserialize(root.ToJsonString()));
            }
            var legacy = JsonNode.Parse(original)!.AsObject();
            legacy["LvCatalog"]!["Source"]!.AsObject().Remove("X82SnapshotBase64");
            var loaded = TemplateJson.Deserialize(legacy.ToJsonString());
            try { X31Writer.Serialize(loaded, [new("FirstItem", 1m, "m", "")]); throw new Exception("Legacy snapshot absence was guessed"); }
            catch (InvalidDataException ex) { Require(ex.Message.Contains("erneut importieren"), "Missing explicit reimport guidance"); }
        });
        Test("Save roundtrips verified UTF8 DA31 and atomically replaces an existing target", () =>
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "technical-fixture.x31");
                var template = Read(Fixture);
                QuantityTakeoffRow[] rows = [new("FirstItem", 12.345m, "m2", "TECHNICAL FIXTURE, NOT MODEL DATA; Grüße & <source>")];
                Save(path, template, rows);
                var expected = X31Writer.Serialize(template, rows);
                Equal(expected, File.ReadAllText(path, new UTF8Encoding(false, true)));
                Validate(File.ReadAllText(path));
                Require(!File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "Unexpected BOM");
                Save(path, template, [new("SecondItem", -0.0005m, "m3", "replacement fixture")]);
                var saved = File.ReadAllText(path);
                Validate(saved);
                Require(saved.Contains("-0,001="), "Saved decimal was not rounded to three places");
                Equal(1, Directory.GetFiles(directory).Length);
            });
        });
        Test("Save preserves exact existing bytes on invalid data and locked-target IO failures and cleans temporary files", () =>
        {
            WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "keep.x31");
                var original = new byte[] { 0, 255, 1, 2, 128, 3 };
                File.WriteAllBytes(path, original);
                Reject(() => Save(path, Read(Fixture), [new("unknown", 1, "m", "")]));
                Require(original.SequenceEqual(File.ReadAllBytes(path)), "Invalid input changed existing target");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { Save(path, Read(Fixture), [new("FirstItem", 1, "m2", "locked fixture")]); throw new Exception("Expected locked-target IO error"); }
                    catch (IOException) { }
                }
                Require(original.SequenceEqual(File.ReadAllBytes(path)), "IO failure changed existing target");
                Equal(1, Directory.GetFiles(directory).Length);
            });
        });
        if (args.Length != 0)
        {
            if (args.Length != 4 || args[0] != "--acceptance") throw new ArgumentException("Usage: --acceptance <fresh-schema-directory> <read-only-attachments-directory> <new-scratch-output.x31>");
            Test("independent supplied-reference acceptance and synthetic technical X31 artifact", () => IndependentAcceptance.Run(args[1], args[2], args[3]));
        }
        Console.WriteLine($"{passed} passed; {failed} failed"); return failed == 0 ? 0 : 1;
    }
    sealed class ImpossibleRows(int count = 2599741) : IReadOnlyList<QuantityTakeoffRow>
    {
        public int Count => count;
        public QuantityTakeoffRow this[int i] => throw new Exception("Overflow preflight must run before enumeration");
        public IEnumerator<QuantityTakeoffRow> GetEnumerator() => throw new Exception("Overflow preflight must run before enumeration");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    static void WithDirectory(Action<string> action)
    {
        var scratch = Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath();
        var directory = Path.Combine(scratch, "AvaX31Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
    static void Save(string path, AvaTemplate template, IReadOnlyList<QuantityTakeoffRow> rows)
    {
        var method = typeof(X31Writer).GetMethod("Save", [typeof(string), typeof(AvaTemplate), typeof(IReadOnlyList<QuantityTakeoffRow>)]);
        Require(method != null, "Public Save(string, AvaTemplate, IReadOnlyList<QuantityTakeoffRow>) is missing");
        try { method!.Invoke(null, [path, template, rows]); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    static void Validate(string xml)
    {
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(Ns.NamespaceName, Path.Combine(AppContext.BaseDirectory, "Fixtures/Official/GAEB_DA_XML_31_3.3_2023-01.xsd"));
        schemas.Compile();
        var errors = new List<string>();
        var document = XDocument.Parse(xml);
        document.Validate(schemas, (_, e) => errors.Add(e.Message));
        Require(errors.Count == 0, string.Join("\n", errors));
    }
    static void ValidateX82(XDocument document)
    {
        var schemaPath = Environment.GetEnvironmentVariable("BIMASSIST_AVA_X82_SCHEMA");
        if (string.IsNullOrWhiteSpace(schemaPath)) return;
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(SourceNs.NamespaceName, schemaPath);
        schemas.Compile();
        var errors = new List<string>();
        document = new XDocument(document);
        document.Descendants(XName.Get("Explanation", "BVBS")).Remove();
        document.Validate(schemas, (_, e) => errors.Add(e.Message), true);
        Require(errors.Count == 0, string.Join("\n", errors));
    }
    static AvaTemplate WithSnapshot(AvaTemplate t, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        t.LvCatalog = t.LvCatalog with { Source = t.LvCatalog.Source with { X82SnapshotBase64 = Convert.ToBase64String(bytes), Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } }; return t;
    }
    static AvaTemplate Read(string xml) => new() { LvCatalog = CatalogReaders.ReadX82(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "missing/portable-source.x82") };
    static void Test(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch(Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
    static void Require(bool condition, string message) { if(!condition) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) { if(!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    static void Reject(Action action) { try { action(); } catch(InvalidDataException) { return; } throw new Exception("Expected InvalidDataException"); }
}
