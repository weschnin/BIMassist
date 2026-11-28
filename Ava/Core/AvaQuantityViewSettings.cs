using System.IO;
using System.Text.Json;

namespace BIMassist.Ava.Core;

public sealed record AvaQuantityViewSettings
{
    public int SchemaVersion { get; init; } = 1;
    /// <summary>An empty list uses the window's default columns.</summary>
    public IReadOnlyList<AvaQuantityColumnSetting> Columns { get; init; } = [];
    public IReadOnlyList<AvaQuantitySortSetting> Sorts { get; init; } = [];
    public string Grouping { get; init; } = "";
    public bool VisibleOnly { get; init; }
    public bool X31UseRevitElementId { get; init; }
}

public sealed record AvaQuantityColumnSetting(string Key, int DisplayIndex, bool Visible, double Width);
public sealed record AvaQuantitySortSetting(string Key, bool Descending);

/// <summary>Pure JSON presentation state; keys and grouping labels are not filesystem paths.</summary>
public static class AvaQuantityViewSettingsCodec
{
    private const int MaxJsonCharacters = 1024 * 1024;
    private const int MaxColumns = 256;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 16 };

    public static string Serialize(AvaQuantityViewSettings settings)
    {
        Validate(settings);
        var json = JsonSerializer.Serialize(settings, Options);
        if (json.Length > MaxJsonCharacters) throw new InvalidDataException("Die Ansichtseinstellungen sind zu groß.");
        return json;
    }

    public static AvaQuantityViewSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxJsonCharacters) throw new InvalidDataException("Die Ansichtseinstellungen sind zu groß.");
        try
        {
            var settings = JsonSerializer.Deserialize<AvaQuantityViewSettings>(json, Options)
                ?? throw new InvalidDataException("Die Mengenermittlungsansicht ist leer.");
            Validate(settings);
            return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Ungültige Ansichtseinstellungen.", ex);
        }
    }

    private static void Validate(AvaQuantityViewSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != 1 || settings.Columns == null || settings.Sorts == null
            || settings.Columns.Count > MaxColumns || settings.Sorts.Count > MaxColumns
            || settings.Grouping == null || settings.Grouping.Length > 4096)
            throw new InvalidDataException("Ungültige Version oder Grenzen der Ansichtseinstellungen.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var indices = new HashSet<int>();
        foreach (var column in settings.Columns)
        {
            if (column == null || !ValidKey(column.Key) || !keys.Add(column.Key)
                || column.DisplayIndex < 0 || column.DisplayIndex >= MaxColumns || !indices.Add(column.DisplayIndex)
                || !double.IsFinite(column.Width) || column.Width < 1 || column.Width > 10000)
                throw new InvalidDataException("Ungültige oder doppelte Spalteneinstellung.");
        }
        keys.Clear();
        foreach (var sort in settings.Sorts)
            if (sort == null || !ValidKey(sort.Key) || !keys.Add(sort.Key))
                throw new InvalidDataException("Ungültige oder doppelte Sortierung.");
    }

    private static bool ValidKey(string key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 256;
}
