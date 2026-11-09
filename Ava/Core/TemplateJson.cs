using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BIMassist.Ava.Core;

/// <summary>Strict, versioned portable JSON. Serialization permits incomplete drafts;
/// semantic diagnostics are separate. Unknown members and explicit nulls cannot silently erase data.</summary>
public static class TemplateJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // .NET 8 supports required-property metadata, but not the .NET 9+
        // RespectRequiredConstructorParameters/RespectNullableAnnotations switches.
        // CheckJson checks nulls (including collection elements) before migration.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ConfigureStrictContract } },
        Converters = {
            new ExactEnumConverter<ParameterKind>(), new ExactEnumConverter<ParameterScope>(),
            new ExactEnumConverter<ConditionOperator>(), new ExactEnumConverter<OperandKind>(),
            new ExactEnumConverter<QuantityKind>()
        }
    };
    private static void ConfigureStrictContract(JsonTypeInfo type)
    {
        if (type.Kind != JsonTypeInfoKind.Object || type.Type.Namespace != typeof(AvaTemplate).Namespace) return;
        // Guard Serialize/Copy too. Import's CheckJson is deliberately stronger:
        // it also rejects null collection elements and invalid mode-specific nulls.
        var nullability = new NullabilityInfoContext();
        foreach (var property in type.Properties)
            if (property.AttributeProvider is PropertyInfo member &&
                nullability.Create(member).ReadState == NullabilityState.NotNull && property.Get is { } get)
                property.Get = owner => get(owner) ?? throw new JsonException("Null ist nicht erlaubt: " + type.Type.Name + "." + property.Name);
        // Portable catalog records have one public constructor. Mirror required
        // constructor parameters, not optional metadata such as X82SnapshotBase64.
        var constructors = type.Type.GetConstructors();
        if (constructors.Length != 1) return;
        foreach (var parameter in constructors[0].GetParameters().Where(p => !p.HasDefaultValue))
        {
            var property = type.Properties.Single(p => p.PropertyType == parameter.ParameterType &&
                string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            property.IsRequired = true;
        }
    }
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
        if (template.SchemaVersion != 3) throw new InvalidDataException("Zum Speichern wird Schema 3 benötigt; ältere Vorlagen zuerst importieren.");
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
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema is not (1 or 2 or 3))
                throw new InvalidDataException("Fehlende oder nicht unterstützte SchemaVersion.");
            if (schema < 3)
            {
                // Deserialize the complete ORIGINAL contract before adding, moving or removing a field.
                // In particular, an old document must not smuggle in fields that migration overwrites.
                if (schema == 1) _ = JsonSerializer.Deserialize<LegacyTemplate<LegacyQuantity1>>(json, Options);
                else
                {
                    var legacy = JsonSerializer.Deserialize<LegacyTemplate<LegacyQuantity2>>(json, Options)!;
                    foreach (var rule in legacy.Mappings)
                        if (rule.Quantity is { } quantity &&
                            (quantity.Kind is not (QuantityKind.Parameter or QuantityKind.Constant) ||
                             (quantity.Kind == QuantityKind.Parameter ? quantity.Source == null || quantity.ConstantValue != null : quantity.Source != null || quantity.ConstantValue is not > 0)))
                            throw new InvalidDataException("Widersprüchliche oder unvollständige Schema-2-Menge: " + rule.Id);
                }
                var migrated = JsonNode.Parse(json)!.AsObject();
                if (migrated["Mappings"] is JsonArray mappings)
                    foreach (var mapping in mappings)
                        if (mapping is JsonObject rule)
                        {
                            if (rule["Quantity"] is JsonObject quantity)
                            {
                                if (schema == 1)
                                {
                                    quantity["Kind"] = "Parameter";
                                    quantity["ConstantValue"] = null;
                                }
                                quantity["Expression"] = "";
                                quantity["Variables"] = new JsonArray();
                            }
                            var position = new JsonObject {
                                ["Id"] = rule["Id"]?.DeepClone(),
                                ["LvItemId"] = rule["LvItemId"]?.DeepClone(),
                                ["Quantity"] = rule["Quantity"]?.DeepClone()
                            };
                            rule.Remove("LvItemId"); rule.Remove("Quantity");
                            rule["Positions"] = new JsonArray(position);
                        }
                migrated["SchemaVersion"] = 3;
                json = migrated.ToJsonString();
            }
            var template = JsonSerializer.Deserialize<AvaTemplate>(json, Options) ?? throw new InvalidDataException("Leere Vorlage.");
            if (template.ProfileId == Guid.Empty) throw new InvalidDataException("Profil-ID fehlt.");
            foreach (var rule in template.Mappings)
            foreach (var position in rule.Positions)
                if (position.Quantity is { } quantity &&
                    (quantity.Kind switch {
                        QuantityKind.Parameter => quantity.Source == null || quantity.ConstantValue != null || quantity.Expression.Length > 0 || quantity.Variables.Count > 0,
                        QuantityKind.Constant => quantity.Source != null || quantity.ConstantValue is not > 0 || quantity.Expression.Length > 0 || quantity.Variables.Count > 0,
                        QuantityKind.Formula => quantity.Source != null || quantity.ConstantValue != null || string.IsNullOrWhiteSpace(quantity.Expression),
                        _ => true
                    }))
                    throw new InvalidDataException("Widersprüchliche oder unvollständige Mengenart: " + rule.Id);
            return template;
        }
        catch (JsonException ex) { throw new InvalidDataException("Ungültige AVA-Vorlage: " + ex.Message, ex); }
    }
    // Frozen historical wire contracts. Do not reuse the evolving schema-3 model here.
    private sealed class LegacyTemplate<TQuantity> where TQuantity : class
    {
        [JsonRequired] public int SchemaVersion { get; set; }
        [JsonRequired] public Guid ProfileId { get; set; }
        [JsonRequired] public string Name { get; set; } = "";
        [JsonRequired] public string Version { get; set; } = "";
        [JsonRequired] public string ReviewScope { get; set; } = "";
        [JsonRequired] public ParameterSource BgkIdentification { get; set; } = new();
        [JsonRequired] public ParameterSource TypeIdentification { get; set; } = new();
        [JsonRequired] public Catalog<BgkEntry> BgkCatalog { get; set; } = new(new("", "", ""), []);
        [JsonRequired] public Catalog<LvEntry> LvCatalog { get; set; } = new(new("", "", ""), []);
        [JsonRequired] public Catalog<SharedParameter> ParameterCatalog { get; set; } = new(new("", "", ""), []);
        [JsonRequired] public List<LegacyRule<TQuantity>> Mappings { get; set; } = [];
    }
    private sealed class LegacyRule<TQuantity> where TQuantity : class
    {
        [JsonRequired] public Guid Id { get; set; }
        [JsonRequired] public string BgkKey { get; set; } = "";
        [JsonRequired] public string TypeNumber { get; set; } = "";
        [JsonRequired] public string LvItemId { get; set; } = "";
        [JsonRequired] public List<RuleCondition> Conditions { get; set; } = [];
        [JsonRequired] public TQuantity? Quantity { get; set; }
    }
    private sealed class LegacyQuantity1
    {
        [JsonRequired] public ParameterSource Source { get; set; } = new();
        [JsonRequired] public string Unit { get; set; } = "";
    }
    private sealed class LegacyQuantity2
    {
        [JsonRequired] public QuantityKind Kind { get; set; }
        [JsonRequired] public ParameterSource? Source { get; set; }
        [JsonRequired] public decimal? ConstantValue { get; set; }
        [JsonRequired] public string Unit { get; set; } = "";
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
                bool quantity = name is "root.Mappings[].Quantity" or "root.Mappings[].Positions[].Quantity";
                bool nullable = ((name is "root.Mappings[]" or "root.Mappings[].Positions[]") && property.Name == "Quantity") ||
                    (quantity && property.Name == "ConstantValue") ||
                    (quantity && property.Name == "Source" && element.TryGetProperty("Kind", out var kind) && kind.ValueKind == JsonValueKind.String && (kind.GetString() is "Constant" or "Formula"));
                CheckJson(property.Value, name + "." + property.Name, nullable);
            }
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckJson(item, name + "[]");
    }
}
