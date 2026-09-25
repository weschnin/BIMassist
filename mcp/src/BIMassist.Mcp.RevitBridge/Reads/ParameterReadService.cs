using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record ParameterReadRecord(
    ParameterSummary Summary,
    ParameterMetadata Metadata,
    ParameterValue? Value = null);

internal sealed class ParameterReadService
{
    private readonly StablePaginator _paginator;

    internal ParameterReadService(StablePaginator paginator)
    {
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
    }

    internal PageResult<ParameterSummary> ListParameters(
        IReadOnlyList<ParameterReadRecord> records,
        ListParametersRequest request,
        string documentKey,
        string revision)
    {
        ArgumentNullException.ThrowIfNull(records);
        ContractValidator.Validate(request);
        ParameterSummary[] ordered = records
            .Where(record => record is not null && record.Summary is not null)
            .Select(record => record.Summary)
            .Where(summary => string.IsNullOrWhiteSpace(request.NameContains) ||
                summary.Identity.Name.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase))
            .Where(summary => request.StorageType is null || summary.StorageType == request.StorageType)
            .Where(summary => request.BindingKind is null || summary.BindingKind == request.BindingKind)
            .Where(summary => request.IsReadOnly is null || summary.IsReadOnly == request.IsReadOnly)
            .OrderBy(SortKey, StringComparer.Ordinal)
            .ToArray();
        string queryHash = Hash(
            BridgeOperations.ListParameters,
            TargetKey(request.Target),
            request.NameContains?.ToUpperInvariant() ?? string.Empty,
            request.StorageType?.ToString() ?? string.Empty,
            request.BindingKind?.ToString() ?? string.Empty,
            request.IsReadOnly?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        PageResult<ParameterSummary> result = _paginator.Page(
            ordered,
            request.Page,
            SortKey,
            BridgeOperations.ListParameters,
            documentKey,
            revision,
            queryHash);
        ContractValidator.Validate(result);
        return result;
    }

    internal ParameterMetadata GetParameterMetadata(
        IReadOnlyList<ParameterReadRecord> records,
        GetParameterMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(records);
        ContractValidator.Validate(request);
        ParameterReadRecord? record = records.SingleOrDefault(candidate =>
            candidate is not null &&
            candidate.Summary.Identity.Kind == request.Parameter.Kind &&
            string.Equals(
                candidate.Summary.Identity.StableId,
                request.Parameter.StableId,
                StringComparison.Ordinal));
        if (record is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.TargetNotFound);
        }

        if (record.Metadata.Context.Target != request.Target)
        {
            throw new ReadCursorException(BridgeErrorCodes.InvalidRequest);
        }

        ContractValidator.Validate(record.Metadata);
        return record.Metadata;
    }

    private static string SortKey(ParameterSummary summary) => string.Join(
        '\0',
        summary.Identity.Name.ToUpperInvariant(),
        summary.Identity.Name,
        summary.Identity.StableId);

    private static string TargetKey(ParameterTarget target) => string.Join(
        '\0',
        target.Kind.ToString(),
        target.UniqueId ?? string.Empty,
        target.ElementId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        target.FamilyName ?? string.Empty,
        target.TypeName ?? string.Empty);

    private static string Hash(params string[] parts)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\0', parts));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
