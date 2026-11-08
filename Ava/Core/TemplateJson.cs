using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            new ExactEnumConverter<ConditionOperator>(), new ExactEnumConverter<OperandKind>(),
            new ExactEnumConverter<QuantityKind>()
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
    public static string Serialize(AvaTemplate template)
    {
        if (template.SchemaVersion != 2) throw new InvalidDataException("Zum Speichern wird Schema 2 benötigt; ältere Vorlagen zuerst importieren.");
        return JsonSerializer.Serialize(template, Options);
    }
    public static AvaTemplate Deserialize(string json)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > CatalogReaders.MaxBytes) throw new InvalidDataException("Vorlage überschreitet 32 MiB.");
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 64 });
            CheckJson(document.RootElement, "root");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema is not (1 or 2))
                throw new InvalidDataException("Fehlende oder nicht unterstützte SchemaVersion.");
            if (schema == 1)
            {
                var migrated = JsonNode.Parse(json)!.AsObject();
                if (migrated["Mappings"] is JsonArray mappings)
                    foreach (var mapping in mappings)
                        if (mapping is JsonObject rule && rule["Quantity"] is JsonObject quantity)
                        {
                            // Validate the original shape before supplying any new required fields.
                            if (quantity.Count != 2 || !quantity.ContainsKey("Source") || !quantity.ContainsKey("Unit"))
                                throw new InvalidDataException("Schema 1 erwartet ausschließlich Source und Unit für die Menge.");
                            quantity["Kind"] = "Parameter";
                            quantity["ConstantValue"] = null;
                        }
                migrated["SchemaVersion"] = 2;
                json = migrated.ToJsonString();
            }
            var template = JsonSerializer.Deserialize<AvaTemplate>(json, Options) ?? throw new InvalidDataException("Leere Vorlage.");
            if (template.ProfileId == Guid.Empty) throw new InvalidDataException("Profil-ID fehlt.");
            foreach (var rule in template.Mappings)
                if (rule.Quantity is { } quantity &&
                    (quantity.Kind == QuantityKind.Parameter ? quantity.Source == null || quantity.ConstantValue != null : quantity.Source != null || quantity.ConstantValue == null))
                    throw new InvalidDataException("Widersprüchliche oder unvollständige Mengenart: " + rule.Id);
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
    private static void CheckJson(JsonElement element, string name, bool allowNull = false)
    {
        if (element.ValueKind == JsonValueKind.Null && !allowNull) throw new InvalidDataException("Null ist nicht erlaubt: " + name);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Doppelter JSON-Schlüssel: " + property.Name);
                bool quantity = name == "root.Mappings[].Quantity";
                bool nullable = (name == "root.Mappings[]" && property.Name == "Quantity") ||
                    (quantity && property.Name == "ConstantValue") ||
                    (quantity && property.Name == "Source" && element.TryGetProperty("Kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "Constant");
                CheckJson(property.Value, name + "." + property.Name, nullable);
            }
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckJson(item, name + "[]");
    }
}
