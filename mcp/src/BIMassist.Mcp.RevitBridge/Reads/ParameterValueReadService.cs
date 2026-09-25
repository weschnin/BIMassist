using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class ParameterValueReadService
{
    private readonly StablePaginator _paginator;

    internal ParameterValueReadService(StablePaginator paginator)
    {
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
    }

    internal PageResult<ParameterValueEntry> GetValues(
        IReadOnlyList<ParameterReadRecord> records,
        GetParameterValuesRequest request,
        string documentKey,
        string revision)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(request);
        ContractValidator.Validate(request);

        HashSet<string>? selectors = request.Parameters is null
            ? null
            : request.Parameters.Select(parameter => parameter.StableId).ToHashSet(StringComparer.Ordinal);
        ParameterValueEntry[] values = records
            .Where(record => selectors is null || selectors.Contains(record.Summary.Identity.StableId))
            .Select(record => new ParameterValueEntry
            {
                Target = request.Target,
                Parameter = record.Summary.Identity,
                Value = record.Value ?? throw new ReadCursorException(BridgeErrorCodes.BridgeInternalError)
            })
            .OrderBy(entry => entry.Parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Parameter.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Parameter.StableId, StringComparer.Ordinal)
            .ToArray();

        return _paginator.Page(
            values,
            request.Page,
            SortKey,
            BridgeOperations.GetParameterValues,
            documentKey,
            revision,
            ComputeQueryHash(request));
    }

    private static string SortKey(ParameterValueEntry entry) =>
        $"{entry.Parameter.Name.ToUpperInvariant()}\u001f{entry.Parameter.Name}\u001f{entry.Parameter.StableId}";

    private static string ComputeQueryHash(GetParameterValuesRequest request)
    {
        string selectors = request.Parameters is null
            ? "*"
            : string.Join(",", request.Parameters.Select(parameter => parameter.StableId).OrderBy(value => value, StringComparer.Ordinal));
        string input = string.Join(
            "\u001f",
            ((int)request.Target.Kind).ToString(CultureInfo.InvariantCulture),
            request.Target.UniqueId ?? string.Empty,
            request.Target.ElementId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.Target.FamilyName ?? string.Empty,
            request.Target.TypeName ?? string.Empty,
            selectors);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
}
