using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Changes;

public static class ChangePlanHasher
{

    public static string ComputeHash(ChangePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var payload = new ChangePlanHashPayload(
            plan.PlanId,
            plan.SessionId,
            plan.DocumentKey,
            plan.ExpectedRevision,
            plan.CreatedAtUtc.ToUniversalTime(),
            plan.ExpiresAtUtc.ToUniversalTime(),
            plan.Operations,
            plan.Warnings);

        using JsonDocument document = SerializeHashPayload(payload);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, document.RootElement);
        }

        byte[] hash = SHA256.HashData(buffer.WrittenSpan);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static byte[] SerializeCanonical(ChangePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var payload = new ChangePlanHashPayload(
            plan.PlanId,
            plan.SessionId,
            plan.DocumentKey,
            plan.ExpectedRevision,
            plan.CreatedAtUtc.ToUniversalTime(),
            plan.ExpiresAtUtc.ToUniversalTime(),
            plan.Operations,
            plan.Warnings);
        using JsonDocument document = SerializeHashPayload(payload);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported value kind {element.ValueKind} in a change plan.");
        }
    }

    private static JsonDocument SerializeHashPayload(ChangePlanHashPayload payload) =>
        JsonSerializer.SerializeToDocument(payload, ContractJson.Options);

    internal sealed record ChangePlanHashPayload(
        string PlanId,
        string SessionId,
        string DocumentKey,
        string ExpectedRevision,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<ChangeOperation> Operations,
        IReadOnlyList<Protocol.BridgeWarning> Warnings);
}
