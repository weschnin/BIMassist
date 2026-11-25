using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BIMassist.Ava.Core;

/// <summary>Creates a reduced DA82 quantity LV from the immutable source snapshot.</summary>
public static class X82QuantityWriter
{
    private static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";

    public static string Serialize(AvaTemplate template, IReadOnlyList<QuantityTakeoffRow> rows)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) throw new InvalidDataException("Keine Mengenzeilen für den X82-Export vorhanden.");
        var source = template.LvCatalog.Source ?? throw new InvalidDataException("Die LV-Quelle fehlt.");
        var bytes = LoadSnapshot(source);
        using var input = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(input, SafeSettings());
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        VerifyCatalogSnapshot(source, template.LvCatalog.Entries, bytes);

        var catalog = template.LvCatalog.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!catalog.TryGetValue(row.LvItemId, out var entry))
                throw new InvalidDataException("X82-Menge verweist auf eine unbekannte LV-Position: " + row.LvItemId);
            if (!string.Equals(row.Unit, entry.Unit, StringComparison.Ordinal))
                throw new InvalidDataException("X82-Export abgebrochen: Einheit der Position „" + entry.Ordinal + "“ wurde geändert (LV: " + entry.Unit + ", Menge: " + row.Unit + ").");
            totals[row.LvItemId] = totals.GetValueOrDefault(row.LvItemId) + row.Value;
        }

        var items = document.Descendants(Ns + "Item").ToDictionary(item => RequiredAttribute(item, "ID"), StringComparer.Ordinal);
        var rowsByItem = rows.GroupBy(row => row.LvItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var address = 0;
        foreach (var item in items.Values.ToArray())
        {
            if (!totals.TryGetValue(item.Attribute("ID")!.Value, out var total))
            {
                item.Remove();
                continue;
            }
            var rounded = decimal.Round(total, 3, MidpointRounding.AwayFromZero);
            var formatted = rounded.ToString("0.###", CultureInfo.InvariantCulture);
            if (formatted.Replace("-", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).Length > 11)
                throw new InvalidDataException("Die summierte Menge überschreitet das GAEB-Feld Qty (11 Stellen, maximal 3 Nachkommastellen) bei OZ " + catalog[item.Attribute("ID")!.Value].Ordinal + ".");
            var determination = item.Element(Ns + "QtyDeterm");
            if (determination == null)
            {
                determination = new XElement(Ns + "QtyDeterm");
                var successor = item.Elements().FirstOrDefault(element => element.Name.LocalName is "BidUpDownReq" or "BidUpDownPct");
                if (successor == null) item.Add(determination);
                else successor.AddBeforeSelf(determination);
            }
            var determinationQuantity = determination.Element(Ns + "Qty");
            if (determinationQuantity == null) determination.AddFirst(determinationQuantity = new XElement(Ns + "Qty"));
            determinationQuantity.Value = formatted;
            determination.Elements(Ns + "QDetermItem").Remove();
            // A bare QtyDeterm/Qty is an optional total; interoperable quantity takeoff also
            // needs numeric REB rows and the method in the containing LV's BoQInfo.
            var boqInfo = item.Ancestors(Ns + "BoQ").Single().Element(Ns + "BoQInfo")
                ?? throw new InvalidDataException("BoQInfo fehlt im X82-Quell-LV.");
            var info = boqInfo.Element(Ns + "QtyDetermInfo");
            if (info == null) boqInfo.Add(info = new XElement(Ns + "QtyDetermInfo"));
            var method = info.Element(Ns + "MethodDescription");
            if (method == null) info.AddFirst(method = new XElement(Ns + "MethodDescription"));
            method.Value = "REB23003-2009";

            var measuredRows = rowsByItem[item.Attribute("ID")!.Value];
            // Round cumulative measured quantities, not each row independently: the
            // difference is sign-consistent with its input and the final sum is exact.
            var cumulative = 0m;
            var allocated = 0m;
            for (var i = 0; i < measuredRows.Length; i++)
            {
                cumulative += measuredRows[i].Value;
                var next = decimal.Round(cumulative, 3, MidpointRounding.AwayFromZero);
                var value = next - allocated;
                allocated = next;
                var literal = value.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',') + "=";
                if (address >= 9999 * 260 || literal.Length > 38)
                    throw new InvalidDataException("X82-Mengenermittlung überschreitet den REB-Zeilenadressraum oder das Zahlenfeld.");
                var row = new string(' ', 29) + "91" + literal.PadRight(38)
                    + (address / 260 + 1).ToString(CultureInfo.InvariantCulture).PadLeft(4)
                    + (char)('A' + address % 260 / 10) + (address % 10).ToString(CultureInfo.InvariantCulture)
                    + new string(' ', 5);
                determination.Add(new XElement(Ns + "QDetermItem", new XElement(Ns + "QTakeoff", new XAttribute("Row", row))));
                address++;
            }
        }
        if (totals.Keys.Any(id => !items.ContainsKey(id)))
            throw new InvalidDataException("Mindestens eine X82-Position fehlt im eingebetteten Quell-LV.");
        PruneUnrelatedMarkup(document.Root!, totals.Keys.ToHashSet(StringComparer.Ordinal));
        foreach (var masterItems in document.Descendants(Ns + "MastAgrItems").Reverse().ToArray())
            if (!masterItems.Descendants(Ns + "Item").Any()) masterItems.Remove();
        PruneEmptyContainers(document.Root!);
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false, true), Indent = true, NewLineHandling = NewLineHandling.Entitize }))
            document.Save(writer);
        var result = new UTF8Encoding(false, true).GetString(output.ToArray());
        ValidateSerializedResult(result, totals, catalog);
        return result;
    }

    public static void Save(string path, AvaTemplate template, IReadOnlyList<QuantityTakeoffRow> rows)
    {
        var content = Serialize(template, rows);
        var bytes = new UTF8Encoding(false, true).GetBytes(content);
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (!bytes.SequenceEqual(File.ReadAllBytes(temporary))) throw new IOException("Die gespeicherte X82-Datei stimmt nicht mit der geprüften Ausgabe überein.");
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] LoadSnapshot(SourceStamp source)
    {
        if (string.IsNullOrWhiteSpace(source.X82SnapshotBase64) || source.Sha256.Length != 64 || !source.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Der verifizierbare X82-Quellsnapshot fehlt. Importieren Sie das LV erneut.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(source.X82SnapshotBase64); }
        catch (FormatException ex) { throw new InvalidDataException("Der eingebettete X82-Snapshot ist ungültig.", ex); }
        if (bytes.Length > CatalogReaders.MaxBytes || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Der eingebettete X82-Snapshot stimmt nicht mit seinem SHA-256 überein.");
        return bytes;
    }

    private static void VerifyCatalogSnapshot(SourceStamp source, IReadOnlyList<LvEntry> entries, byte[] bytes)
    {
        var read = CatalogReaders.ReadX82(new MemoryStream(bytes, false), source.FileName);
        if (read.Entries.Count != entries.Count) throw new InvalidDataException("Das AVA-LV weicht vom importierten X82-Snapshot ab.");
        var imported = read.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!imported.TryGetValue(entry.Id, out var actual) || actual.BoqId != entry.BoqId || actual.Ordinal != entry.Ordinal
                || actual.Text != entry.Text || actual.Description != entry.Description || actual.Unit != entry.Unit)
                throw new InvalidDataException("Die AVA-LV-Position „" + entry.Ordinal + "“ weicht vom unveränderten X82-Quellsnapshot ab.");
        }
    }


    private static void PruneEmptyContainers(XElement root)
    {
        foreach (var itemlist in root.Descendants(Ns + "Itemlist").Reverse().ToArray())
            if (!itemlist.Descendants(Ns + "Item").Any()) itemlist.Remove();
        foreach (var body in root.Descendants(Ns + "BoQBody").Reverse().ToArray())
            if (!body.Descendants(Ns + "Item").Any())
                body.Elements().Where(element => element.Name == Ns + "Remark" || element.Name == Ns + "PerfDescr").Remove();
        foreach (var category in root.Descendants(Ns + "BoQCtgy").Reverse().ToArray())
            if (!category.Descendants(Ns + "Item").Any()) category.Remove();
    }

    private static void PruneUnrelatedMarkup(XElement root, IReadOnlySet<string> includedItemIds)
    {
        // A Zuschlagsposition is retained only while its GAEB IDREF still targets an included service or in-scope description.
        var relevantPerformanceDescriptions = root.Descendants(Ns + "PerfDescr")
            .Where(description =>
            {
                var itemlist = description.Ancestors(Ns + "Itemlist").FirstOrDefault();
                if (itemlist != null) return itemlist.Descendants(Ns + "Item").Any();
                var body = description.Ancestors(Ns + "BoQBody").FirstOrDefault();
                return body?.Descendants(Ns + "Item").Any() == true;
            })
            .Select(description => (string?)description.Attribute("ID"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var markup in root.Descendants(Ns + "MarkupItem").ToArray())
        {
            foreach (var reference in markup.Descendants(Ns + "RefRNo").Concat(markup.Descendants(Ns + "RefItem")).ToArray())
            {
                var id = (string?)reference.Attribute("IDRef");
                if (id != null && includedItemIds.Contains(id)) continue;
                var subQuantity = reference.Ancestors(Ns + "MarkupSubQty").FirstOrDefault();
                if (subQuantity != null) subQuantity.Remove();
                else reference.Remove();
            }
            foreach (var reference in markup.Descendants(Ns + "RefPerfNo").ToArray())
                if (!relevantPerformanceDescriptions.Contains((string?)reference.Attribute("IDRef") ?? "")) reference.Remove();
            if (!markup.Descendants(Ns + "RefRNo").Any()
                && !markup.Descendants(Ns + "RefItem").Any()
                && !markup.Descendants(Ns + "RefPerfNo").Any())
                markup.Remove();
        }
    }

    private static void ValidateSerializedResult(string content, IReadOnlyDictionary<string, decimal> totals, IReadOnlyDictionary<string, LvEntry> catalog)
    {
        using var stream = new MemoryStream(new UTF8Encoding(false, true).GetBytes(content));
        using var reader = XmlReader.Create(stream, SafeSettings());
        var document = XDocument.Load(reader);
        if (document.Root?.Name != Ns + "GAEB") throw new InvalidDataException("Der X82-Export ist kein GAEB DA82/3.3-Dokument.");
        var output = document.Descendants(Ns + "Item").ToArray();
        if (output.Length != totals.Count) throw new InvalidDataException("Die reduzierte X82-Datei enthält eine unerwartete Anzahl LV-Positionen.");
        foreach (var item in output)
        {
            var id = RequiredAttribute(item, "ID");
            if (!totals.TryGetValue(id, out var expected) || !catalog.ContainsKey(id)
                || !decimal.TryParse(item.Element(Ns + "QtyDeterm")?.Element(Ns + "Qty")?.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var actual)
                || actual != decimal.Round(expected, 3, MidpointRounding.AwayFromZero))
                throw new InvalidDataException("Der X82-Export enthält eine falsche Position oder Menge.");
            var lines = item.Element(Ns + "QtyDeterm")!.Elements(Ns + "QDetermItem")
                .Select(line => (string?)line.Element(Ns + "QTakeoff")?.Attribute("Row")).ToArray();
            if (lines.Length == 0 || lines.Any(line => line is not { Length: 80 } || line.Substring(29, 2) != "91")
                || item.Ancestors(Ns + "BoQ").Single().Element(Ns + "BoQInfo")?
                    .Element(Ns + "QtyDetermInfo")?.Element(Ns + "MethodDescription")?.Value != "REB23003-2009")
                throw new InvalidDataException("Der X82-Export enthält keine lesbaren REB-Mengenzeilen.");
            var sum = 0m;
            foreach (var line in lines)
            {
                if (!decimal.TryParse(line!.Substring(31, 38).Trim().TrimEnd('=').Replace(',', '.'),
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException("Die X82-Mengenzeile enthält keine gültige REB-Zahl.");
                sum += value;
            }
            if (sum != actual) throw new InvalidDataException("REB-Zeilen und X82-Aufmaßmenge weichen voneinander ab.");
        }
    }

    private static XmlReaderSettings SafeSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = CatalogReaders.MaxBytes,
        MaxCharactersFromEntities = 0
    };

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value ? value : throw new InvalidDataException("Fehlendes " + name + " in X82-Position.");
}
