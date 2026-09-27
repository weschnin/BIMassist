using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class MetadataSnapshotServiceTests
{
    [Fact]
    public void Immutable_snapshot_continuation_uses_authenticated_cached_rows()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        var codec = new ReadCursorCodec(SHA256.HashData("snapshot service cursor key"u8));
        var service = new MetadataSnapshotService(
            codec,
            clock,
            MetadataSnapshotOptions.Default,
            () => "snapshot-0123456789abcdef0123456789abcdef");
        ExportMetadataSnapshotRequest firstRequest = Request(pageSize: 1);

        MetadataSnapshotPage first = service.Create(
            firstRequest,
            "document-1",
            "revision-4",
            [Row("family:1", Family("family-1", 1)), Row("family:2", Family("family-2", 2))]);
        MetadataSnapshotPage second = service.Continue(
            firstRequest with { Page = new PageRequest { PageSize = 1, Cursor = first.Page.NextCursor } },
            "document-1");

        Assert.Single(first.Page.Items);
        Assert.Single(second.Page.Items);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal("revision-4", second.DocumentRevision);
        Assert.Null(second.Page.NextCursor);
        Assert.NotEqual(first.Page.Items[0].Family!.UniqueId, second.Page.Items[0].Family!.UniqueId);
    }

    [Fact]
    public void Snapshot_continuation_rejects_selection_reuse_and_reports_expiry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        var codec = new ReadCursorCodec(SHA256.HashData("snapshot expiry cursor key"u8));
        var options = MetadataSnapshotOptions.Default with { TimeToLive = TimeSpan.FromMinutes(5) };
        var service = new MetadataSnapshotService(
            codec,
            clock,
            options,
            () => "snapshot-fedcba9876543210fedcba9876543210");
        ExportMetadataSnapshotRequest request = Request(pageSize: 1);
        MetadataSnapshotPage first = service.Create(
            request,
            "document-1",
            "revision-4",
            [Row("family:1", Family("family-1", 1)), Row("family:2", Family("family-2", 2))]);
        var cursorPage = new PageRequest { PageSize = 1, Cursor = first.Page.NextCursor };
        ExportMetadataSnapshotRequest changedSelection = request with
        {
            Page = cursorPage,
            Selection = request.Selection with { IncludeSharedDefinitions = true }
        };

        ReadCursorException selectionError = Assert.Throws<ReadCursorException>(
            () => service.Continue(changedSelection, "document-1"));
        clock.Advance(TimeSpan.FromMinutes(6));
        ReadCursorException expiryError = Assert.Throws<ReadCursorException>(
            () => service.Continue(request with { Page = cursorPage }, "document-1"));

        Assert.Equal(BridgeErrorCodes.InvalidCursor, selectionError.ErrorCode);
        Assert.Equal(BridgeErrorCodes.SnapshotExpired, expiryError.ErrorCode);
    }

    [Fact]
    public void Snapshot_cursor_rejects_changed_numeric_target_identity()
    {
        var codec = new ReadCursorCodec(SHA256.HashData("snapshot target cursor key"u8));
        var service = new MetadataSnapshotService(codec);
        var originalTarget = new BIMassist.Mcp.Contracts.Parameters.ParameterTarget
        {
            Kind = BIMassist.Mcp.Contracts.Parameters.ParameterTargetKind.Element,
            UniqueId = "element-stable-1",
            ElementId = 101
        };
        ExportMetadataSnapshotRequest request = Request(pageSize: 1) with
        {
            Selection = Request(pageSize: 1).Selection with { ParameterTargets = [originalTarget] }
        };
        MetadataSnapshotPage first = service.Create(
            request,
            "document-1",
            "revision-4",
            [Row("item:1", Family("family-1", 1)), Row("item:2", Family("family-2", 2))]);
        ExportMetadataSnapshotRequest changedTargetRequest = request with
        {
            Page = new PageRequest { PageSize = 1, Cursor = first.Page.NextCursor },
            Selection = request.Selection with
            {
                ParameterTargets = [originalTarget with { ElementId = 202 }]
            }
        };

        ReadCursorException error = Assert.Throws<ReadCursorException>(
            () => service.Continue(changedTargetRequest, "document-1"));

        Assert.Equal(BridgeErrorCodes.InvalidCursor, error.ErrorCode);
    }

    [Fact]
    public void Snapshot_admission_and_pages_are_byte_bounded()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T06:00:00Z"));
        var codec = new ReadCursorCodec(SHA256.HashData("snapshot bounds cursor key"u8));
        var options = MetadataSnapshotOptions.Default with
        {
            MaximumSnapshotBytes = 2_000,
            MaximumTotalBytes = 4_000,
            MaximumPageItemBytes = 500
        };
        var service = new MetadataSnapshotService(
            codec,
            clock,
            options,
            () => "snapshot-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        string longName = new('x', 300);
        ExportMetadataSnapshotRequest request = Request(pageSize: 10);

        MetadataSnapshotPage page = service.Create(
            request,
            "document-1",
            "revision-4",
            [
                Row("family:1", Family("family-1", 1, longName)),
                Row("family:2", Family("family-2", 2, longName))
            ]);
        ReadCursorException sizeError = Assert.Throws<ReadCursorException>(() => service.Create(
            request,
            "document-1",
            "revision-4",
            Enumerable.Range(1, 20)
                .Select(id => Row($"family:{id}", Family($"oversized-{id}", id, longName)))
                .ToArray()));

        Assert.Single(page.Page.Items);
        Assert.NotNull(page.Page.NextCursor);
        Assert.Equal(BridgeErrorCodes.LimitExceeded, sizeError.ErrorCode);
    }

    private static ExportMetadataSnapshotRequest Request(int pageSize) => new()
    {
        Page = new PageRequest { PageSize = pageSize },
        Selection = new MetadataSnapshotSelection
        {
            IncludeFamilies = true,
            IncludeSharedDefinitions = false,
            IncludeProjectBindings = false,
            ParameterTargets = [],
            IncludeParameterValues = false
        }
    };

    private static MetadataSnapshotRow Row(string key, MetadataSnapshotItem item) => new(key, item);

    private static MetadataSnapshotItem Family(string uniqueId, long elementId, string? name = null) => new()
    {
        Kind = MetadataSnapshotItemKind.Family,
        Family = new FamilySummary
        {
            UniqueId = uniqueId,
            ElementId = elementId,
            Name = name ?? uniqueId,
            IsInPlace = false,
            IsEditable = true,
            IsShared = false,
            TypeCount = 0
        }
    };

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        internal void Advance(TimeSpan duration)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
        }
    }
}
