using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Protocol;


namespace BIMassist.Mcp.Contracts.Serialization;

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value)
    {
        ContractValidator.Validate(value);
        string json = JsonSerializer.Serialize(value, Options);
        EnsureWithinContractLimit(Encoding.UTF8.GetByteCount(json));
        return json;
    }

    public static T Deserialize<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        EnsureWithinContractLimit(Encoding.UTF8.GetByteCount(json));
        if (typeof(T) == typeof(PlanSetParameterValueRequest) || typeof(T) == typeof(BridgeRequest))
        {
            using JsonDocument document = JsonDocument.Parse(json);
            ValidateInboundShape<T>(document.RootElement);
        }
        T value = JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException($"JSON did not contain a {typeof(T).Name} value.");
        ContractValidator.Validate(value);
        return value;
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        EnsureWithinContractLimit(utf8Json.Length);
        if (typeof(T) == typeof(PlanSetParameterValueRequest) || typeof(T) == typeof(BridgeRequest))
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray());
            ValidateInboundShape<T>(document.RootElement);
        }
        T value = JsonSerializer.Deserialize<T>(utf8Json, Options)
            ?? throw new JsonException($"JSON did not contain a {typeof(T).Name} value.");
        ContractValidator.Validate(value);
        return value;
    }

    private static void ValidateInboundShape<T>(JsonElement root)
    {
        if (typeof(T) == typeof(PlanSetParameterValueRequest))
        {
            ContractValidator.ValidatePlanSetParameterValueFields(root);
            return;
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Bridge request must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new JsonException("Bridge request contains a duplicate field.");
        }
    }

    private static void EnsureWithinContractLimit(int utf8Length)
    {
        if (utf8Length > ContractLimits.MaximumContractBytes)
        {
            throw new JsonException("Contract JSON exceeds the configured byte limit.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(McpJsonContext.Default.Options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.Converters.Insert(0, new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
