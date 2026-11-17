using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BIMassist.Ava.Core;

/// <summary>Serializes the AVA BGK snapshot as the recursive, un-namespaced Baugruppe XML read by BGK Manager.</summary>
public static class BgkManagerCatalogWriter
{
    private const string HiddenRootKey = "///";
    private const int MaxHierarchyDepth = 256;

    public static string Serialize(Catalog<BgkEntry> catalog)
    {
        if (catalog?.Entries == null || catalog.Entries.Count == 0)
            throw new InvalidDataException("Der AVA-BGK-Katalog ist leer.");

        var byKey = new Dictionary<string, BgkEntry>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Key))
                throw new InvalidDataException("Der BGK-Katalog enthält einen Eintrag ohne Schlüssel.");
            if (!byKey.TryAdd(entry.Key, entry))
                throw new InvalidDataException("Doppelter BGK-Schlüssel: " + entry.Key);
            if (entry.Types == null)
                throw new InvalidDataException("Typmarkierungen fehlen für BGK " + entry.Key + ".");

            var typeNumbers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in entry.Types)
            {
                if (type == null || string.IsNullOrWhiteSpace(type.Number)
                    || !int.TryParse(type.Number, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw new InvalidDataException($"BGK-Manager erwartet ganzzahlige Typnummern. Ungültige Typnummer bei BGK {entry.Key}: {type?.Number ?? "(leer)"}.");
                if (!typeNumbers.Add(type.Number))
                    throw new InvalidDataException($"Doppelte Typnummer {type.Number} bei BGK {entry.Key}.");
            }
        }

        var regularEntries = byKey.Values.Where(entry => entry.Key != HiddenRootKey).ToArray();
        var regularByKey = regularEntries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var childrenByParent = regularEntries
            .Select(entry => (Entry: entry, Parent: ParentKey(entry.Key)))
            .Where(pair => pair.Parent.Length > 0 && regularByKey.ContainsKey(pair.Parent))
            .GroupBy(pair => pair.Parent, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Entry).OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var roots = regularEntries
            .Where(entry => ParentKey(entry.Key) is not { Length: > 0 } parent || parent == HiddenRootKey || !regularByKey.ContainsKey(parent))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();

        XElement root;
        if (byKey.TryGetValue(HiddenRootKey, out var catalogRoot))
        {
            root = WriteEntry(catalogRoot, 0, true, roots, childrenByParent, new HashSet<string>(StringComparer.Ordinal));
        }
        else if (roots.Length == 1)
        {
            root = WriteEntry(roots[0], 0, true, null, childrenByParent, new HashSet<string>(StringComparer.Ordinal));
        }
        else
        {
            var hiddenRoot = new BgkEntry(HiddenRootKey, "", "", []);
            root = WriteEntry(hiddenRoot, 0, true, roots, childrenByParent, new HashSet<string>(StringComparer.Ordinal));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = false,
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace
        }))
        {
            document.Save(writer);
        }
        var xml = Encoding.UTF8.GetString(stream.ToArray());
        if (Encoding.UTF8.GetByteCount(xml) > CatalogReaders.MaxBytes)
            throw new InvalidDataException("Der exportierte BGK-Katalog überschreitet 32 MiB.");
        return xml;
    }

    public static void Save(string path, Catalog<BgkEntry> catalog)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Kein BGK-Exportpfad ausgewählt.");
        var xml = Serialize(catalog);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidDataException("Der BGK-Exportpfad enthält keinen Zielordner.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var text = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true))
            {
                text.Write(xml);
                text.Flush();
                output.Flush(flushToDisk: true);
            }

            using (var check = File.OpenRead(temporaryPath))
                _ = CatalogReaders.ReadBgk(check, fullPath);

            if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static XElement WriteEntry(BgkEntry entry, int level, bool isRoot, BgkEntry[]? rootChildren,
        IReadOnlyDictionary<string, BgkEntry[]> childrenByParent, HashSet<string> ancestorKeys)
    {
        if (level >= MaxHierarchyDepth)
            throw new InvalidDataException($"Die BGK-Hierarchie überschreitet {MaxHierarchyDepth} Ebenen.");
        if (!ancestorKeys.Add(entry.Key))
            throw new InvalidDataException("Zyklus in der BGK-Hierarchie bei " + entry.Key + ".");

        var children = rootChildren ?? (childrenByParent.TryGetValue(entry.Key, out var nested) ? nested : []);
        var result = new XElement("Baugruppe",
            new XElement("Key", entry.Key),
            new XElement("Description", entry.Description ?? ""),
            new XElement("NodeProtection", false),
            new XElement("RootNode", isRoot),
            new XElement("ExpandNode", false),
            new XElement("IsSelectedNode", false),
            new XElement("Level", level),
            new XElement("Category", entry.Category ?? ""),
            new XElement("Typmarkierungen", entry.Types.Select(type => new XElement("Typmarkierung",
                new XElement("Typnummer", type.Number),
                new XElement("Typbezeichnung", type.Description ?? "")))),
            new XElement("Baugruppen", children.Select(child => WriteEntry(child, level + 1, false, null, childrenByParent,
                new HashSet<string>(ancestorKeys, StringComparer.Ordinal)))));
        return result;
    }

    private static string ParentKey(string key)
    {
        var separator = key.LastIndexOf('-');
        return separator > 0 ? key[..separator] : "";
    }
}
