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
      <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA82/3.3"><Award><DP>82</DP><BoQ ID="OriginalBoQ">
      <BoQInfo><Name>Source only</Name><Date>2026-01-01</Date>
      <BoQBkdn><Type>BoQLevel</Type><Length>2</Length><Num>Yes</Num></BoQBkdn>
      <BoQBkdn><Type>Item</Type><Length>4</Length><Num>Yes</Num></BoQBkdn></BoQInfo>
      <BoQBody><BoQCtgy ID="OriginalCategory" RNoPart="01"><BoQBody><Itemlist>
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
        Test("production X31 independently validates against unmodified official 2023-01 schema", () =>
        {
            var type = typeof(AvaTemplate).Assembly.GetType("BIMassist.Ava.Core.X31Writer");
            Require(type != null, "X31Writer is missing");
            var rowType = typeof(AvaTemplate).Assembly.GetType("BIMassist.Ava.Core.QuantityTakeoffRow")!;
            var rows = Array.CreateInstance(rowType, 1);
            rows.SetValue(Activator.CreateInstance(rowType, "FirstItem", 12.345m, "m2", "Revit ElementId=42", "", null, null), 0);
            var xml = (string)type!.GetMethod("Serialize")!.Invoke(null, [Read(Fixture), rows])!;
            Validate(xml);
            var root = XDocument.Parse(xml);
            Equal("OriginalBoQ", (string?)root.Descendants(Ns + "BoQ").Single().Attribute("ID"));
            Equal("01", (string?)root.Descendants(Ns + "BoQCtgy").Single().Attribute("RNoPart"));
            var item = root.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "FirstItem");
            Equal("0010", (string?)item.Attribute("RNoPart"));
            var numeric = item.Descendants(Ns + "QTakeoff").Select(x => x.Attribute("Row")!.Value).Single(x => x.Substring(29, 2) == "91");
            Equal(80, numeric.Length); Equal("12,345=", numeric.Substring(31, 38).Trim());
            Require(!xml.Contains("999") && !xml.Contains("do not copy me") && !xml.Contains("Available"), "Exemplar values or certificate leaked");
        });
        Test("Serialize fails closed on official-schema-invalid output structure", () =>
        {
            Reject(() => X31Writer.Serialize(Read(Fixture.Replace("RNoPart=\"0010\"", "RNoPart=\"bad!\"")), [new("FirstItem", 1m, "m2", "")]));
            var missingBreakdown = XDocument.Parse(Fixture);
            missingBreakdown.Descendants(SourceNs + "BoQBkdn").Remove();
            Reject(() => X31Writer.Serialize(Read(missingBreakdown.ToString()), [new("FirstItem", 1m, "m2", "")]));
        });
        Test("X31 puts Revit ID in quantity-row remark field and emits grouping remarks only when grouped", () =>
        {
            var explanation = "Revit ElementId=42 UniqueId=source&<element>; " + new string('x', 130);
            var template = Read(Fixture);
            var xml = X31Writer.Serialize(template, [new("FirstItem", 2.5m, "kg", explanation, "42", "Erdgeschoss", "Baubereich"), new("SecondItem", 0.0045m, "m", "same Revit ElementId=42", "43")]);
            Validate(xml);
            var doc = XDocument.Parse(xml);
            var first = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "FirstItem");
            var lines = first.Descendants(Ns + "QTakeoff").Select(x => x.Attribute("Row")!.Value).ToArray();
            Equal(2, lines.Length);
            Require(lines[0][12] == '*' && lines[0].Substring(13, 56).TrimEnd() == "Baubereich: Erdgeschoss", "Grouping row does not match screenshot layout");
            var firstQuantity = lines.Single(x => x[12] != '*');
            Equal("42", firstQuantity.Substring(13, 9).Trim());
            Equal("2,5=", firstQuantity.Substring(31, 38).Trim());
            var second = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "SecondItem");
            var secondRows = second.Descendants(Ns + "QTakeoff").Select(x => x.Attribute("Row")!.Value).ToArray();
            Equal(1, secondRows.Length);
            var secondQuantity = secondRows.Single();
            Equal("43", secondQuantity.Substring(13, 9).Trim());
            Equal("0,0045=", secondQuantity.Substring(31,38).Trim());
            Require(lines.All(x => x.Length == 80), "Line width changed");
            Equal("m2", template.LvCatalog.Entries[0].Unit);
            Require(!xml.Contains("BVBS") && !xml.Contains("QU>") && !xml.Contains(explanation), "Non-screenshot source details or reassigned LV unit leaked");
        });
        Test("X31 groups element quantities within each LV position and emits group and Revit-ID remarks", () =>
        {
            var rows = new[]
            {
                new QuantityTakeoffRow("FirstItem", 1m, "St", "trace 101", "101", "Erdgeschoss", "Baubereich"),
                new QuantityTakeoffRow("FirstItem", 2m, "St", "trace 102", "102", "Erdgeschoss", "Baubereich"),
                new QuantityTakeoffRow("FirstItem", 3m, "St", "trace 103", "103", "Obergeschoss", "Baubereich"),
                new QuantityTakeoffRow("SecondItem", 4m, "m", "trace 104", "104", "Rohbau", "Gewerk")
            };
            var xml = X31Writer.Serialize(Read(Fixture), rows); Validate(xml);
            var doc = XDocument.Parse(xml);
            var first = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "FirstItem");
            var firstRows = first.Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            var comments = firstRows.Where(x => x[12] == '*').Select(x => x.Substring(13, 56).TrimEnd()).ToArray();
            Equal(1, comments.Count(x => x == "Baubereich: Erdgeschoss"));
            Equal(1, comments.Count(x => x == "Baubereich: Obergeschoss"));
            foreach (var row in firstRows.Where(x => x[12] != '*'))
                Require(!string.IsNullOrWhiteSpace(row.Substring(13, 9).Trim()), "Revit element ID missing from the quantity-row remark field");
            Equal("1=", firstRows.First(x => x[12] != '*').Substring(31, 38).Trim());
            var second = doc.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == "SecondItem");
            Require(second.Descendants(Ns + "QTakeoff").Select(x => x.Attribute("Row")!.Value)
                .Any(x => x[12] == '*' && x.Substring(13, 56).TrimEnd() == "Gewerk: Rohbau"), "Trade header missing");
            var ungrouped = X31Writer.Serialize(Read(Fixture), [new("FirstItem", 1m, "m", "trace", "77")]);
            var ungroupedRows = XDocument.Parse(ungrouped).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Require(!ungroupedRows.Any(x => x[12] == '*'), "Ungrouped export added a comment row");
            Require(ungroupedRows.All(x => x[12] != '*'), "Ungrouped export added a comment row");
            Equal("77", ungroupedRows.Single().Substring(13, 9).Trim());
        });
        Test("blank unknown null and empty row input fail with domain errors", () =>
        {
            var t = Read(Fixture);
            Reject(() => X31Writer.Serialize(t, []));
            Reject(() => X31Writer.Serialize(null!, [new("FirstItem", 1, "m", "")]));
            Reject(() => X31Writer.Serialize(t, null!));
            foreach (var id in new[] { "", " ", "unknown", null! }) Reject(() => X31Writer.Serialize(t, [new(id, 1, "m", "")]));
            Reject(() => X31Writer.Serialize(t, [null!]));
            foreach (var unit in new[] { "", " ", "kg\n91", null!, new string('u', 41) }) Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, unit, "")]));
            Reject(() => X31Writer.Serialize(t, [new("FirstItem", 1, "m", "", "4294967296")]));
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
            var xml = X31Writer.Serialize(Read(Fixture), Enumerable.Range(0,150).Select(i => new QuantityTakeoffRow("FirstItem", i, "m", "ref")).ToArray());
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
                        var xml = X31Writer.Serialize(Read(Fixture), [new("FirstItem", value, "m", explanation, "42")]);
                        Validate(xml);
                        var lines = XDocument.Parse(xml).Descendants(Ns+"QTakeoff").Select(x => x.Attribute("Row")!.Value).ToArray();
                        Require(!xml.Contains(explanation), "Internal formula explanation leaked into screenshot-oriented output");
                        Require(lines[0][12] == ' ' && lines[0].Substring(13, 9).Trim() == "42", "Revit ID remark field changed");
                        var numeric = lines.Single(x => x[12] != '*');
                        Require(numeric[..12].All(x => x == ' ') && numeric.Substring(13, 9).Trim() == "42"
                            && numeric.Substring(22, 7).All(x => x == ' '), "Unexpected quantity remark/sign prefix");
                        Equal("91", numeric.Substring(29, 2));
                        Equal(value, decimal.Parse(numeric.Substring(31, 38).Trim().TrimEnd('='), CultureInfo.GetCultureInfo("de-DE")));
                        Require(lines.All(x => x.Length == 80), "Literal crossed field boundary");
                    }
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("long internal explanations do not consume export addresses or leak into California", () =>
        {
            var explanation = new string('x', 4096);
            var xml = X31Writer.Serialize(Read(Fixture), [new QuantityTakeoffRow("FirstItem", 1m, "m", explanation, "42")]);
            Validate(xml);
            var rows = XDocument.Parse(xml).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Equal(1, rows.Length);
            Require(!xml.Contains(explanation), "Internal explanation leaked into output");
        });
        Test("long grouping comments cannot turn continuation chunks into REB directives", () =>
        {
            const string groupPrefix = "Baubereich: ";
            var groupLabel = new string('x', 56 - groupPrefix.Length) + "#Bild ../../group.png";
            var xml = X31Writer.Serialize(Read(Fixture), [new("FirstItem", 1m, "m", "", "42", groupLabel, "Baubereich")]);
            Validate(xml);
            var comments = XDocument.Parse(xml).Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!)
                .Where(row => row[12] == '*').Select(row => row.Substring(13, 56)).ToArray();
            Require(comments.Length >= 2, "Test input did not produce group continuation chunks");
            Require(comments.All(comment => !comment.TrimStart().StartsWith("#Bild", StringComparison.Ordinal)),
                "A continuation comment begins with a REB image directive");
            Require(comments.Any(comment => comment.StartsWith("Fortsetzung: ", StringComparison.Ordinal)),
                "Continuation chunks are not explicitly marked as safe comments");
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
                Save(path, template, [new("SecondItem", -0.0000000000000000000000000001m, "m3", "replacement fixture")]);
                var saved = File.ReadAllText(path);
                Validate(saved);
                Require(saved.Contains("-0,0000000000000000000000000001="), "Decimal was rounded");
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
                    try { Save(path, Read(Fixture), [new("FirstItem", 1, "m", "locked fixture")]); throw new Exception("Expected locked-target IO error"); }
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
        XDocument.Parse(xml).Validate(schemas, (_, e) => errors.Add(e.Message));
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
