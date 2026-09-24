using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class StablePaginatorTests
{
    private static readonly byte[] Key = SHA256.HashData("BIMassist Phase 3 paginator tests"u8);
    private static readonly string QueryHash = Convert.ToHexString(SHA256.HashData("family.list|door"u8)).ToLowerInvariant();

    [Fact]
    public void Pages_sorted_items_without_duplicates_or_terminal_empty_cursor()
    {
        var paginator = new StablePaginator(new ReadCursorCodec(Key));
        Item[] items = [new("a"), new("b"), new("c")];
        var firstRequest = new PageRequest { PageSize = 2 };

        PageResult<Item> first = paginator.Page(
            items,
            firstRequest,
            item => item.Key,
            BridgeOperations.ListFamilies,
            "document-1",
            "revision-1",
            QueryHash);
        PageResult<Item> second = paginator.Page(
            items,
            new PageRequest { PageSize = 2, Cursor = first.NextCursor },
            item => item.Key,
            BridgeOperations.ListFamilies,
            "document-1",
            "revision-1",
            QueryHash);

        Assert.Equal(["a", "b"], first.Items.Select(item => item.Key));
        Assert.NotNull(first.NextCursor);
        Assert.Equal(["c"], second.Items.Select(item => item.Key));
        Assert.Null(second.NextCursor);
        Assert.Equal(3, second.TotalCount);
    }

    [Fact]
    public void Rejects_unsorted_or_duplicate_stable_keys()
    {
        var paginator = new StablePaginator(new ReadCursorCodec(Key));
        Item[] items = [new("b"), new("a"), new("a")];

        ReadCursorException error = Assert.Throws<ReadCursorException>(() => paginator.Page(
            items,
            new PageRequest { PageSize = 2 },
            item => item.Key,
            BridgeOperations.ListFamilies,
            "document-1",
            "revision-1",
            QueryHash));

        Assert.Equal(BridgeErrorCodes.InvalidRequest, error.ErrorCode);
    }

    private sealed record Item(string Key);
}
