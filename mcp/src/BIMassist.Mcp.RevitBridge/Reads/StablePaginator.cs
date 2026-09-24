using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed class StablePaginator
{
    private readonly ReadCursorCodec _cursorCodec;

    internal StablePaginator(ReadCursorCodec cursorCodec)
    {
        _cursorCodec = cursorCodec ?? throw new ArgumentNullException(nameof(cursorCodec));
    }

    internal PageResult<T> Page<T>(
        IReadOnlyList<T> sortedItems,
        PageRequest request,
        Func<T, string> stableKey,
        string operation,
        string documentKey,
        string documentRevision,
        string queryHash)
    {
        ArgumentNullException.ThrowIfNull(sortedItems);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stableKey);
        ContractValidator.Validate(request);

        string? previousKey = null;
        for (int index = 0; index < sortedItems.Count; index++)
        {
            if (sortedItems[index] is null)
            {
                throw InvalidRequest();
            }

            string key = stableKey(sortedItems[index]);
            if (string.IsNullOrWhiteSpace(key) || key.Length > ContractLimits.MaximumCursorLength ||
                (previousKey is not null && StringComparer.Ordinal.Compare(previousKey, key) >= 0))
            {
                throw InvalidRequest();
            }
            previousKey = key;
        }

        var expected = new ReadCursorContext(
            operation,
            documentKey,
            documentRevision,
            queryHash,
            0,
            string.Empty);
        int offset = 0;
        if (request.Cursor is not null)
        {
            ReadCursorContext cursor = _cursorCodec.Decode(request.Cursor, expected);
            offset = cursor.Offset;
            if (offset > sortedItems.Count ||
                (offset > 0 && !string.Equals(stableKey(sortedItems[offset - 1]), cursor.LastKey, StringComparison.Ordinal)))
            {
                throw new ReadCursorException(BridgeErrorCodes.DocumentChanged);
            }
        }

        int count = Math.Min(request.PageSize, sortedItems.Count - offset);
        T[] pageItems = new T[count];
        for (int index = 0; index < count; index++)
        {
            pageItems[index] = sortedItems[offset + index];
        }

        int nextOffset = offset + count;
        string? nextCursor = null;
        if (nextOffset < sortedItems.Count)
        {
            nextCursor = _cursorCodec.Encode(expected with
            {
                Offset = nextOffset,
                LastKey = stableKey(sortedItems[nextOffset - 1])
            });
        }

        var result = new PageResult<T>
        {
            Items = pageItems,
            NextCursor = nextCursor,
            TotalCount = sortedItems.Count
        };
        ContractValidator.Validate(result);
        return result;
    }

    private static ReadCursorException InvalidRequest() => new(BridgeErrorCodes.InvalidRequest);
}
