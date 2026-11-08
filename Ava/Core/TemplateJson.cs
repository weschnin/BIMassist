using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BIMassist.Ava.Core;

/// <summary>Strict, versioned portable JSON. Serialization permits incomplete drafts;
/// semantic diagnostics are separate. Unknown members and explicit nulls cannot silently erase data.</summary>
public static class TemplateJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = {
            new ExactEnumConverter<ParameterKind>(), new ExactEnumConverter<ParameterScope>(),
            new ExactEnumConverter<ConditionOperator>(), new ExactEnumConverter<OperandKind>()
        }
    };
    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is string token && Enum.GetNames<T>().Contains(token, StringComparer.Ordinal))
                return Enum.Parse<T>(token);
            throw new JsonException("Ungültiger Enum-Wert: " + typeof(T).Name);
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException("Ungültiger Enum-Wert: " + typeof(T).Name));
    }
    public static string Serialize(AvaTemplate template) => JsonSerializer.Serialize(template, Options);
    public static AvaTemplate Deserialize(string json)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > CatalogReaders.MaxBytes) throw new InvalidDataException("Vorlage überschreitet 32 MiB.");
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 64 });
            CheckJson(document.RootElement, "root");
            var template = JsonSerializer.Deserialize<AvaTemplate>(json, Options) ?? throw new InvalidDataException("Leere Vorlage.");
            if (template.SchemaVersion != 1) throw new InvalidDataException("Nicht unterstützte SchemaVersion: " + template.SchemaVersion);
            if (template.ProfileId == Guid.Empty) throw new InvalidDataException("Profil-ID fehlt.");
            return template;
        }
        catch (JsonException ex) { throw new InvalidDataException("Ungültige AVA-Vorlage: " + ex.Message, ex); }
    }
    public static AvaTemplate Load(string path)
    {
        using var stream = File.OpenRead(path);
        try
        {
            return Deserialize(new UTF8Encoding(false, true).GetString(CatalogReaders.ReadBytes(stream)).TrimStart('\uFEFF'));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Ungültige UTF-8-Kodierung der AVA-Vorlage.", ex);
        }
    }
    public static void Save(string path, AvaTemplate template)
    {
        var json = Serialize(template);
        Deserialize(json); // verify structure before touching the destination
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            // A same-directory replacement preserves the existing file if writing the temporary fails.
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
            if (File.ReadAllText(fullPath, Encoding.UTF8) != json) throw new IOException("Gespeicherte Datei konnte nicht verifiziert werden.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
    private static void CheckJson(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Null && name != "Quantity") throw new InvalidDataException("Null ist nicht erlaubt: " + name);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Doppelter JSON-Schlüssel: " + property.Name);
                CheckJson(property.Value, property.Name);
            }
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckJson(item, "Array-Eintrag");
    }
}
