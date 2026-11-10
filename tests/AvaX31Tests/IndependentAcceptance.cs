using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using BIMassist.Ava.Core;

/// <summary>Opt-in local acceptance using separately downloaded official schemas and read-only attachments.
/// All generated quantities are explicitly synthetic technical fixtures, never actual Revit model data.</summary>
internal static class IndependentAcceptance
{
    static readonly XNamespace SourceNs = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";
    static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";
    public static void Run(string referenceDirectory, string attachmentsDirectory, string outputPath)
    {
        var proof = new List<string> { "TECHNICAL FIXTURE ONLY — SYNTHETIC VALUES, NOT A REAL MODEL TAKEOFF OR THIRD-PARTY IMPORT CERTIFICATE." };
        foreach (var (file, field) in new[] { ("GAEB_DA_XML_31_3.3_2023-01.xsd", "MainSchema"), ("GAEB_DA_XML_Lib_3.3_2021-05.xsd", "LibrarySchema") })
        {
            var downloaded = File.ReadAllBytes(Path.Combine(referenceDirectory, file));
            var encoded = (string)typeof(X31Writer).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            using var zipped = new MemoryStream(Convert.FromBase64String(encoded));
            using var gzip = new GZipStream(zipped, CompressionMode.Decompress);
            using var decoded = new MemoryStream(); gzip.CopyTo(decoded);
            Require(downloaded.SequenceEqual(decoded.ToArray()), "Embedded schema differs from fresh official schema: " + file);
            proof.Add(file + " SHA256 " + Hash(downloaded) + "; embedded exact byte match");
        }
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(Ns.NamespaceName, Path.Combine(referenceDirectory, "GAEB_DA_XML_31_3.3_2023-01.xsd"));
        schemas.Compile();
        foreach (var sample in new[] { "Beispiel.x31", "Beispiel da11.x31", "Beispiel_1.x31" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(attachmentsDirectory, sample));
            var document = Parse(bytes); var sampleErrors = ValidationErrors(document, schemas);
            var rows = document.Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            Require(rows.Length > 0 && rows.All(x => x.Length == 80), "Sample has invalid widths");
            foreach (var row in rows)
            {
                Require(row[..12].All(c => c == ' '), "Sample has unexpected DA/OZ/V fields");
                Require(row[12] == ' ' && row.Substring(29, 2) == "91", "Sample is not expected REB91");
                Require(row.Substring(22, 7) == "       ", "Sample sign/factor fields are not blank");
                Require(row.Substring(75) == "     ", "Sample reserved fields changed");
                Require(decimal.TryParse(row.Substring(31, 38).Trim().TrimEnd('='), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.GetCultureInfo("de-DE"), out _), "Sample has non-literal calculation");
                CheckAddress(row.Substring(69, 6));
            }
            // Exact 1-based field positions are recorded, including nonblank 14..22 explanations.
            proof.Add(sample + "; SHA256 " + Hash(bytes) + "; fresh-schema errors=" + sampleErrors.Count + "; " + rows.Length + " REB91 rows, all 80 chars; " +
                "DA/OZ/V 1..12 blank; KZ 13 blank; text 14..22; sign/factor 23..29 blank; FN 30..31=91; literal 32..69; address 70..75; reserved 76..80 blank");
            foreach (var error in sampleErrors.Distinct()) proof.Add("Sample schema finding: " + sample + ": " + error);
            foreach (var row in rows.Where(x => !string.IsNullOrWhiteSpace(x.Substring(13, 9))).Take(2))
                proof.Add("Sample explanatory field " + JsonSerializer.Serialize(new { sample, text14to22 = row.Substring(13, 9), literal32to69 = row.Substring(31, 38), address70to75 = row.Substring(69, 6) }));
        }
        var sourcePath = Path.Combine(attachmentsDirectory, "StammLV Stand 09.07.2026.x82");
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var original = Parse(sourceBytes);
        using var sourceStream = new MemoryStream(sourceBytes, false);
        var catalog = CatalogReaders.ReadX82(sourceStream, sourcePath);
        Require(Hash(sourceBytes) == catalog.Source.Sha256, "X82 stamp mismatch");
        Require(Convert.FromBase64String(catalog.Source.X82SnapshotBase64!).SequenceEqual(sourceBytes), "X82 embedded bytes changed");
        var stamp = new SourceStamp("technical-fixture", "synthetic", "");
        ParameterSource Local(string name) => new() { Kind = ParameterKind.LocalFamily, Name = name, Spec = "LENGTH", Scope = ParameterScope.Instance };
        var positions = new List<MappingPosition>
        {
            new() { LvItemId = catalog.Entries[0].Id, Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 2, Unit = "St" } },
            new() { LvItemId = catalog.Entries[1].Id, Quantity = new() { Source = Local("SyntheticLength"), Unit = "cm" } },
            new() { LvItemId = catalog.Entries[2].Id, Quantity = new() { Kind = QuantityKind.Formula, Source = null, Expression = "L*W", Unit = "m2",
                Variables = [new() { Name = "L", Source = Local("SyntheticLength"), Unit = "m" }, new() { Name = "W", Source = Local("SyntheticWidth"), Unit = "m" }] } }
        };
        var template = new AvaTemplate
        {
            ReviewScope = "TECHNICAL FIXTURE ONLY — synthetic callback, no Revit model access",
            LvCatalog = catalog, BgkCatalog = new(stamp, [new("001", "Synthetic group", "Technical fixture", [new("02", "Synthetic type")])]),
            Mappings = [new() { BgkKey = "001", TypeNumber = "02", Positions = positions }]
        };
        var identity = "TECHNICAL FIXTURE — NOT REAL MODEL DATA; Dokument: synthetic-document.rvt; UniqueId: synthetic-element-42; XML & <proof>";
        var result = QuantityTakeoff.Calculate(template, [new("42", (source, unit) =>
        {
            if (ReferenceEquals(source, template.BgkIdentification)) return new(Text: "001");
            if (ReferenceEquals(source, template.TypeIdentification)) return new(Text: "02");
            return source.Name switch { "SyntheticLength" => new(Number: unit == "cm" ? 250 : 2.5m), "SyntheticWidth" => new(Number: 3), _ => throw new InvalidDataException("Unexpected fixture source") };
        }, identity)]);
        Require(result.Errors.Count == 0 && result.Rows.Count == 3, "Synthetic takeoff failed");
        Require(result.Rows.Select(x => x.Value).SequenceEqual(new[] { 2m, 250m, 7.5m }), "Independent sibling quantity changed");
        // Refuse to overwrite any user or prior output file.
        Require(!File.Exists(outputPath), "Acceptance artifact exists; choose a fresh scratch output filename");
        X31Writer.Save(outputPath, template, result.Rows.Select(x => new QuantityTakeoffRow(x.LvItemId, x.Value, x.Unit, x.Explanation,
            x.ElementId, "Erdgeschoss", "Baubereich")).ToArray());
        var savedBytes = File.ReadAllBytes(outputPath); var exported = Parse(savedBytes); Validate(exported, schemas);
        var originalHierarchy = original.Descendants().Where(IsHierarchy).Select(Describe).ToArray();
        var exportedHierarchy = exported.Descendants().Where(IsHierarchy).Select(Describe).ToArray();
        Require(originalHierarchy.SequenceEqual(exportedHierarchy), "Original X82 hierarchy, IDs or OZ changed");
        Require(original.Descendants(SourceNs + "BoQBkdn").Select(Canonical).SequenceEqual(exported.Descendants(Ns + "BoQBkdn").Select(Canonical)), "Original breakdown changed");
        var allRows = exported.Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
        Require(allRows.Select(x => x.Substring(69, 6)).Distinct().Count() == allRows.Length, "Repeated generated address");
        Require(allRows.All(x => x.Length == 80 && x[..12].All(c => c == ' ') && x.Substring(75) == "     "), "Invalid generated fixed row");
        foreach (var row in allRows) CheckAddress(row.Substring(69, 6));
        foreach (var expected in result.Rows)
        {
            var lines = exported.Descendants(Ns + "Item").Single(x => (string?)x.Attribute("ID") == expected.LvItemId)
                .Descendants(Ns + "QTakeoff").Select(x => (string)x.Attribute("Row")!).ToArray();
            var comments = lines.Where(x => x[12] == '*').Select(x => x.Substring(13, 56).TrimEnd()).ToArray();
            Require(comments.Contains("Baubereich oder Gewerk: Erdgeschoss") && comments.Contains("Revit-Element-ID: " + expected.ElementId),
                "Grouping or element reference does not match the California row layout");
            Require(!comments.Any(x => x.StartsWith("Info:", StringComparison.Ordinal) || x.Contains("UniqueId:", StringComparison.Ordinal)),
                "Verbose internal source data leaked into the California row layout");
            var numeric = lines.Single(x => x[12] != '*');
            Require(numeric[..29].All(c => c == ' ') && numeric.Substring(29, 2) == "91", "Unexpected numeric metadata");
            Require(decimal.Parse(numeric.Substring(31, 38).Trim().TrimEnd('='), CultureInfo.GetCultureInfo("de-DE")) == expected.Value, "Literal differs from precomputed decimal");
            proof.Add("Synthetic quantity: " + expected.LvItemId + " / OZ " + catalog.Entries.Single(x => x.Id == expected.LvItemId).Ordinal + " = " + expected.Value.ToString(CultureInfo.InvariantCulture) + " " + expected.Unit + "; screenshot-style group + ElementId rows");
        }
        proof.Add("X82 SHA256 " + Hash(sourceBytes) + "; " + catalog.Entries.Count + " item IDs and all original BoQ/category/item hierarchy + OZ attributes + BoQBkdn unchanged");
        proof.Add("OUTPUT " + Path.GetFullPath(outputPath) + "; bytes=" + savedBytes.Length + "; SHA256 " + Hash(savedBytes) + "; fresh official schema valid; " + allRows.Length + " unique REB addresses incl comments");
        File.WriteAllLines(outputPath + ".proof.txt", proof, new UTF8Encoding(false));
        foreach (var line in proof) Console.WriteLine(line);
    }
    static bool IsHierarchy(XElement x) => (x.Name.Namespace == SourceNs || x.Name.Namespace == Ns) && x.Name.LocalName is "BoQ" or "BoQBody" or "BoQCtgy" or "Itemlist" or "Item";
    static string Describe(XElement x) => x.Name.LocalName + ":" + string.Join("|", x.Attributes().Where(a => a.Name.LocalName is "ID" or "RNoPart" or "RNoIndex").Select(a => a.Name + "=" + a.Value)) + ":parent=" + (x.Parent?.Name.LocalName == "Award" ? "QtyDeterm" : x.Parent?.Name.LocalName) + ":depth=" + x.Ancestors().Count();
    static string Canonical(XElement x) => x.Name.LocalName + (x.HasElements ? "[" + string.Join(";", x.Elements().Select(Canonical)) + "]" : "=" + x.Value);
    static XDocument Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static void CheckAddress(string address)
    {
        Require(address.Length == 6 && int.TryParse(address[..4], NumberStyles.AllowLeadingWhite, CultureInfo.InvariantCulture, out var sheet) && sheet is >= 1 and <= 9999 &&
            address[4] is >= 'A' and <= 'Z' && address[5] is >= '0' and <= '9', "Invalid address: " + address);
    }
    static void Validate(XDocument document, XmlSchemaSet schemas)
    {
        var errors = ValidationErrors(document, schemas);
        Require(errors.Count == 0, string.Join("; ", errors));
    }
    static List<string> ValidationErrors(XDocument document, XmlSchemaSet schemas)
    {
        var errors = new List<string>(); document.Validate(schemas, (_, e) => errors.Add(e.Message)); return errors;
    }
    static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
