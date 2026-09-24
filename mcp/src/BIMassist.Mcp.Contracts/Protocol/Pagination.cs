using System.Text.Json.Serialization;

namespace BIMassist.Mcp.Contracts.Protocol;

public sealed record PageRequest
{
    [JsonRequired]
    public required int PageSize { get; init; }

    public string? Cursor { get; init; }
}

public interface IPageResult
{
    int ItemCount { get; }

    long? TotalCount { get; }

    string? NextCursorForValidation { get; }

    IEnumerable<object?> ItemsForValidation { get; }
}

public sealed record PageResult<T> : IPageResult
{
    [JsonRequired]
    public required IReadOnlyList<T> Items { get; init; }

    public string? NextCursor { get; init; }

    public long? TotalCount { get; init; }

    int IPageResult.ItemCount => Items?.Count ?? -1;

    string? IPageResult.NextCursorForValidation => NextCursor;

    IEnumerable<object?> IPageResult.ItemsForValidation =>
        Items?.Cast<object?>() ?? Enumerable.Empty<object?>();
}
