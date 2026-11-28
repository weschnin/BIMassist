using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BIMassist.Ava.Core;

public sealed record AvaQuantityRun(
    int SchemaVersion,
    Guid RunId,
    DateTimeOffset CreatedUtc,
    string ProjectTitle,
    string ProjectPath,
    string ScopeDescription,
    string TemplateJson,
    IReadOnlyList<AvaQuantityRecord> Records)
{
    public bool VisibleOnly { get; init; }
}

public sealed record AvaQuantityRecord(
    Guid RecordId,
    string BimassistId,
    string LvItemId,
    string Ordinal,
    string Kurztext,
    string Unit,
    string RevitElementId,
    string FormulaWithParameters,
    string FormulaWithValues,
    string GroupingParameter,
    string GroupingValue,
    string FamilyName,
    string TypeName,
    decimal Result)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyGroupingValues =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
    private readonly IReadOnlyDictionary<string, string> groupingValues = EmptyGroupingValues;

    /// <summary>All offered grouping labels and their values, frozen at takeoff time.</summary>
    public IReadOnlyDictionary<string, string> GroupingValues
    {
        get => groupingValues;
        init => groupingValues = value == null ? null! : value.Count == 0 ? EmptyGroupingValues
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                value.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));
    }
}

/// <summary>Versioned, bounded project-payload codec for the single current takeoff run.</summary>
public static class AvaQuantityRunCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxStorageChunkCharacters = 60_000;
    private const int MaxJsonBytes = 256 * 1024 * 1024;
    private const int MaxEncodedCharacters = 360 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static string CreateBimassistId(int ordinal)
    {
        if (ordinal <= 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        long value = ordinal;
        Span<char> token = stackalloc char[7];
        for (int i = token.Length - 1; i >= 0; i--)
        {
            token[i] = digits[(int)(value % digits.Length)];
            value /= digits.Length;
        }
        if (value != 0) throw new ArgumentOutOfRangeException(nameof(ordinal), "BIMassistID-Kapazität überschritten.");
        return "B" + new string(token);
    }

    public static string Serialize(AvaQuantityRun run)
    {
        Validate(run);
        var json = JsonSerializer.Serialize(run, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            throw new InvalidDataException("Der gespeicherte Mengenermittlungslauf überschreitet 256 MiB.");
        return json;
    }

    public static AvaQuantityRun Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
            throw new InvalidDataException("Der gespeicherte Mengenermittlungslauf überschreitet 256 MiB.");
        try
        {
            var run = JsonSerializer.Deserialize<AvaQuantityRun>(json, JsonOptions)
                ?? throw new InvalidDataException("Der gespeicherte Mengenermittlungslauf ist leer.");
            Validate(run);
            return run;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Der gespeicherte Mengenermittlungslauf ist ungültig: " + ex.Message, ex);
        }
    }

    public static IReadOnlyList<string> ToStorageChunks(AvaQuantityRun run)
    {
        var json = Serialize(run);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            writer.Write(json);
        var encoded = Convert.ToBase64String(compressed.ToArray());
        var chunks = new List<string>((encoded.Length + MaxStorageChunkCharacters - 1) / MaxStorageChunkCharacters);
        for (int offset = 0; offset < encoded.Length; offset += MaxStorageChunkCharacters)
            chunks.Add(encoded.Substring(offset, Math.Min(MaxStorageChunkCharacters, encoded.Length - offset)));
        return chunks;
    }

    public static AvaQuantityRun FromStorageChunks(IReadOnlyList<string> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0 || chunks.Any(string.IsNullOrEmpty) || chunks.Any(c => c.Length > MaxStorageChunkCharacters))
            throw new InvalidDataException("Der gespeicherte Mengenermittlungslauf enthält ungültige Datenblöcke.");
        long encodedLength = chunks.Sum(c => (long)c.Length);
        if (encodedLength > MaxEncodedCharacters)
            throw new InvalidDataException("Der gespeicherte Mengenermittlungslauf ist zu groß.");
        try
        {
            var encoded = string.Concat(chunks);
            using var source = new MemoryStream(Convert.FromBase64String(encoded));
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxJsonBytes)
                    throw new InvalidDataException("Der gespeicherte Mengenermittlungslauf überschreitet 256 MiB.");
                output.Write(buffer, 0, read);
            }
            return Deserialize(Encoding.UTF8.GetString(output.ToArray()));
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Die gespeicherten Mengenermittlungsdaten sind nicht Base64-kodiert.", ex);
        }
        catch (InvalidDataException) { throw; }
        catch (IOException ex)
        {
            throw new InvalidDataException("Die gespeicherten Mengenermittlungsdaten konnten nicht entpackt werden.", ex);
        }
    }

    private static bool IsGroupingText(string value) => !value.Any(character => char.IsControl(character)
        || char.IsSurrogate(character) || char.GetUnicodeCategory(character) is
            System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator);

    private static void Validate(AvaQuantityRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SchemaVersion != CurrentSchemaVersion || run.RunId == Guid.Empty)
            throw new InvalidDataException($"Nicht unterstützte Mengenermittlungs-Schemaversion: {run.SchemaVersion}.");
        if (run.ProjectTitle == null || run.ProjectPath == null || run.ScopeDescription == null || run.TemplateJson == null || run.Records == null)
            throw new InvalidDataException("Mengenermittlungslauf enthält fehlende Pflichtdaten.");
        var recordIds = new HashSet<Guid>();
        var bimassistIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in run.Records)
        {
            if (record == null || record.RecordId == Guid.Empty || !recordIds.Add(record.RecordId))
                throw new InvalidDataException("Leere oder doppelte Mengenermittlungs-Datensatz-ID.");
            if (record.BimassistId == null || record.LvItemId == null || record.Ordinal == null || record.Kurztext == null
                || record.Unit == null || record.RevitElementId == null || record.FormulaWithParameters == null
                || record.FormulaWithValues == null || record.GroupingParameter == null || record.GroupingValue == null
                || record.FamilyName == null || record.TypeName == null)
                throw new InvalidDataException("Mengenermittlungsdatensatz enthält fehlende Pflichtdaten.");
            if (record.GroupingValues == null || record.GroupingValues.Count > 4096
                || record.GroupingValues.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 1024
                    || entry.Value == null || entry.Value.Length > 32768
                    || !IsGroupingText(entry.Key) || !IsGroupingText(entry.Value)))
                throw new InvalidDataException("Mengenermittlungsdatensatz enthält ungültige Gruppierungswerte.");
            if (record.BimassistId.Length != 8 || record.BimassistId[0] != 'B'
                || record.BimassistId.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))
                || !bimassistIds.Add(record.BimassistId))
                throw new InvalidDataException("Leere, ungültige oder doppelte BIMassistID.");
            if (string.IsNullOrWhiteSpace(record.LvItemId) || string.IsNullOrWhiteSpace(record.Ordinal)
                || string.IsNullOrWhiteSpace(record.Unit) || string.IsNullOrWhiteSpace(record.RevitElementId))
                throw new InvalidDataException("Mengenermittlungsdatensatz hat keine vollständige LV-, Einheiten- oder Revit-Elementidentität.");
        }
    }
}
