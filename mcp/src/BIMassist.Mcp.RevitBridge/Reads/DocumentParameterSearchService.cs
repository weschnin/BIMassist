using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class DocumentParameterScanBudget
{
    internal static readonly TimeSpan MaximumElapsed = TimeSpan.FromSeconds(5);
    private readonly Func<TimeSpan> _elapsed;
    private readonly TimeSpan _maximumElapsed;
    private readonly CancellationToken _cancellationToken;
    private int _candidates;
    private int _inspections;
    private int _matches;

    internal DocumentParameterScanBudget(CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        _elapsed = () => Stopwatch.GetElapsedTime(started);
        _maximumElapsed = MaximumElapsed;
        _cancellationToken = cancellationToken;
    }

    internal DocumentParameterScanBudget(Func<TimeSpan> elapsed, TimeSpan maximumElapsed,
        CancellationToken cancellationToken = default)
    {
        _elapsed = elapsed ?? throw new ArgumentNullException(nameof(elapsed));
        _maximumElapsed = maximumElapsed > TimeSpan.Zero ? maximumElapsed
            : throw new ArgumentOutOfRangeException(nameof(maximumElapsed));
        _cancellationToken = cancellationToken;
    }

    internal void CountCandidate() { CheckTime(); Check(++_candidates, 5_000); }
    internal void CountInspection() { CheckTime(); Check(++_inspections, 50_000); }
    internal void CountMatch() { CheckTime(); Check(++_matches, 5_000); }
    internal void CheckTime()
    {
        if (_cancellationToken.IsCancellationRequested || _elapsed() >= _maximumElapsed)
            throw new ReadCursorException(BridgeErrorCodes.LimitExceeded);
    }

    private static void Check(int count, int maximum)
    {
        if (count > maximum) throw new ReadCursorException(BridgeErrorCodes.LimitExceeded);
    }
}

internal sealed class DocumentParameterSearchService
{
    internal static ParameterIdentity RequireSearchIdentity(ParameterIdentity? identity)
    {
        if (identity is null || identity.Kind == ParameterIdentityKind.ParameterElement &&
            string.IsNullOrWhiteSpace(identity.DataTypeId))
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        try
        {
            ContractValidator.Validate(identity);
        }
        catch (JsonException)
        {
            throw new ReadCursorException(BridgeErrorCodes.OperationNotSupported);
        }
        return identity;
    }
    private readonly StablePaginator _paginator;

    internal DocumentParameterSearchService(StablePaginator paginator) =>
        _paginator = paginator ?? throw new ArgumentNullException(nameof(paginator));

    internal PageResult<DocumentParameterMatch> Search(
        IReadOnlyList<DocumentParameterMatch> matches,
        SearchDocumentParametersRequest request,
        string documentKey,
        string revision,
        DocumentParameterScanBudget? budget = null)
    {
        budget ??= new DocumentParameterScanBudget();
        budget.CheckTime();
        ArgumentNullException.ThrowIfNull(matches);
        ContractValidator.Validate(request);
        budget.CheckTime();
        var filtered = new List<DocumentParameterMatch>(matches.Count);
        foreach (DocumentParameterMatch match in matches)
        {
            budget.CheckTime();
            ContractValidator.Validate(match);
            if (match.Parameter.Name.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase))
                filtered.Add(match);
        }
        // Check during key materialization and comparisons, not only before and after sorting.
        DocumentParameterMatch[] ordered = filtered.OrderBy(match =>
        {
            budget.CheckTime();
            return Key(match);
        }, Comparer<string>.Create((left, right) =>
        {
            budget.CheckTime();
            return StringComparer.Ordinal.Compare(left, right);
        })).ToArray();
        budget.CheckTime();
        PageResult<DocumentParameterMatch> page = _paginator.Page(ordered, request.Page, match =>
        {
            budget.CheckTime();
            return Key(match);
        }, BridgeOperations.SearchDocumentParameters, documentKey, revision,
            Hash($"{BridgeOperations.SearchDocumentParameters}\0{request.NameContains.ToUpperInvariant()}\0{request.CategoryId ?? string.Empty}\0{request.IncludeTypes}"));
        budget.CheckTime();
        return page;
    }

    private static string Key(DocumentParameterMatch match) => Hash(
        $"{(int)match.Target.Kind}\0{match.Target.UniqueId}\0{match.Target.ElementId}\0{match.Parameter.StableId}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
