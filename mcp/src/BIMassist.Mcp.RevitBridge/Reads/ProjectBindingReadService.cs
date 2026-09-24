using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class ProjectBindingReadService
{
    private readonly StablePaginator _paginator;

    internal ProjectBindingReadService(StablePaginator paginator)
    {
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
    }

    internal PageResult<ProjectBindingDescriptor> ListBindings(
        IReadOnlyList<ProjectBindingDescriptor> bindings,
        ListProjectBindingsRequest request,
        string documentKey,
        string revision)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ContractValidator.Validate(request);
        List<ProjectBindingDescriptor> ordered = bindings
            .Where(binding => string.IsNullOrWhiteSpace(request.NameContains) ||
                binding.Definition.Name.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase))
            .Where(binding => request.BindingKind is null || binding.Binding.Kind == request.BindingKind)
            .Where(binding => string.IsNullOrWhiteSpace(request.CategoryId) ||
                binding.Binding.CategoryIds.Contains(request.CategoryId, StringComparer.Ordinal))
            .Where(binding => request.IsShared is null ||
                (binding.Identity.Kind == ParameterIdentityKind.SharedGuid) == request.IsShared)
            .OrderBy(binding => SortKey(binding), StringComparer.Ordinal)
            .ToList();
        string queryHash = Hash(
            BridgeOperations.ListProjectBindings,
            request.NameContains?.ToUpperInvariant() ?? string.Empty,
            request.BindingKind?.ToString() ?? string.Empty,
            request.CategoryId ?? string.Empty,
            request.IsShared?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        PageResult<ProjectBindingDescriptor> result = _paginator.Page(
            ordered,
            request.Page,
            SortKey,
            BridgeOperations.ListProjectBindings,
            documentKey,
            revision,
            queryHash);
        ContractValidator.Validate(result);
        return result;
    }

    private static string SortKey(ProjectBindingDescriptor binding) => string.Join(
        '\0',
        binding.Definition.Name.ToUpperInvariant(),
        binding.Definition.Name,
        binding.Identity.StableId);

    private static string Hash(params string[] parts)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\0', parts));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
