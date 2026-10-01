using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class ElementReadService
{
    private readonly StablePaginator _paginator;

    internal ElementReadService(StablePaginator paginator)
    {
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
    }

    internal PageResult<ElementSummary> ListElements(
        IReadOnlyList<ElementSummary> elements,
        ListElementsRequest request,
        string documentKey,
        string documentRevision)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ContractValidator.Validate(request);

        foreach (ElementSummary element in elements)
        {
            ContractValidator.Validate(element);
        }

        if (elements.Select(element => element.UniqueId).Distinct(StringComparer.Ordinal).Count() != elements.Count ||
            elements.Select(element => element.ElementId).Distinct().Count() != elements.Count)
        {
            throw new ReadCursorException(BridgeErrorCodes.InvalidRequest);
        }

        string? nameFilter = request.NameContains;
        ElementSummary[] matches = elements
            .Where(element => request.IncludeTypes || !element.IsElementType)
            .Where(element => request.CategoryId is null ||
                              string.Equals(element.CategoryId, request.CategoryId, StringComparison.Ordinal))
            .Where(element => nameFilter is null ||
                              element.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(element => element.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(element => element.Name, StringComparer.Ordinal)
            .ThenBy(element => element.UniqueId, StringComparer.Ordinal)
            .ToArray();

        string queryHash = HashQuery(
            BridgeOperations.ListElements,
            request.IncludeTypes ? "1" : "0",
            request.CategoryId ?? string.Empty,
            nameFilter?.ToUpperInvariant() ?? string.Empty);
        return _paginator.Page(
            matches,
            request.Page,
            ElementSortKey,
            BridgeOperations.ListElements,
            documentKey,
            documentRevision,
            queryHash);
    }

    private static string ElementSortKey(ElementSummary element) =>
        $"{element.Name.ToUpperInvariant()}\0{element.Name}\0{element.UniqueId}";

    private static string HashQuery(params string[] parts)
    {
        string canonical = string.Join('\0', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
