using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal static class ExactDocumentResolver
{
    internal static T Resolve<T>(
        IEnumerable<T> candidates,
        string documentKey,
        Func<T, string> keySelector)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        ArgumentNullException.ThrowIfNull(keySelector);

        using IEnumerator<T> matches = candidates
            .Where(candidate => candidate is not null &&
                string.Equals(keySelector(candidate), documentKey, StringComparison.Ordinal))
            .Take(2)
            .GetEnumerator();
        if (!matches.MoveNext())
        {
            throw new ReadCursorException(BridgeErrorCodes.DocumentNotFound);
        }

        T result = matches.Current;
        if (matches.MoveNext())
        {
            throw new ReadCursorException(BridgeErrorCodes.AmbiguousTarget);
        }

        return result;
    }
}
