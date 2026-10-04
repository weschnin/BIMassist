using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class DocumentParameterSearchTests
{
    private static readonly StablePaginator Paginator = new(new ReadCursorCodec(SHA256.HashData("document parameter search tests"u8)));

    [Fact]
    public void Search_returns_exact_total_and_disjoint_stable_pages()
    {
        var service = new DocumentParameterSearchService(Paginator);
        DocumentParameterMatch[] matches = [Match("z", "uid-1", "built-in:-1"), Match("A", "uid-2", "built-in:-2"), Match("a", "uid-1", "built-in:-3")];
        var request = Request("a", 1);
        PageResult<DocumentParameterMatch> first = service.Search(matches, request, "document-1", "revision-1");
        PageResult<DocumentParameterMatch> second = service.Search(matches, request with { Page = request.Page with { Cursor = first.NextCursor } }, "document-1", "revision-1");
        Assert.Equal(2, first.TotalCount);
        Assert.NotEqual(Assert.Single(first.Items).Parameter.StableId, Assert.Single(second.Items).Parameter.StableId);
        Assert.Equal(new[] { "built-in:-2", "built-in:-3" },
            first.Items.Concat(second.Items).Select(item => item.Parameter.StableId).Order(StringComparer.Ordinal));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public void Cursor_is_bound_to_query_and_revision()
    {
        var service = new DocumentParameterSearchService(Paginator);
        DocumentParameterMatch[] matches = [Match("A", new string('x', 120), "built-in:-1"), Match("Ab", "uid-2", "built-in:-2")];
        PageResult<DocumentParameterMatch> page = service.Search(matches, Request("a", 1), "document-1", "revision-1");
        Assert.NotNull(page.NextCursor);
        Assert.True(page.NextCursor!.Length <= 1024);
        Assert.Equal(BridgeErrorCodes.InvalidCursor, Assert.Throws<ReadCursorException>(() =>
            service.Search(matches, Request("b", 1) with { Page = new PageRequest { PageSize = 1, Cursor = page.NextCursor } }, "document-1", "revision-1")).ErrorCode);
        Assert.Equal(BridgeErrorCodes.DocumentChanged, Assert.Throws<ReadCursorException>(() =>
            service.Search(matches, Request("a", 1) with { Page = new PageRequest { PageSize = 1, Cursor = page.NextCursor } }, "document-1", "revision-2")).ErrorCode);
    }

    [Fact]
    public void Cursor_rejects_changed_category_type_option_or_document()
    {
        var service = new DocumentParameterSearchService(Paginator);
        DocumentParameterMatch[] matches = [Match("A", "uid-1", "built-in:-1"), Match("A", "uid-2", "built-in:-2")];
        var request = Request("A", 1);
        string cursor = service.Search(matches, request, "document-1", "revision-1").NextCursor!;
        var next = request with { Page = request.Page with { Cursor = cursor } };
        foreach (var changed in new[]
        {
            next with { CategoryId = "revit-category:-2000014" },
            next with { IncludeTypes = true }
        })
            Assert.Equal(BridgeErrorCodes.InvalidCursor, Assert.Throws<ReadCursorException>(() =>
                service.Search(matches, changed, "document-1", "revision-1")).ErrorCode);
        Assert.Equal(BridgeErrorCodes.InvalidCursor, Assert.Throws<ReadCursorException>(() =>
            service.Search(matches, next, "document-2", "revision-1")).ErrorCode);
    }

    [Theory]
    [InlineData(5000, 0, 0)]
    [InlineData(0, 50000, 0)]
    [InlineData(0, 0, 5000)]
    public void Scan_budget_fails_before_exposing_partial_results(int candidates, int inspections, int matches)
    {
        var budget = new DocumentParameterScanBudget();
        for (int i = 0; i < candidates; i++) budget.CountCandidate();
        for (int i = 0; i < inspections; i++) budget.CountInspection();
        for (int i = 0; i < matches; i++) budget.CountMatch();
        Action overflow = candidates != 0 ? budget.CountCandidate : inspections != 0 ? budget.CountInspection : budget.CountMatch;
        Assert.Equal(BridgeErrorCodes.LimitExceeded, Assert.Throws<ReadCursorException>(overflow).ErrorCode);
    }

    [Fact]
    public void Search_rejects_matching_identity_that_cannot_be_represented_in_contract()
    {
        Assert.Equal(BridgeErrorCodes.OperationNotSupported,
            Assert.Throws<ReadCursorException>(() => DocumentParameterSearchService.RequireSearchIdentity(null)).ErrorCode);
        Assert.Equal(BridgeErrorCodes.OperationNotSupported,
            Assert.Throws<ReadCursorException>(() => DocumentParameterSearchService.RequireSearchIdentity(
                Match("Fire", "uid", "built-in:-1").Parameter with
                {
                    Kind = ParameterIdentityKind.ParameterElement, BuiltInId = null,
                    DefinitionId = 42, OwnerContext = "document:doc-1", DataTypeId = null
                })).ErrorCode);
        Assert.Equal(BridgeErrorCodes.OperationNotSupported,
            Assert.Throws<ReadCursorException>(() => DocumentParameterSearchService.RequireSearchIdentity(
                Match("Fire", "uid", "built-in:-1").Parameter with { StableId = "" })).ErrorCode);
    }

    [Fact]
    public void Scan_budget_rejects_elapsed_deadline_and_cancellation()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        var budget = new DocumentParameterScanBudget(() => elapsed, TimeSpan.FromSeconds(2));
        budget.CountCandidate();
        elapsed = TimeSpan.FromSeconds(3);
        Assert.Equal(BridgeErrorCodes.LimitExceeded,
            Assert.Throws<ReadCursorException>(budget.CountInspection).ErrorCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledBudget = new DocumentParameterScanBudget(() => TimeSpan.Zero,
            TimeSpan.FromSeconds(2), cancelled.Token);
        Assert.Equal(BridgeErrorCodes.LimitExceeded,
            Assert.Throws<ReadCursorException>(cancelledBudget.CountCandidate).ErrorCode);
    }

    [Fact]
    public void Search_rejects_cancellation_after_scan_during_filtering()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new DocumentParameterScanBudget(() => TimeSpan.Zero, TimeSpan.FromSeconds(5), cancellation.Token);
        var service = new DocumentParameterSearchService(Paginator);
        DocumentParameterMatch[] matches = Enumerable.Range(0, 100).Select(i =>
            Match("Fire", $"uid-{i}", $"built-in:{-i - 1}")).ToArray();
        cancellation.Cancel();
        Assert.Equal(BridgeErrorCodes.LimitExceeded, Assert.Throws<ReadCursorException>(() =>
            service.Search(matches, Request("Fire", 1), "document-1", "revision-1", budget)).ErrorCode);
    }

    [Fact]
    public void Search_rejects_elapsed_budget_during_sort_or_pagination()
    {
        int checks = 0;
        var budget = new DocumentParameterScanBudget(() => TimeSpan.FromMilliseconds(++checks),
            TimeSpan.FromMilliseconds(8));
        var service = new DocumentParameterSearchService(Paginator);
        DocumentParameterMatch[] matches = Enumerable.Range(0, 10).Select(i =>
            Match("Fire", $"uid-{i}", $"built-in:{-i - 1}")).ToArray();
        Assert.Equal(BridgeErrorCodes.LimitExceeded, Assert.Throws<ReadCursorException>(() =>
            service.Search(matches, Request("Fire", 1), "document-1", "revision-1", budget)).ErrorCode);
    }

    [Theory]
    [InlineData(-1, "uid")]
    [InlineData(42, null)]
    [InlineData(42, "")]
    [InlineData(42, " ")]
    public void Matching_unrepresentable_target_fails_instead_of_being_omitted(long id, string? uid)
    {
        Assert.Equal(BridgeErrorCodes.OperationNotSupported, Assert.Throws<ReadCursorException>(() =>
            RevitDocumentParameterSearchSource.RequireSearchTarget(id, uid, ParameterTargetKind.Element)).ErrorCode);
    }

    [Fact]
    public void Matching_target_with_oversized_unique_id_fails_structured()
    {
        Assert.Equal(BridgeErrorCodes.OperationNotSupported, Assert.Throws<ReadCursorException>(() =>
            RevitDocumentParameterSearchSource.RequireSearchTarget(42,
                new string('x', ContractLimits.MaximumIdentifierLength + 1), ParameterTargetKind.Element)).ErrorCode);
    }

    [Fact]
    public void Matching_target_with_element_id_zero_is_preserved()
    {
        ParameterTarget target = RevitDocumentParameterSearchSource.RequireSearchTarget(0, "uid-0", ParameterTargetKind.Element);
        Assert.Equal(0, target.ElementId);
        var match = Match("Fire", "uid-0", "built-in:-1") with { Target = target };
        PageResult<DocumentParameterMatch> page = new DocumentParameterSearchService(Paginator)
            .Search([match], Request("Fire", 1), "document-1", "revision-1");
        Assert.Equal(0, Assert.Single(page.Items).Target.ElementId);
    }

    private static SearchDocumentParametersRequest Request(string name, int pageSize) => new()
    {
        Page = new PageRequest { PageSize = pageSize },
        NameContains = name,
        IncludeTypes = false
    };

    private static DocumentParameterMatch Match(string name, string uid, string stableId) => new()
    {
        Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = uid, ElementId = 7 },
        Parameter = new ParameterIdentity
        {
            Kind = ParameterIdentityKind.BuiltIn,
            BuiltInId = long.Parse(stableId.AsSpan("built-in:".Length), System.Globalization.CultureInfo.InvariantCulture),
            StableId = stableId,
            Name = name,
            IsInstance = true
        },
        StorageType = ParameterStorageType.String,
        IsReadOnly = false
    };
}
