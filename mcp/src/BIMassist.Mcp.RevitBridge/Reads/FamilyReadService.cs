using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record FamilyReadRecord(
    FamilySummary Family,
    IReadOnlyList<FamilyTypeSummary> Types);

internal sealed class FamilyReadService
{
    private readonly StablePaginator _paginator;

    internal FamilyReadService(StablePaginator paginator)
    {
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
    }

    internal PageResult<FamilySummary> ListFamilies(
        IReadOnlyList<FamilyReadRecord> records,
        ListFamiliesRequest request,
        string documentKey,
        string documentRevision)
    {
        ArgumentNullException.ThrowIfNull(records);
        ContractValidator.Validate(request);

        string? filter = request.NameContains;
        FamilySummary[] families = records
            .Where(record => record is not null && record.Family is not null)
            .Where(record => request.IncludeInPlace || !record.Family.IsInPlace)
            .Where(record => filter is null || record.Family.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(record => record.Family with { TypeCount = record.Types?.Count ?? 0 })
            .OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(family => family.Name, StringComparer.Ordinal)
            .ThenBy(family => family.UniqueId, StringComparer.Ordinal)
            .ToArray();

        string queryHash = HashQuery(
            BridgeOperations.ListFamilies,
            request.IncludeInPlace ? "1" : "0",
            filter?.ToUpperInvariant() ?? string.Empty);
        return _paginator.Page(
            families,
            request.Page,
            FamilySortKey,
            BridgeOperations.ListFamilies,
            documentKey,
            documentRevision,
            queryHash);
    }

    internal FamilyMetadata GetFamilyMetadata(
        IReadOnlyList<FamilyReadRecord> records,
        GetFamilyMetadataRequest request,
        string documentKey,
        string documentRevision)
    {
        ArgumentNullException.ThrowIfNull(records);
        ContractValidator.Validate(request);

        FamilyReadRecord? record = records.SingleOrDefault(candidate =>
            candidate is not null && candidate.Family is not null &&
            string.Equals(candidate.Family.UniqueId, request.FamilyUniqueId, StringComparison.Ordinal));
        if (record is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.TargetNotFound);
        }

        FamilyTypeSummary[] types = (record.Types ?? [])
            .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(type => type.Name, StringComparer.Ordinal)
            .ThenBy(type => type.UniqueId, StringComparer.Ordinal)
            .ToArray();
        string queryHash = HashQuery(BridgeOperations.GetFamilyMetadata, request.FamilyUniqueId);
        PageResult<FamilyTypeSummary> page = _paginator.Page(
            types,
            request.Page,
            FamilyTypeSortKey,
            BridgeOperations.GetFamilyMetadata,
            documentKey,
            documentRevision,
            queryHash);
        var metadata = new FamilyMetadata
        {
            Family = record.Family with { TypeCount = types.Length },
            Types = page
        };
        ContractValidator.Validate(metadata);
        return metadata;
    }

    private static string FamilySortKey(FamilySummary family) =>
        $"{family.Name.ToUpperInvariant()}\0{family.Name}\0{family.UniqueId}";

    private static string FamilyTypeSortKey(FamilyTypeSummary type) =>
        $"{type.Name.ToUpperInvariant()}\0{type.Name}\0{type.UniqueId}";

    private static string HashQuery(params string[] parts)
    {
        string canonical = string.Join('\0', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
