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
        }
        if (totals.Keys.Any(id => !items.ContainsKey(id)))
            throw new InvalidDataException("Mindestens eine X82-Position fehlt im eingebetteten Quell-LV.");
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
            if (!itemlist.Elements().Any()) itemlist.Remove();
        foreach (var body in root.Descendants(Ns + "BoQBody").Reverse().ToArray())
            foreach (var category in body.Elements(Ns + "BoQCtgy").Reverse().ToArray())
            {
                var nestedBody = category.Element(Ns + "BoQBody");
                if (nestedBody != null && !nestedBody.Elements().Any()) category.Remove();
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
