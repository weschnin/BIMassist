using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BIMassist.Ava.Core;

public enum X31ReviewRowKind { Quantity, GroupComment, ElementIdComment, Comment }

public sealed record X31ReviewRow(
    int Number,
    string Position,
    string LvItemId,
    X31ReviewRowKind Kind,
    string Comment,
    string Formula,
    string? ElementId,
    string? GroupKind,
    string? GroupLabel,
    IReadOnlyList<string> ElementIdsForSelection,
    bool SelectionIncomplete);

public sealed record X31ReviewDocument(
    string SourceName,
    IReadOnlyList<X31ReviewRow> Rows,
    IReadOnlyList<string> Warnings);

/// <summary>Reads the fixed-row subset written by X31Writer for review; it does not evaluate GAEB formulas.</summary>
public static class X31ReviewReader
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private const int MaxRows = 200_000;

    public static IReadOnlyList<string> FindMissingElementIds(X31ReviewDocument review, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(exists);
        return review.Rows.SelectMany(row => row.ElementIdsForSelection)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !exists(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }
    private const string NamespaceUri = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";
    private static readonly XNamespace Ns = NamespaceUri;
    private static readonly Regex ElementIdPattern = new(
        @"Revit-ElementID:\s*(-?[0-9]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> GroupKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Baubereich", "Gewerk", "Familie und Typ"
    };

    public static X31ReviewDocument ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Es wurde keine X31-Datei ausgewählt.");
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Die ausgewählte X31-Datei wurde nicht gefunden.", fullPath);
        if (info.Length > MaxBytes) throw new InvalidDataException("Die X31-Datei überschreitet die zulässige Größe von 32 MiB.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream, fullPath);
    }

    public static X31ReviewDocument Read(Stream input, string sourceName = "X31")
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.CanSeek && input.Length > MaxBytes)
            throw new InvalidDataException("Die X31-Datei überschreitet die zulässige Größe von 32 MiB.");

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
                MaxCharactersFromEntities = 0,
                CloseInput = false,
                IgnoreComments = true
            };
            using var reader = XmlReader.Create(input, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Die ausgewählte Datei enthält kein sicheres, gültiges X31-XML.", ex);
        }

        var root = document.Root;
        if (root?.Name != Ns + "GAEB")
            throw new InvalidDataException("Die Datei ist kein GAEB DA31/3.3-X31-Dokument.");
        var info = root.Element(Ns + "GAEBInfo");
        if ((string?)info?.Element(Ns + "Version") != "3.3" || (string?)info.Element(Ns + "VersDate") != "2023-01")
            throw new InvalidDataException("Unterstützt werden GAEB DA31/3.3-Dokumente (Version 3.3, VersDate 2023-01).");

        var rawRows = new List<MutableRow>();
        var groupMembership = new Dictionary<GroupKey, MutableGroup>();
        var warnings = new List<string>();
        foreach (var item in root.Descendants(Ns + "BoQ").Descendants(Ns + "Item"))
        {
            string? currentGroupKind = null;
            string? currentGroupLabel = null;
            var itemId = (string?)item.Attribute("ID") ?? string.Empty;
            var position = GetPosition(item);
            foreach (var takeoff in item.Element(Ns + "QtyDeterm")?.Elements(Ns + "QDetermItem") ?? [])
            {
                if (rawRows.Count >= MaxRows)
                    throw new InvalidDataException("Die X31-Datei enthält mehr als 200.000 Zeilen; sie wird nicht geöffnet.");
                var rowText = (string?)takeoff.Element(Ns + "QTakeoff")?.Attribute("Row");
                if (rowText is null || rowText.Length < 31)
                {
                    warnings.Add($"Zeile {rawRows.Count + 1}: fehlende oder zu kurze REB-Zeile.");
                    rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId, X31ReviewRowKind.Comment,
                        "REB-Zeile fehlt oder ist unvollständig", string.Empty, null, currentGroupKind, currentGroupLabel));
                    continue;
                }

                if (rowText[12] == '*')
                {
                    var comment = rowText.Substring(13, Math.Min(56, rowText.Length - 13)).Trim();
                    var separator = comment.IndexOf(':');
                    var kind = separator > 0 ? comment[..separator].Trim() : string.Empty;
                    var label = separator >= 0 ? comment[(separator + 1)..].Trim() : string.Empty;
                    var parsedId = ParseElementId(comment, warnings, rawRows.Count + 1);
                    if (parsedId is not null)
                    {
                        rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId, X31ReviewRowKind.ElementIdComment,
                            comment, string.Empty, parsedId, currentGroupKind, currentGroupLabel));
                        if (currentGroupKind is not null && currentGroupLabel is not null)
                            GetGroup(groupMembership, currentGroupKind, currentGroupLabel).ElementIds.Add(parsedId);
                    }
                    else if (separator >= 0 && label.Length > 0 && GroupKinds.Contains(kind))
                    {
                        currentGroupKind = kind;
                        currentGroupLabel = label;
                        var key = new GroupKey(kind, label);
                        GetGroup(groupMembership, kind, label);
                        rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId, X31ReviewRowKind.GroupComment,
                            comment, string.Empty, null, kind, label, key));
                    }
                    else
                    {
                        rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId, X31ReviewRowKind.Comment,
                            comment, string.Empty, null, currentGroupKind, currentGroupLabel));
                    }
                    continue;
                }

                if (rowText.Substring(29, 2) != "91")
                {
                    var remark = rowText.Substring(13, Math.Min(9, rowText.Length - 13)).Trim();
                    rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId, X31ReviewRowKind.Comment,
                        remark, string.Empty, null, currentGroupKind, currentGroupLabel));
                    continue;
                }

                var explanation = takeoff.Elements().FirstOrDefault(element => element.Name.LocalName == "Explanation")?.Value;
                var remarkText = rowText.Substring(13, 9).Trim();
                var numericRemark = long.TryParse(remarkText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var remarkId)
                    && remarkText[0] != '+';
                var explanationId = ParseElementId(explanation, warnings, rawRows.Count + 1);
                var explanationLabelsElementId = explanation?.TrimStart()
                    .StartsWith("Revit-ElementID:", StringComparison.OrdinalIgnoreCase) == true;
                if (numericRemark && explanationLabelsElementId && explanationId is null)
                    throw new InvalidDataException($"Zeile {rawRows.Count + 1}: REB-Bemerkung {remarkText} hat eine ungültige BVBS:Explanation-ElementID.");
                if (numericRemark && explanationId is not null)
                {
                    if (long.Parse(explanationId, CultureInfo.InvariantCulture) != remarkId)
                        throw new InvalidDataException($"Zeile {rawRows.Count + 1}: REB-Bemerkung {remarkText} widerspricht BVBS:Explanation {explanationId}; X31-Prüfung abgebrochen.");
                }
                // A bare numeric REB remark is not self-identifying; only the
                // labelled extension emitted by X31Writer proves it's a Revit ID.
                var elementId = explanationId;
                var groupExtension = takeoff.Elements().FirstOrDefault(element => element.Name.LocalName == "Explanation");
                var rowGroupKind = (string?)groupExtension?.Attribute("GroupKind") ?? currentGroupKind;
                var rowGroupLabel = (string?)groupExtension?.Attribute("GroupLabel") ?? currentGroupLabel;
                var formula = rowText.Substring(31, Math.Min(38, rowText.Length - 31)).Trim();
                rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId, X31ReviewRowKind.Quantity,
                    string.IsNullOrWhiteSpace(explanation) ? remarkText : explanation.Trim(), formula, elementId,
                    rowGroupKind, rowGroupLabel));
                if (rowGroupKind is not null && rowGroupLabel is not null)
                {
                    var group = GetGroup(groupMembership, rowGroupKind, rowGroupLabel);
                    if (elementId is null) group.SelectionIncomplete = true;
                    else group.ElementIds.Add(elementId);
                }
                if (elementId is null)
                    warnings.Add($"Zeile {rawRows.Count}: keine gültige Revit-ElementID in REB-Bemerkung oder BVBS:Explanation.");
            }
        }

        var rows = rawRows.Select(row => row.ToImmutable(groupMembership)).ToArray();
        return new X31ReviewDocument(sourceName, rows, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static MutableGroup GetGroup(Dictionary<GroupKey, MutableGroup> groups, string kind, string label)
    {
        var key = new GroupKey(kind, label);
        if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new MutableGroup());
        return group;
    }

    private static string? ParseElementId(string? text, List<string> warnings, int rowNumber)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = ElementIdPattern.Match(text.Trim());
        if (!match.Success) return null;
        if (!long.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            warnings.Add($"Zeile {rowNumber}: Revit-ElementID liegt außerhalb des unterstützten 64-Bit-Bereichs.");
            return null;
        }
        return match.Groups[1].Value;
    }

    private static string GetPosition(XElement item)
    {
        var numbers = item.AncestorsAndSelf(Ns + "Item").Reverse()
            .Select(node => ((string?)node.Attribute("RNoPart"))?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var fullNumber = numbers.LastOrDefault(value => value!.Contains('.', StringComparison.Ordinal));
        if (fullNumber is not null) return fullNumber;
        if (numbers.Length > 0) return string.Join('.', numbers);
        return (string?)item.Attribute("ID") ?? string.Empty;
    }

    private readonly record struct GroupKey(string Kind, string Label);
    private sealed class MutableGroup
    {
        public HashSet<string> ElementIds { get; } = new(StringComparer.Ordinal);
        public bool SelectionIncomplete { get; set; }
    }

    private sealed class MutableRow(int number, string position, string itemId, X31ReviewRowKind kind,
        string comment, string formula, string? elementId, string? groupKind, string? groupLabel,
        GroupKey? groupKey = null)
    {
        public X31ReviewRow ToImmutable(Dictionary<GroupKey, MutableGroup> groups)
        {
            if (kind == X31ReviewRowKind.GroupComment && groupKey is { } key && groups.TryGetValue(key, out var group))
                return new X31ReviewRow(number, position, itemId, kind, comment, formula, null, groupKind, groupLabel,
                    group.ElementIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(), group.SelectionIncomplete);
            var ids = elementId is null ? Array.Empty<string>() : [elementId];
            return new X31ReviewRow(number, position, itemId, kind, comment, formula, elementId, groupKind, groupLabel,
                ids, false);
        }
    }
}
