using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class SharedDefinitionReadService
{
    private readonly StablePaginator _paginator;

    internal SharedDefinitionReadService(StablePaginator paginator)
    {
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
    }

    internal PageResult<SharedDefinitionDescriptor> ListDefinitions(
        IReadOnlyList<SharedDefinitionDescriptor> definitions,
        ListSharedDefinitionsRequest request,
        string documentKey,
        string revision)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ContractValidator.Validate(request);
        List<SharedDefinitionDescriptor> ordered = definitions
            .Where(definition => request.SharedGuid is null || definition.SharedGuid == request.SharedGuid)
            .Where(definition => string.IsNullOrWhiteSpace(request.GroupName) ||
                string.Equals(definition.GroupName, request.GroupName, StringComparison.OrdinalIgnoreCase))
            .Where(definition => string.IsNullOrWhiteSpace(request.NameContains) ||
                definition.Name.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(definition => SortKey(definition), StringComparer.Ordinal)
            .ToList();
        string queryHash = QueryHash(request);
        PageResult<SharedDefinitionDescriptor> result = _paginator.Page(
            ordered,
            request.Page,
            SortKey,
            BridgeOperations.ListSharedDefinitions,
            documentKey,
            revision,
            queryHash);
        ContractValidator.Validate(result);
        return result;
    }

    private static string QueryHash(ListSharedDefinitionsRequest request) => Hash(
        BridgeOperations.ListSharedDefinitions,
        request.NameContains?.ToUpperInvariant() ?? string.Empty,
        request.GroupName?.ToUpperInvariant() ?? string.Empty,
        request.SharedGuid?.ToString("D") ?? string.Empty);

    private static string SortKey(SharedDefinitionDescriptor definition) => string.Join(
        '\0',
        definition.GroupName.ToUpperInvariant(),
        definition.GroupName,
        definition.Name.ToUpperInvariant(),
        definition.Name,
        definition.SharedGuid.ToString("D"));

    private static string Hash(params string[] parts)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\0', parts));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
