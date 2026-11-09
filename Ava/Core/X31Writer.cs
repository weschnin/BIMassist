using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BIMassist.Ava.Core;

public sealed record QuantityTakeoffRow(string LvItemId, decimal Value, string Unit, string Explanation);

/// <summary>Portable DA31/3.3 (2023-01) writer. No Revit API, source-file access, or formula evaluation.</summary>
public static class X31Writer
{
    private static readonly XNamespace SourceNs = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";
    private static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";

    public static string Serialize(AvaTemplate template, IReadOnlyList<QuantityTakeoffRow> rows)
    {
        if (template?.LvCatalog?.Entries == null || template.LvCatalog.Source == null || rows == null || rows.Count == 0)
            throw new InvalidDataException("LV und mindestens eine Mengenzeile sind erforderlich.");
        // At least one explanation comment AND one numeric line per quantity.
        if (rows.Count > 9999 * 260 / 2) throw new InvalidDataException("REB-Zeilenadressraum überschritten.");
        rows = rows.ToArray();
        var source = LoadSnapshot(template.LvCatalog.Source, out var imported);
        var entries = template.LvCatalog.Entries.ToArray();
        var knownItems = new HashSet<string>(StringComparer.Ordinal);
        var originals = imported.Entries.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (entries.Length != originals.Count) throw new InvalidDataException("LV-Katalog und X82-Snapshot haben unterschiedliche Positionen; bitte das LV erneut importieren.");
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !knownItems.Add(entry.Id) ||
                !originals.TryGetValue(entry.Id, out var original) || entry.NumberParts == null ||
                !entry.NumberParts.SequenceEqual(original.NumberParts, StringComparer.Ordinal) ||
                entry.BoqId != original.BoqId || entry.Ordinal != original.Ordinal || entry.Unit != original.Unit ||
                entry.Text != original.Text || entry.Description != original.Description)
                throw new InvalidDataException("LV-Katalog stimmt nicht mit dem unveränderten X82-Snapshot überein.");
        }
        long requiredLines = 0;
        foreach (var row in rows)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.LvItemId) || !knownItems.Contains(row.LvItemId))
                throw new InvalidDataException("Unbekannte oder leere LV-Position.");
            CheckText(row.Unit, 40, "Ergebniseinheit", false);
            CheckText(row.Explanation, 4096, "Erläuterung", true);
            var explanationLength = "Ergebniseinheit: ".Length + row.Unit.Length + "; ".Length + row.Explanation.Length;
            requiredLines += 1 + (explanationLength + 49) / 50;
            if (requiredLines > 9999 * 260) throw new InvalidDataException("REB-Zeilenadressraum einschließlich Erläuterungen überschritten.");
        }
        var boq = ValidateHierarchy(source);
        var outputBoq = new XElement(Ns + "BoQ", new XAttribute("ID", boq.Attribute("ID")!.Value),
            boq.Element(SourceNs + "BoQInfo")!.Elements(SourceNs + "BoQBkdn").Select(Rename),
            Skeleton(boq.Element(SourceNs + "BoQBody")!));
        var items = outputBoq.Descendants(Ns + "Item").ToDictionary(x => x.Attribute("ID")!.Value, StringComparer.Ordinal);
        int index = 0;
        foreach (var row in rows)
        {
            var item = items[row.LvItemId];
            var determination = item.Element(Ns + "QtyDeterm");
            if (determination == null) item.Add(determination = new XElement(Ns + "QtyDeterm"));
            // REB 23.003 (2009), 2.2.9: '*' column 13, text columns 14..69.
            // Prefix EVERY chunk so supplied text cannot become a #Bild directive.
            var explanation = "Ergebniseinheit: " + row.Unit + "; " + row.Explanation;
            for (int start = 0; start < explanation.Length; start += 50)
            {
                var chunk = "Info: " + explanation.Substring(start, Math.Min(50, explanation.Length - start));
                AddLine(determination, new string(' ', 12) + "*" + chunk.PadRight(56), index++);
            }
            var formula = row.Value.ToString("0.############################", CultureInfo.InvariantCulture).Replace('.', ',') + "=";
            AddLine(determination, new string(' ', 29) + "91" + formula.PadRight(38), index++);
        }
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(Ns + "GAEB",
            new XElement(Ns + "GAEBInfo", new XElement(Ns + "Version", "3.3"), new XElement(Ns + "VersDate", "2023-01"),
                new XElement(Ns + "Date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            new XElement(Ns + "QtyDeterm", new XElement(Ns + "QtyDetermInfo", new XElement(Ns + "MethodDescription", "REB23003-2009")),
                new XElement(Ns + "DP", "31"), outputBoq)));
        ValidateXml(document);
        return document.Declaration + "\n" + document;
    }

    /// <summary>Write, flush, re-read and validate a same-directory temporary before atomic publication.
    /// Existing target bytes are never touched if serialization, verification or replacement fails.</summary>
    public static void Save(string path, AvaTemplate template, IReadOnlyList<QuantityTakeoffRow> rows)
    {
        var xml = Serialize(template, rows);
        var bytes = new UTF8Encoding(false, true).GetBytes(xml);
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            var saved = File.ReadAllBytes(temporary);
            if (!bytes.SequenceEqual(saved)) throw new IOException("Gespeicherte X31-Datei stimmt nicht mit den geprüften Daten überein.");
            using (var stream = new MemoryStream(saved, false))
            using (var reader = XmlReader.Create(stream, SafeSettings()))
                ValidateXml(XDocument.Load(reader));
            // No delete/move fallback: failure must preserve an existing target.
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static XElement ValidateHierarchy(XDocument source)
    {
        var boqs = source.Descendants(SourceNs + "BoQ").ToArray();
        if (boqs.Length != 1) throw new InvalidDataException("DA31 unterstützt genau ein Quell-LV pro Export; mehrere BoQs nicht zusammenfassen.");
        var boq = boqs[0];
        var info = RequiredElement(boq, "BoQInfo");
        _ = RequiredElement(boq, "BoQBody");
        var breakdown = info.Elements(SourceNs + "BoQBkdn").ToArray();
        if (breakdown.Length is < 1 or > 7) throw new InvalidDataException("Originale BoQBkdn fehlt oder ist ungültig; bitte das LV erneut importieren.");
        var fields = new List<(string Type, int Length, bool Numeric)>();
        foreach (var field in breakdown)
        {
            if (field.Descendants().Any(x => x.Name.Namespace != SourceNs)) throw new InvalidDataException("Fremder Namensraum in BoQBkdn.");
            var type = RequiredElement(field, "Type").Value;
            var num = RequiredElement(field, "Num").Value;
            if (!int.TryParse(RequiredElement(field, "Length").Value, NumberStyles.None, CultureInfo.InvariantCulture, out int length) || length is < 1 or > 14 || num is not ("Yes" or "No"))
                throw new InvalidDataException("Ungültige originale BoQBkdn-Länge oder Num.");
            fields.Add((type, length, num == "Yes"));
        }
        int itemIndex = fields.FindIndex(x => x.Type == "Item");
        if (itemIndex < 0 || fields.Count(x => x.Type == "Item") != 1 ||
            fields.Take(itemIndex).Where((x, i) => x.Type != "BoQLevel" && !(i == 0 && x.Type == "Lot")).Any() ||
            fields.Skip(itemIndex + 1).Any(x => x.Type != "Index" || x.Length != 1) || fields.Count > itemIndex + 2)
            throw new InvalidDataException("Ungültige Reihenfolge der originalen BoQBkdn.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in source.Descendants().Where(x => x.Name.Namespace == SourceNs))
        {
            var name = node.Name.LocalName;
            if (name is "BoQ" or "BoQCtgy" or "Item")
            {
                var id = RequiredAttribute(node, "ID");
                try { XmlConvert.VerifyNCName(id); } catch (XmlException ex) { throw new InvalidDataException("Ungültige GAEB-ID.", ex); }
                if (!ids.Add(id)) throw new InvalidDataException("Doppelte GAEB-ID: " + id);
            }
            var parent = node.Parent?.Name;
            if ((name == "BoQ" && parent != SourceNs + "Award") ||
                (name == "BoQBody" && parent != SourceNs + "BoQ" && parent != SourceNs + "BoQCtgy") ||
                (name is "BoQCtgy" or "Itemlist" && parent != SourceNs + "BoQBody") ||
                (name == "Item" && parent != SourceNs + "Itemlist"))
                throw new InvalidDataException("Ungültige X82-Hierarchie: " + name);
            if (name == "BoQBody")
            {
                var categories = node.Elements(SourceNs + "BoQCtgy").ToArray();
                var lists = node.Elements(SourceNs + "Itemlist").ToArray();
                if (lists.Length > 1 || (lists.Length > 0 && categories.Length > 0)) throw new InvalidDataException("Mehrdeutiger LV-Hauptteil.");
                var depth = node.Ancestors(SourceNs + "BoQCtgy").Count();
                if (categories.Length > 0 && depth >= itemIndex) throw new InvalidDataException("LV-Bereich tiefer als originale BoQBkdn.");
                if (categories.Length > 0) UniqueParts(categories, fields[depth]);
            }
            if (name == "Itemlist") UniqueParts(node.Elements(SourceNs + "Item"), fields[itemIndex], fields.Count > itemIndex + 1 ? fields[itemIndex + 1] : null);
            if (name == "BoQCtgy" && node.Elements(SourceNs + "BoQBody").Count() > 1) throw new InvalidDataException("Mehrere Hauptteile eines LV-Bereichs.");
        }
        return boq;
    }
    private static void UniqueParts(IEnumerable<XElement> nodes, (string Type, int Length, bool Numeric) field, (string Type, int Length, bool Numeric)? index = null)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var part = RequiredAttribute(node, "RNoPart");
            var canonical = CheckPart(part, field);
            var suffix = (string?)node.Attribute("RNoIndex");
            if (suffix != null)
            {
                if (index == null) throw new InvalidDataException("RNoIndex ohne originale Index-Gliederung.");
                canonical += "." + CheckPart(suffix, index.Value);
            }
            if (!keys.Add(canonical)) throw new InvalidDataException("Doppelte semantische Teil-OZ: " + part);
        }
    }
    private static string CheckPart(string part, (string Type, int Length, bool Numeric) field)
    {
        if (part.Length > field.Length || part.Any(c => field.Numeric ? c is < '0' or > '9' : !(c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')))
            throw new InvalidDataException("Teil-OZ passt nicht zur originalen BoQBkdn: " + part);
        return field.Numeric ? part.TrimStart('0') : part;
    }
    private static XElement RequiredElement(XElement parent, string name)
    {
        var matches = parent.Elements(SourceNs + name).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("Fehlendes oder doppeltes " + name + " im X82-Snapshot.");
    }
    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value && value == value.Trim() ? value : throw new InvalidDataException("Fehlendes oder ungültiges " + name + ".");

    private static XDocument LoadSnapshot(SourceStamp stamp, out Catalog<LvEntry> imported)
    {
        if (string.IsNullOrEmpty(stamp.X82SnapshotBase64))
            throw new InvalidDataException("Der eingebettete X82-Snapshot fehlt. Bitte das LV erneut importieren; der ursprüngliche Dateiname wird nicht geöffnet.");
        if (stamp.X82SnapshotBase64.Length > ((CatalogReaders.MaxBytes + 2) / 3) * 4 ||
            stamp.Sha256 == null || stamp.Sha256.Length != 64 || !stamp.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Ungültiger X82-Snapshot oder SHA-256.");
        try
        {
            var bytes = Convert.FromBase64String(stamp.X82SnapshotBase64);
            if (bytes.Length > CatalogReaders.MaxBytes || !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), stamp.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Der X82-Snapshot stimmt nicht mit seinem SHA-256 überein.");
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, SafeSettings());
            var document = XDocument.Load(reader);
            if (document.Root?.Name != SourceNs + "GAEB") throw new InvalidDataException("X82-Snapshot muss GAEB DA82/3.3 sein.");
            imported = CatalogReaders.ReadX82(new MemoryStream(bytes, false), stamp.FileName);
            return document;
        }
        catch (FormatException ex) { throw new InvalidDataException("Ungültiges X82-Base64.", ex); }
        catch (XmlException ex) { throw new InvalidDataException("Ungültiges X82-XML; DTDs sind nicht erlaubt.", ex); }
    }
    private static XmlReaderSettings SafeSettings() => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = CatalogReaders.MaxBytes };

    private static void CheckText(string? text, int maximum, string label, bool allowBlank)
    {
        if (text == null || text.Length > maximum || (!allowBlank && string.IsNullOrWhiteSpace(text)) ||
            text.Any(c => char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new InvalidDataException(label + " ist nicht als einzeiliger REB-Text darstellbar (max. " + maximum + " BMP-Zeichen).");
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException ex) { throw new InvalidDataException(label + " enthält ungültige XML-Zeichen.", ex); }
    }

    private static string FormatAddress(int index)
    {
        // REB 23.003 (2009), 2.2.5: sheet columns 70..73; A..Z at 74; 0..9 at 75.
        if (index < 0 || index >= 9999 * 260) throw new InvalidDataException("REB-Zeilenadresse außerhalb 1A0 bis 9999Z9.");
        return (index / 260 + 1).ToString(CultureInfo.InvariantCulture).PadLeft(4) + (char)('A' + index % 260 / 10) + (index % 10).ToString(CultureInfo.InvariantCulture);
    }
    private static void AddLine(XElement determination, string content, int index)
    {
        var line = content + FormatAddress(index) + new string(' ', 5);
        determination.Add(new XElement(Ns + "QDetermItem", new XElement(Ns + "QTakeoff", new XAttribute("Row", line))));
    }

    private static readonly Lazy<System.Xml.Schema.XmlSchemaSet> Schemas = new(() =>
    {
        var schemas = new System.Xml.Schema.XmlSchemaSet { XmlResolver = new OfficialSchemaResolver() };
        using var main = Inflate(MainSchema);
        using var reader = XmlReader.Create(main, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }, "https://embedded.invalid/GAEB_DA_XML_31_3.3_2023-01.xsd");
        schemas.Add(Ns.NamespaceName, reader);
        schemas.Compile();
        return schemas;
    });
    private static Stream Inflate(string encoded)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
        using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        var expanded = new MemoryStream(); gzip.CopyTo(expanded); expanded.Position = 0; return expanded;
    }
    private sealed class OfficialSchemaResolver : XmlResolver
    {
        public override System.Net.ICredentials? Credentials { set { } }
        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) =>
            absoluteUri.AbsoluteUri == "https://embedded.invalid/GAEB_DA_XML_Lib_3.3_2021-05.xsd"
                ? Inflate(LibrarySchema) : throw new XmlException("Unbekannte externe Schema-Referenz.");
    }
    private static void ValidateXml(XDocument document)
    {
        var errors = new List<string>();
        System.Xml.Schema.Extensions.Validate(document, Schemas.Value, (_, e) => errors.Add(e.Message));
        if (errors.Count != 0) throw new InvalidDataException("Ungültiges GAEB DA31/3.3 XML: " + string.Join("; ", errors));
    }


    // Exact unmodified official GAEB schemas, gzip/base64 encoded to keep the pure writer
    // self-contained without project-file resources or a network/filesystem schema resolver.
    // Source: https://www.gaeb.de/wp-content/uploads/2023/09/2023-01_Mengenermittlung.zip
    // GAEB_DA_XML_31_3.3_2023-01.xsd SHA-256 179b9d8c9a93145af63298270e44c2d1ae820bb850edd0ba284d5b7cbef01b56
    private const string MainSchema =
        "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" +
        "4/83fsSfzD0AAA==";
    // GAEB_DA_XML_Lib_3.3_2021-05.xsd SHA-256 a00cad3a5bfaf079de936393c0827c2387d4b9407c113359cca4b493903388ec
    private const string LibrarySchema =
        "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";

    private static XElement Rename(XElement element) => new(Ns + element.Name.LocalName,
        element.Attributes().Where(a => !a.IsNamespaceDeclaration),
        element.HasElements ? element.Elements().Select(Rename) : element.Value);

    private static XElement Skeleton(XElement element) => new(Ns + element.Name.LocalName,
        element.Attributes().Where(a => a.Name == "ID" || a.Name == "RNoPart" || a.Name == "RNoIndex"),
        element.Elements().Where(e => e.Name == SourceNs + "BoQBody" || e.Name == SourceNs + "BoQCtgy" || e.Name == SourceNs + "Itemlist" || e.Name == SourceNs + "Item").Select(Skeleton));
}
