using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BIMassist.Ava.Core;

public sealed record SourceStamp(string FileName, string Format, string Sha256);
public sealed record Catalog<T>(SourceStamp Source, List<T> Entries);
public sealed record BgkType(string Number, string Description)
{
    public override string ToString() => Number + " — " + Description;
}
public sealed record BgkEntry(string Key, string Description, string Category, List<BgkType> Types)
{
    public override string ToString() => Key + " — " + Description;
}
public sealed record LvEntry(string Id, string BoqId, List<string> NumberParts, string Ordinal, string Text, string Description, string Unit)
{
    public override string ToString() => Ordinal + " | " + Unit + " | " + Text;
}
public sealed record SharedParameter(Guid Guid, string Name, string Spec, string Group)
{
    public override string ToString() => Name + " | " + Spec + " | " + Guid;
}

/// <summary>Read-only readers for the supplied DA82/3.3, Baugruppe XML and Revit definition TXT formats.
/// They do not bind parameters, infer model identity, or validate the full GAEB XSD.</summary>
public static class CatalogReaders
{
    public const int MaxBytes = 32 * 1024 * 1024;
    private const string GaebNamespace = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";

    public static Catalog<LvEntry> ReadX82(Stream input, string fileName)
    {
        var bytes = ReadBytes(input);
        var root = Xml(bytes);
        XNamespace ns = GaebNamespace;
        if (root.Name != ns + "GAEB") throw new InvalidDataException("Nur GAEB DA82 / 3.3 wird gelesen.");
        var entries = new List<LvEntry>();
        foreach (var item in root.Descendants(ns + "Item"))
        {
            var boq = item.Ancestors(ns + "BoQ").FirstOrDefault() ?? throw new InvalidDataException("LV-Position ohne BoQ.");
            var parts = item.Ancestors(ns + "BoQCtgy").Reverse().Select(x => RequiredAttribute(x, "RNoPart")).ToList();
            parts.Add(RequiredAttribute(item, "RNoPart"));
            var index = (string?)item.Attribute("RNoIndex");
            if (!string.IsNullOrEmpty(index)) parts.Add(index);
            string outline = Text(item.Descendants(ns + "OutlineText").FirstOrDefault());
            string detail = Text(item.Descendants(ns + "DetailTxt").FirstOrDefault());
            entries.Add(new(RequiredAttribute(item, "ID"), RequiredAttribute(boq, "ID"), parts,
                string.Join(".", parts), string.IsNullOrWhiteSpace(outline) ? detail : outline, detail, item.Element(ns + "QU")?.Value ?? ""));
        }
        if (entries.Count == 0) throw new InvalidDataException("Keine LV-Positionen gefunden.");
        Unique(entries.Select(e => e.Id), "LV-ID");
        Unique(entries.Select(e => e.BoqId + "\0" + e.Ordinal), "LV-OZ");
        return new(Stamp(bytes, fileName, "GAEB DA82/3.3"), entries);
    }

    public static Catalog<BgkEntry> ReadBgk(Stream input, string fileName)
    {
        var bytes = ReadBytes(input);
        var root = Xml(bytes);
        if (root.Name != "Baugruppe") throw new InvalidDataException("Baugruppe-XML erwartet (ohne Namensraum).");
        var entries = root.DescendantsAndSelf("Baugruppe").Select(x => new BgkEntry(
            RequiredValue(x, "Key"), x.Element("Description")?.Value ?? "", x.Element("Category")?.Value ?? "",
            x.Element("Typmarkierungen")?.Elements("Typmarkierung").Select(t => new BgkType(RequiredValue(t, "Typnummer"), t.Element("Typbezeichnung")?.Value ?? "")).ToList() ?? [])).ToList();
        Unique(entries.Select(e => e.Key), "BGK-Schlüssel");
        foreach (var entry in entries) Unique(entry.Types.Select(t => t.Number), "Typnummer in " + entry.Key);
        return new(Stamp(bytes, fileName, "Baugruppe XML"), entries);
    }

    public static Catalog<SharedParameter> ReadSharedParameters(Stream input, string fileName)
    {
        var bytes = ReadBytes(input);
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
        var entries = new List<SharedParameter>();
        Dictionary<string, int>? header = null;
        try
        {
            while (reader.ReadLine() is { } line)
            {
                var cells = line.Split('\t');
                if (cells[0] == "*PARAM")
                {
                    header = cells.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i, StringComparer.Ordinal);
                    continue;
                }
                if (cells[0] != "PARAM") continue;
                string Field(string name) => header != null && header.TryGetValue(name, out var i) && i < cells.Length ? cells[i] : throw new InvalidDataException("Fehlende PARAM-Spalte: " + name);
                if (!Guid.TryParseExact(Field("GUID"), "D", out var guid) || guid == Guid.Empty) throw new InvalidDataException("Ungültige Parameter-GUID.");
                var name = Field("NAME"); var spec = Field("DATATYPE");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(spec)) throw new InvalidDataException("Parametername / Datentyp fehlt.");
                entries.Add(new(guid, name, spec, Field("GROUP")));
            }
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("TXT-Zeichenkodierung ist ungültig.", ex); }
        catch (ArgumentException ex) { throw new InvalidDataException("Doppelte PARAM-Spalten.", ex); }
        if (entries.Count == 0) throw new InvalidDataException("Keine gemeinsamen Parameterdefinitionen gefunden.");
        Unique(entries.Select(e => e.Guid.ToString()), "Parameter-GUID");
        return new(Stamp(bytes, fileName, "Revit shared parameters"), entries);
    }

    internal static byte[] ReadBytes(Stream input)
    {
        using var buffer = new MemoryStream();
        var block = new byte[8192]; int count;
        while ((count = input.Read(block, 0, block.Length)) != 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("Datei überschreitet 32 MiB.");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }
    private static XElement Xml(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            return XDocument.Load(reader).Root ?? throw new InvalidDataException("Leeres XML.");
        }
        catch (XmlException ex) { throw new InvalidDataException("Ungültiges XML: " + ex.Message, ex); }
    }
    private static SourceStamp Stamp(byte[] bytes, string fileName, string format) => new(Path.GetFileName(fileName), format, Convert.ToHexString(SHA256.HashData(bytes)));
    private static string RequiredValue(XElement element, string name) => NotEmpty(element.Element(name)?.Value, name);
    private static string RequiredAttribute(XElement element, string name) => NotEmpty((string?)element.Attribute(name), name);
    private static string NotEmpty(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Fehlender Schlüssel: " + name);
    private static string Text(XElement? element) => element == null ? "" : string.Join("\n", element.Descendants().Where(x => x.Name.LocalName == "p").Select(x => x.Value)).Trim() is { Length: > 0 } paragraphs ? paragraphs : element.Value.Trim();
    private static void Unique(IEnumerable<string> keys, string label)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys) if (!set.Add(key)) throw new InvalidDataException("Doppelter " + label + ": " + key);
    }
}
