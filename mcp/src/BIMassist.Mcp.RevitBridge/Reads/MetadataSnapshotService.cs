using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record MetadataSnapshotRow(string StableKey, MetadataSnapshotItem Item);

internal sealed record PreparedMetadataSnapshotRow(
    string StableKey,
    MetadataSnapshotItem Item,
    int SerializedBytes);

internal sealed class MetadataSnapshotCollector
{
    private readonly MetadataSnapshotOptions _options;
    private readonly List<PreparedMetadataSnapshotRow> _rows = [];
    private int _serializedBytes;

    internal MetadataSnapshotCollector(MetadataSnapshotOptions options)
    {
        _options = options;
    }

    internal IReadOnlyList<PreparedMetadataSnapshotRow> Rows => _rows;

    internal void Add(string stableKey, MetadataSnapshotItem item)
    {
        if (string.IsNullOrWhiteSpace(stableKey) || stableKey.Length > 1_024)
        {
            throw new InvalidOperationException("Snapshot rows require a bounded stable identity.");
        }
        if (_rows.Count >= _options.MaximumItems)
        {
            throw new ReadCursorException(BridgeErrorCodes.LimitExceeded);
        }

        string json = ContractJson.Serialize(item);
        int serializedBytes = Encoding.UTF8.GetByteCount(json);
        _serializedBytes = checked(_serializedBytes + serializedBytes);
        if (_serializedBytes > _options.MaximumSnapshotBytes ||
            _serializedBytes > _options.MaximumTotalBytes)
        {
            throw new ReadCursorException(BridgeErrorCodes.LimitExceeded);
        }

        MetadataSnapshotItem immutableItem = ContractJson.Deserialize<MetadataSnapshotItem>(json);
        _rows.Add(new PreparedMetadataSnapshotRow(stableKey, immutableItem, serializedBytes));
    }
}

internal sealed record MetadataSnapshotOptions(
    int MaximumSnapshots,
    int MaximumItems,
    int MaximumSnapshotBytes,
    int MaximumTotalBytes,
    int MaximumPageItemBytes,
    TimeSpan TimeToLive)
{
    internal static MetadataSnapshotOptions Default { get; } = new(
        MaximumSnapshots: 8,
        MaximumItems: 10_000,
        MaximumSnapshotBytes: 16 * 1024 * 1024,
        MaximumTotalBytes: 32 * 1024 * 1024,
        MaximumPageItemBytes: 900 * 1024,
        TimeToLive: TimeSpan.FromMinutes(10));
}

internal sealed class MetadataSnapshotService
{
    private const int MaximumIdentityAttempts = 8;
    private const int MaximumTombstones = 64;
    private readonly object _gate = new();
    private readonly ReadCursorCodec _cursorCodec;
    private readonly TimeProvider _timeProvider;
    private readonly MetadataSnapshotOptions _options;
    private readonly Func<string> _snapshotIdFactory;
    private readonly Dictionary<string, SnapshotEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _expiredTombstones = new(StringComparer.Ordinal);
    private int _totalBytes;

    internal MetadataSnapshotService(
        ReadCursorCodec cursorCodec,
        TimeProvider? timeProvider = null,
        MetadataSnapshotOptions? options = null,
        Func<string>? snapshotIdFactory = null)
    {
        _cursorCodec = cursorCodec ?? throw new ArgumentNullException(nameof(cursorCodec));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? MetadataSnapshotOptions.Default;
        _snapshotIdFactory = snapshotIdFactory ?? CreateSnapshotId;
        ValidateOptions(_options);
    }

    internal MetadataSnapshotCollector CreateCollector() => new(_options);

    internal MetadataSnapshotPage Create(
        ExportMetadataSnapshotRequest request,
        string documentKey,
        string documentRevision,
        IReadOnlyList<MetadataSnapshotRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        MetadataSnapshotCollector collector = CreateCollector();
        foreach (MetadataSnapshotRow row in rows)
        {
            collector.Add(row.StableKey, row.Item);
        }

        return Create(request, documentKey, documentRevision, collector);
    }

