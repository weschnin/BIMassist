using System.Text.Json;
using BIMassist.Mcp.Contracts.Reads;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BIMassist.Mcp.Server.Tools;

public static class ToolArgumentValidator
{
    public static void Validate(
        RequestContext<CallToolRequestParams> requestContext,
        IReadOnlyCollection<string> allowedKeys,
        IReadOnlyCollection<string> requiredKeys)
    {
        ArgumentNullException.ThrowIfNull(requestContext);
        Validate(requestContext.Params.Arguments, allowedKeys, requiredKeys);
    }

    public static void Validate(
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyCollection<string> allowedKeys,
        IReadOnlyCollection<string> requiredKeys)
    {
        ArgumentNullException.ThrowIfNull(allowedKeys);
        ArgumentNullException.ThrowIfNull(requiredKeys);

        if (arguments is null)
        {
            arguments = new Dictionary<string, JsonElement>();
        }

        bool hasUnexpectedKey = arguments.Keys.Any(key => !allowedKeys.Contains(key, StringComparer.Ordinal));
        bool hasMissingRequiredKey = requiredKeys.Any(key => !arguments.ContainsKey(key));
        bool hasInvalidProcessId = arguments.TryGetValue("revitProcessId", out JsonElement processId)
            && (processId.ValueKind != JsonValueKind.Number
                || !processId.TryGetInt32(out int processIdValue)
                || processIdValue <= 0);
        if (hasUnexpectedKey || hasMissingRequiredKey || hasInvalidProcessId)
        {
            throw new ArgumentException("The tool request contains missing, unsupported, or invalid arguments.");
        }
    }
}
