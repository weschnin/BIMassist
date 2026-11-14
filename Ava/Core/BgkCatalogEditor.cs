using System.IO;

namespace BIMassist.Ava.Core;

/// <summary>Safe, validated editing operations for the BGK snapshot stored in an AVA template.</summary>
public static class BgkCatalogEditor
{
    public static Catalog<BgkEntry> Add(Catalog<BgkEntry> catalog, BgkEntry entry)
    {
        if (entry.Key == "///") throw new InvalidDataException("Der BGK-Schlüssel '///' ist reserviert und wird im Katalogbaum nicht angezeigt.");
        if (catalog.Entries.Any(existing => existing.Key == entry.Key))
            throw new InvalidDataException("Dieser BGK-Schlüssel ist bereits vorhanden.");
        return Manual(catalog, catalog.Entries.Append(entry));
    }

    public static Catalog<BgkEntry> Update(Catalog<BgkEntry> catalog, string key, BgkEntry updated,
        IEnumerable<MappingRule> mappings)
    {
        if (updated.Key == "///") throw new InvalidDataException("Der BGK-Schlüssel '///' ist reserviert und wird im Katalogbaum nicht angezeigt.");
        if (updated.Key != key)
            throw new InvalidDataException("BGK-Schlüssel können nicht umbenannt werden. Entfernen Sie zuerst alle betroffenen Zuordnungen und fügen Sie den Eintrag mit dem neuen Schlüssel neu hinzu.");
        if (!catalog.Entries.Any(entry => entry.Key == key)) throw new InvalidDataException("BGK-Schlüssel nicht gefunden: " + key);
        var retainedTypes = updated.Types.Select(type => type.Number).ToHashSet(StringComparer.Ordinal);
        var removedTypeNumbers = catalog.Entries.Single(entry => entry.Key == key).Types
            .Select(type => type.Number).Where(number => !retainedTypes.Contains(number)).ToHashSet(StringComparer.Ordinal);
        var references = mappings.Where(rule => rule.BgkKey == key && removedTypeNumbers.Contains(rule.TypeNumber)).ToList();
        if (references.Count > 0)
            throw new InvalidDataException($"Änderung nicht möglich: {references.Count} Zuordnung(en) verwenden entfernte Typnummern ({string.Join(", ", removedTypeNumbers)}). Entfernen Sie zuerst die betroffenen Zuordnungen.");
        var candidate = Manual(catalog, catalog.Entries.Select(entry => entry.Key == key ? updated : entry));
        TemplateEditor.EnsureValid(new AvaTemplate { BgkCatalog = candidate });
        return candidate;
    }

    public static Catalog<BgkEntry> Remove(Catalog<BgkEntry> catalog, string key, string? typeNumber,
        IEnumerable<MappingRule> mappings)
    {
        var affectedKeys = catalog.Entries.Where(entry => entry.Key == key
            || typeNumber == null && entry.Key.StartsWith(key + "-", StringComparison.Ordinal))
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var affectedRules = mappings.Where(rule => affectedKeys.Contains(rule.BgkKey)
            && (typeNumber == null || rule.TypeNumber == typeNumber)).ToList();
        if (affectedRules.Count > 0)
            throw new InvalidDataException($"Löschen nicht möglich: {affectedRules.Count} Zuordnung(en) verwenden diesen BGK-/Typ-Schlüssel. Entfernen Sie zuerst die Zuordnungen im Register 'Zuordnungen'; Mengenregeln werden nicht stillschweigend gelöscht.");
        if (typeNumber == null && affectedKeys.Count == 0) throw new InvalidDataException("BGK-Schlüssel nicht gefunden: " + key);
        if (typeNumber != null && !catalog.Entries.Any(entry => entry.Key == key && entry.Types.Any(type => type.Number == typeNumber)))
            throw new InvalidDataException($"Typnummer {typeNumber} ist in BGK {key} nicht vorhanden.");
        var entries = catalog.Entries
            .Where(entry => !affectedKeys.Contains(entry.Key) || typeNumber != null)
            .Select(entry => typeNumber != null && entry.Key == key
                ? entry with { Types = entry.Types.Where(type => type.Number != typeNumber).ToList() } : entry).ToList();
        return Manual(catalog, entries);
    }

    private static Catalog<BgkEntry> Manual(Catalog<BgkEntry> previous, IEnumerable<BgkEntry> entries)
    {
        var source = previous.Source;
        var result = new Catalog<BgkEntry>(new SourceStamp(source.FileName, "BIMassist manuell bearbeitet", ""), entries.ToList());
        TemplateEditor.EnsureValid(new AvaTemplate { BgkCatalog = result });
        return result;
    }
}