    internal MetadataSnapshotPage Create(
        ExportMetadataSnapshotRequest request,
        string documentKey,
        string documentRevision,
        MetadataSnapshotCollector collector)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentRevision);
        ArgumentNullException.ThrowIfNull(collector);
        ContractValidator.Validate(request);
        if (request.Page.Cursor is not null)
        {
            throw new ReadCursorException(BridgeErrorCodes.InvalidCursor);
        }

        string selectionHash = ComputeSelectionHash(request.Selection);
        ImmutableRow[] immutableRows = PrepareRows(collector.Rows);
        DateTimeOffset createdAtUtc = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAtUtc = createdAtUtc.Add(_options.TimeToLive);
        long createdTimestamp = _timeProvider.GetTimestamp();
        long expiresTimestamp = AddDuration(createdTimestamp, _options.TimeToLive);

        SnapshotEntry entry;
        lock (_gate)
        {
            PurgeExpired(createdTimestamp);
            entry = CreateEntry(
                documentKey,
                documentRevision,
                selectionHash,
                immutableRows,
                createdAtUtc,
                expiresAtUtc,
                createdTimestamp,
                expiresTimestamp);
            Admit(entry);
        }

        return BuildPage(entry, request.Page, offset: 0, lastKey: string.Empty);
    }

    internal MetadataSnapshotPage Continue(ExportMetadataSnapshotRequest request, string documentKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        ContractValidator.Validate(request);
        if (request.Page.Cursor is null)
        {
            throw new ReadCursorException(BridgeErrorCodes.InvalidCursor);
        }

        VerifiedReadCursorContext cursor = _cursorCodec.DecodeVerified(request.Page.Cursor);
        if (!string.Equals(cursor.Operation, BridgeOperations.ExportMetadataSnapshot, StringComparison.Ordinal) ||
            !string.Equals(cursor.DocumentKey, documentKey, StringComparison.Ordinal))
        {
            throw new ReadCursorException(BridgeErrorCodes.InvalidCursor);
        }

        string selectionHash = ComputeSelectionHash(request.Selection);
        SnapshotEntry entry;
        long nowTimestamp = _timeProvider.GetTimestamp();
        lock (_gate)
        {
            if (!_entries.TryGetValue(cursor.QueryHash, out entry!))
            {
                if (IsExpiredTombstone(cursor.QueryHash, nowTimestamp))
                {
                    throw new ReadCursorException(BridgeErrorCodes.SnapshotExpired);
                }

                throw new ReadCursorException(BridgeErrorCodes.SnapshotNotFound);
            }

            if (!string.Equals(entry.SelectionHash, selectionHash, StringComparison.Ordinal) ||
                !string.Equals(entry.DocumentKey, cursor.DocumentKey, StringComparison.Ordinal) ||
                !string.Equals(entry.DocumentRevision, cursor.DocumentRevision, StringComparison.Ordinal) ||
                !string.Equals(entry.QueryHash, ComputeQueryHash(entry.SnapshotId, entry.SelectionHash), StringComparison.Ordinal))
            {
                throw new ReadCursorException(BridgeErrorCodes.InvalidCursor);
            }

            if (nowTimestamp >= entry.ExpiresTimestamp)
            {
                RemoveEntry(entry, rememberExpiry: true);
                throw new ReadCursorException(BridgeErrorCodes.SnapshotExpired);
            }
        }

        return BuildPage(entry, request.Page, cursor.Offset, cursor.LastKey);
    }

    internal void PurgeDocument(string documentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        lock (_gate)
        {
            foreach (SnapshotEntry entry in _entries.Values
                         .Where(candidate => string.Equals(candidate.DocumentKey, documentKey, StringComparison.Ordinal))
                         .ToArray())
            {
                RemoveEntry(entry, rememberExpiry: false);
            }
        }
    }

    private SnapshotEntry CreateEntry(
        string documentKey,
        string documentRevision,
        string selectionHash,
        ImmutableRow[] rows,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        long createdTimestamp,
        long expiresTimestamp)
    {
        for (int attempt = 0; attempt < MaximumIdentityAttempts; attempt++)
        {
            string snapshotId = _snapshotIdFactory();
            if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Length > ContractLimits.MaximumTextLength)
            {
                throw new InvalidOperationException("The snapshot identity generator returned an invalid identity.");
            }

            string queryHash = ComputeQueryHash(snapshotId, selectionHash);
            if (!_entries.ContainsKey(queryHash))
            {
                return new SnapshotEntry(
                    snapshotId,
                    queryHash,
                    selectionHash,
                    documentKey,
                    documentRevision,
                    createdAtUtc,
                    expiresAtUtc,
                    createdTimestamp,
                    expiresTimestamp,
                    rows,
                    rows.Sum(row => row.SerializedBytes));
            }
        }

        throw new InvalidOperationException("A unique snapshot identity could not be allocated.");
    }

    private static ImmutableRow[] PrepareRows(IReadOnlyList<PreparedMetadataSnapshotRow> rows)
    {
        ImmutableRow[] materialized = rows
            .OrderBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .Select(row => new ImmutableRow(row.StableKey, row.Item, row.SerializedBytes))
            .ToArray();
        for (int index = 1; index < materialized.Length; index++)
        {
            if (string.Equals(materialized[index - 1].StableKey, materialized[index].StableKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Snapshot row identities must be unique.");
            }
        }

        return materialized;
    }

    private MetadataSnapshotPage BuildPage(SnapshotEntry entry, PageRequest pageRequest, int offset, string lastKey)
    {
        if (offset < 0 || offset > entry.Rows.Length ||
            (offset == 0 && lastKey.Length != 0) ||
            (offset > 0 && (offset > entry.Rows.Length ||
                            !string.Equals(entry.Rows[offset - 1].StableKey, lastKey, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("The authenticated snapshot position is inconsistent with the cached entry.");
        }

        var items = new List<MetadataSnapshotItem>(Math.Min(pageRequest.PageSize, entry.Rows.Length - offset));
        int pageBytes = 0;
        int index = offset;
        while (index < entry.Rows.Length && items.Count < pageRequest.PageSize)
        {
            ImmutableRow row = entry.Rows[index];
            if (pageBytes + row.SerializedBytes > _options.MaximumPageItemBytes)
            {
                if (items.Count == 0)
                {
                    throw new ReadCursorException(BridgeErrorCodes.LimitExceeded);
                }

                break;
            }

            items.Add(row.Item);
            pageBytes += row.SerializedBytes;
            index++;
        }

        string? nextCursor = null;
        if (index < entry.Rows.Length)
        {
            nextCursor = _cursorCodec.Encode(new ReadCursorContext(
                BridgeOperations.ExportMetadataSnapshot,
                entry.DocumentKey,
                entry.DocumentRevision,
                entry.QueryHash,
                index,
                entry.Rows[index - 1].StableKey));
        }

        var result = new MetadataSnapshotPage
        {
            SnapshotId = entry.SnapshotId,
            DocumentKey = entry.DocumentKey,
            DocumentRevision = entry.DocumentRevision,
            CreatedAtUtc = entry.CreatedAtUtc,
            ExpiresAtUtc = entry.ExpiresAtUtc,
            Page = new PageResult<MetadataSnapshotItem>
            {
                Items = items,
                NextCursor = nextCursor,
                TotalCount = entry.Rows.Length
            }
        };
        ContractValidator.Validate(result);
        return result;
    }

    private void Admit(SnapshotEntry entry)
    {
        while (_entries.Count >= _options.MaximumSnapshots ||
               _totalBytes + entry.TotalBytes > _options.MaximumTotalBytes)
        {
            SnapshotEntry? oldest = _entries.Values
                .OrderBy(candidate => candidate.CreatedTimestamp)
                .FirstOrDefault();
            if (oldest is null)
            {
                throw new ReadCursorException(BridgeErrorCodes.LimitExceeded);
            }

            RemoveEntry(oldest, rememberExpiry: false);
        }

        _entries.Add(entry.QueryHash, entry);
        _totalBytes = checked(_totalBytes + entry.TotalBytes);
    }

    private void PurgeExpired(long nowTimestamp)
    {
        foreach (SnapshotEntry entry in _entries.Values
                     .Where(candidate => nowTimestamp >= candidate.ExpiresTimestamp)
                     .ToArray())
        {
            RemoveEntry(entry, rememberExpiry: true);
        }

        foreach (string queryHash in _expiredTombstones
                     .Where(pair => nowTimestamp >= pair.Value)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _expiredTombstones.Remove(queryHash);
        }
    }

    private bool IsExpiredTombstone(string queryHash, long nowTimestamp)
    {
        if (!_expiredTombstones.TryGetValue(queryHash, out long retentionDeadline))
        {
            return false;
        }

        if (nowTimestamp < retentionDeadline)
        {
            return true;
        }

        _expiredTombstones.Remove(queryHash);
        return false;
    }

    private void RemoveEntry(SnapshotEntry entry, bool rememberExpiry)
    {
        if (_entries.Remove(entry.QueryHash))
        {
            _totalBytes -= entry.TotalBytes;
        }

        if (!rememberExpiry)
        {
            return;
        }

        while (_expiredTombstones.Count >= MaximumTombstones)
        {
            string oldest = _expiredTombstones.OrderBy(pair => pair.Value).First().Key;
            _expiredTombstones.Remove(oldest);
        }

        _expiredTombstones[entry.QueryHash] = AddDuration(_timeProvider.GetTimestamp(), _options.TimeToLive);
    }

    private long AddDuration(long timestamp, TimeSpan duration)
    {
        double delta = duration.TotalSeconds * _timeProvider.TimestampFrequency;
        if (delta <= 0 || delta > long.MaxValue - timestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return checked(timestamp + (long)Math.Ceiling(delta));
    }

    private static string ComputeSelectionHash(MetadataSnapshotSelection selection)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(selection.IncludeFamilies);
            writer.Write(selection.IncludeSharedDefinitions);
            writer.Write(selection.IncludeProjectBindings);
            writer.Write(selection.IncludeParameterValues);
            string[] targets = selection.ParameterTargets
                .Select(CanonicalTargetIdentity)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            writer.Write(targets.Length);
            foreach (string target in targets)
            {
                writer.Write(target);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string CanonicalTargetIdentity(ParameterTarget target)
    {
        string uniqueId = target.UniqueId ?? string.Empty;
        string elementId = target.ElementId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)target.Kind}:{uniqueId.Length}:{uniqueId}:{elementId}");
    }

    private static string ComputeQueryHash(string snapshotId, string selectionHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("BIMassist.MetadataSnapshot.v1");
            writer.Write(snapshotId);
            writer.Write(selectionHash);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string CreateSnapshotId() => $"snapshot-{Guid.NewGuid():N}";

    private static void ValidateOptions(MetadataSnapshotOptions options)
    {
        if (options.MaximumSnapshots <= 0 || options.MaximumItems <= 0 ||
            options.MaximumSnapshotBytes <= 0 || options.MaximumTotalBytes < options.MaximumSnapshotBytes ||
            options.MaximumPageItemBytes <= 0 || options.MaximumPageItemBytes >= ContractLimits.MaximumContractBytes ||
            options.TimeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private sealed record ImmutableRow(string StableKey, MetadataSnapshotItem Item, int SerializedBytes);

    private sealed record SnapshotEntry(
        string SnapshotId,
        string QueryHash,
        string SelectionHash,
        string DocumentKey,
        string DocumentRevision,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        long CreatedTimestamp,
        long ExpiresTimestamp,
        ImmutableRow[] Rows,
        int TotalBytes);
}
