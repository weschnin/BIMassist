using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BIMassist.Ava.Core;

public enum X31ReviewRowKind { Quantity, GroupComment, ElementIdComment, Comment, Continuation }

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
    bool SelectionIncomplete)
{
    public string ShortText { get; init; } = "";
    public string Unit { get; init; } = "";
    public string FormulaNumber { get; init; } = "";
    public string Result { get; init; } = "";
    public string LogicalApproach { get; init; } = "";
}

/// <summary>Read-only catalog complement keyed by exact GAEB item identity, never by similar text.</summary>
public sealed record X31ReviewPosition(string Ordinal, string ShortText, string Unit, string BoqId = "");

public sealed record X31ReviewDocument(
    string SourceName,
    IReadOnlyList<X31ReviewRow> Rows,
    IReadOnlyList<string> Warnings)
{
    public string TemplateSource { get; init; } = "";
}

/// <summary>Reads physical REB rows without modifying them. An optional emitted-subset evaluator evaluates logical REB91 approaches.</summary>
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
        @"(?:Revit-ElementID:\s*(-?[0-9]+)\s*$|\(ID:\s*(-?[0-9]+)\))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> GroupKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Baubereich", "Gewerk", "Familie und Typ"
    };

    public static X31ReviewDocument ReadFile(string path, IReadOnlyDictionary<string, X31ReviewPosition>? positions = null,
        Func<string, decimal>? evaluateReb91 = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Es wurde keine X31-Datei ausgewählt.");
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Die ausgewählte X31-Datei wurde nicht gefunden.", fullPath);
        if (info.Length > MaxBytes) throw new InvalidDataException("Die X31-Datei überschreitet die zulässige Größe von 32 MiB.");
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream, fullPath, positions, evaluateReb91);
    }

    public static X31ReviewDocument Read(Stream input, string sourceName = "X31",
        IReadOnlyDictionary<string, X31ReviewPosition>? positions = null, Func<string, decimal>? evaluateReb91 = null)
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
        var itemBoqIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var item in root.Descendants(Ns + "BoQ").Descendants(Ns + "Item"))
        {
            string? currentGroupKind = null;
            string? currentGroupLabel = null;
            string? pendingElementId = null;
            var formulaCommentContinues = false;
            var itemId = (string?)item.Attribute("ID") ?? string.Empty;
            if (!itemBoqIds.TryAdd(itemId, (string?)item.Ancestors(Ns + "BoQ").FirstOrDefault()?.Attribute("ID") ?? ""))
                throw new InvalidDataException("Doppelte LV-Positionsidentität in X31: " + itemId);
            var position = GetPosition(item);
            var takeoffs = item.Element(Ns + "QtyDeterm")?.Elements(Ns + "QDetermItem").ToArray() ?? [];
            for (var takeoffIndex = 0; takeoffIndex < takeoffs.Length; takeoffIndex++)
            {
                var takeoff = takeoffs[takeoffIndex];
                if (rawRows.Count >= MaxRows)
                    throw new InvalidDataException("Die X31-Datei enthält mehr als 200.000 Zeilen; sie wird nicht geöffnet.");
                var rowText = (string?)takeoff.Element(Ns + "QTakeoff")?.Attribute("Row");
                if (rowText is null || rowText.Length < 31)
                {
                    pendingElementId = null;
                    formulaCommentContinues = false;
                    warnings.Add($"Zeile {rawRows.Count + 1}: fehlende oder zu kurze REB-Zeile.");
                    rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId, X31ReviewRowKind.Comment,
                        "REB-Zeile fehlt oder ist unvollständig", string.Empty, null, currentGroupKind, currentGroupLabel));
                    continue;
                }

                if (rowText[12] == '*')
                {
                    var commentField = rowText.Substring(13, Math.Min(56, rowText.Length - 13)).TrimEnd();
                    var comment = commentField.TrimStart();
                    var separator = comment.IndexOf(':');
                    var kind = separator > 0 ? comment[..separator].Trim() : string.Empty;
                    var label = separator >= 0 ? comment[(separator + 1)..].Trim() : string.Empty;
                    var symbolicComment = comment.StartsWith("Formel:", StringComparison.Ordinal) || formulaCommentContinues;
                    var parsedId = symbolicComment ? null : ParseElementId(comment, warnings, rawRows.Count + 1);
                    if (!symbolicComment && StartsIdLabel(comment) && parsedId == null)
                        throw new InvalidDataException($"Zeile {rawRows.Count + 1}: ungültige beschriftete Revit-ElementID in Kommentar.");
                    // Only a standalone labelled comment is a provenance carrier.
                    if (parsedId != null && !IsStandaloneId(comment)) parsedId = null;
                    if (parsedId != null) pendingElementId = parsedId;
                    else if (!symbolicComment) pendingElementId = null;
                    formulaCommentContinues = symbolicComment && commentField.Length >= 55;
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

                var method = rowText.Substring(29, 2).Trim();

                var groupExtension = takeoff.Element(XName.Get("Explanation", "BVBS"));
                var explanation = groupExtension?.Value;
                var remarkText = rowText.Substring(13, 9).Trim();
                var explanationId = ParseElementId(explanation, warnings, rawRows.Count + 1);
                var rawId = ParseElementId(remarkText, warnings, rawRows.Count + 1);
                if (StartsIdLabel(explanation) && explanationId == null)
                    throw new InvalidDataException($"Zeile {rawRows.Count + 1}: REB-Bemerkung {remarkText} hat eine ungültige BVBS:Explanation-ElementID.");
                if (StartsIdLabel(remarkText) && rawId == null)
                    throw new InvalidDataException($"Zeile {rawRows.Count + 1}: ungültige beschriftete Revit-ElementID in REB-Bemerkung.");
                var elementId = ReconcileIds(rawRows.Count + 1, explanationId, rawId, pendingElementId);
                // Numeric-only remarks remain ambiguous; compare them only when another source proves identity.
                if (elementId != null && long.TryParse(remarkText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var numericId)
                    && !remarkText.StartsWith('+'))
                    elementId = ReconcileIds(rawRows.Count + 1, elementId, numericId.ToString(CultureInfo.InvariantCulture));
                pendingElementId = null;
                formulaCommentContinues = false;
                var rowGroupKind = (string?)groupExtension?.Attribute("GroupKind") ?? currentGroupKind;
                var rowGroupLabel = (string?)groupExtension?.Attribute("GroupLabel") ?? currentGroupLabel;
                var symbolicFormula = (string?)groupExtension?.Attribute("Formula");
                var segments = new List<string> { rowText.Substring(31, Math.Min(38, rowText.Length - 31)).Trim() };
                while (method == "91" && !segments[^1].EndsWith('=') && takeoffIndex + 1 < takeoffs.Length)
                {
                    var next = (string?)takeoffs[takeoffIndex + 1].Element(Ns + "QTakeoff")?.Attribute("Row");
                    if (next is null || next.Length < 31 || next[12] == '*' || next.Substring(29, 2) != "91") break;
                    if (rawRows.Count + segments.Count >= MaxRows)
                        throw new InvalidDataException("Die X31-Datei enthält mehr als 200.000 Zeilen; sie wird nicht geöffnet.");
                    segments.Add(next.Substring(31, Math.Min(38, next.Length - 31)).Trim());
                    takeoffIndex++;
                }
                var logicalApproach = string.Concat(segments);
                for (var segment = 0; segment < segments.Count; segment++)
                    rawRows.Add(new MutableRow(rawRows.Count + 1, position, itemId,
                        segment == 0 ? X31ReviewRowKind.Quantity : X31ReviewRowKind.Continuation,
                        segment == 0 ? (!string.IsNullOrWhiteSpace(symbolicFormula) ? symbolicFormula
                            : string.IsNullOrWhiteSpace(explanation) ? remarkText : explanation.Trim()) : "",
                        segments[segment], elementId, rowGroupKind, rowGroupLabel)
                    { LogicalApproach = logicalApproach, FormulaNumber = method });
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

        var rows = rawRows.Select(row => row.ToImmutable(groupMembership)).Select(row =>
        {
            var result = row;
            if (row.Kind == X31ReviewRowKind.Quantity)
            {
                decimal? value = null;
                try
                {
                    if (row.FormulaNumber != "91") throw new InvalidDataException("REB-Formelnummer " + row.FormulaNumber);
                    var number = row.LogicalApproach.TrimEnd('=').Trim();
                    if (evaluateReb91 != null) value = evaluateReb91(row.LogicalApproach);
                    else if (decimal.TryParse(number.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var literal)) value = literal;
                    else throw new InvalidDataException("Für diesen REB91-Ansatz ist kein Auswerter verfügbar.");
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or ArithmeticException or ArgumentException)
                {
                    warnings.Add($"Zeile {row.Number}: REB-Berechnung nicht unterstützt oder ungültig; Ergebnis bleibt leer. {ex.Message}");
                }
                result = result with { Result = value?.ToString(CultureInfo.GetCultureInfo("de-DE")) ?? "" };
            }
            if (positions != null && positions.TryGetValue(row.LvItemId, out var position))
            {
                var boqId = itemBoqIds[row.LvItemId];
                if (position.BoqId == boqId)
                    result = result with { Position = position.Ordinal, ShortText = position.ShortText, Unit = position.Unit };
                else warnings.Add($"LV-Position {row.LvItemId}: AVA-Vorlage gehört zu einem anderen LV; Angaben nicht übernommen.");
            }
            else if (positions != null)
                warnings.Add($"LV-Position {row.LvItemId}: keine passende Position in der AVA-Vorlage; Kurztext und Einheit fehlen.");
            return result;
        }).ToArray();
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
        var matches = ElementIdPattern.Matches(text.Trim());
        if (matches.Count > 1)
            throw new InvalidDataException($"Zeile {rowNumber}: mehrere beschriftete Revit-ElementIDs in derselben Provenienzangabe.");
        if (matches.Count == 0) return null;
        var match = matches[0];
        var id = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        if (!long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            warnings.Add($"Zeile {rowNumber}: Revit-ElementID liegt außerhalb des unterstützten 64-Bit-Bereichs.");
            return null;
        }
        return id;
    }

    private static bool IsStandaloneId(string text) => Regex.IsMatch(text,
        @"^(?:\(ID:\s*-?[0-9]+\)|Revit-ElementID:\s*-?[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool StartsIdLabel(string? text) => !string.IsNullOrWhiteSpace(text)
        && Regex.IsMatch(text.TrimStart(), @"^(?:\(ID:|Revit-ElementID:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? ReconcileIds(int rowNumber, params string?[] sources)
    {
        var parsed = sources.Where(value => value != null)
            .Select(value => long.Parse(value!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
            .Distinct().ToArray();
        if (parsed.Length > 1)
            throw new InvalidDataException($"Zeile {rowNumber}: widersprüchliche Revit-ElementIDs {string.Join(" und ", parsed)}; X31-Prüfung abgebrochen.");
        return parsed.Length == 0 ? null : parsed[0].ToString(CultureInfo.InvariantCulture);
    }

    private static string GetPosition(XElement item)
    {
        var numbers = item.AncestorsAndSelf().Where(node => node.Name == Ns + "Item" || node.Name == Ns + "BoQCtgy").Reverse()
            .Select(node => ((string?)node.Attribute("RNoPart"))?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
        var fullNumber = numbers.LastOrDefault(value => value!.Contains('.', StringComparison.Ordinal));
        if (fullNumber is not null) return fullNumber;
        var breakdown = item.Ancestors(Ns + "BoQ").FirstOrDefault()?.Element(Ns + "BoQInfo")?.Elements(Ns + "BoQBkdn").ToArray() ?? [];
        if (numbers.Length > 0)
        {
            var padded = numbers.Select((part, index) => index < breakdown.Length
                && (string?)breakdown[index].Element(Ns + "Num") == "Yes"
                && part!.All(char.IsAsciiDigit)
                && int.TryParse((string?)breakdown[index].Element(Ns + "Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var width)
                && width is >= 1 and <= 14 ? part.PadLeft(width, '0') : part);
            var ordinal = string.Join('.', padded);
            var suffix = (string?)item.Attribute("RNoIndex");
            return suffix == null ? ordinal : ordinal + "." + suffix;
        }
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
        public string LogicalApproach { get; init; } = "";
        public string FormulaNumber { get; init; } = "";
        public X31ReviewRow ToImmutable(Dictionary<GroupKey, MutableGroup> groups)
        {
            if (kind == X31ReviewRowKind.GroupComment && groupKey is { } key && groups.TryGetValue(key, out var group))
                return new X31ReviewRow(number, position, itemId, kind, comment, formula, null, groupKind, groupLabel,
                    group.ElementIds.OrderBy(value => value, StringComparer.Ordinal).ToArray(), group.SelectionIncomplete);
            var ids = elementId is null ? Array.Empty<string>() : [elementId];
            return new X31ReviewRow(number, position, itemId, kind, comment, formula, elementId, groupKind, groupLabel,
                ids, false) { LogicalApproach = LogicalApproach, FormulaNumber = FormulaNumber };
        }
    }
}
