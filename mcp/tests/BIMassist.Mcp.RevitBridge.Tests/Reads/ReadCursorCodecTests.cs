using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class ReadCursorCodecTests
{
    private static readonly byte[] Key = SHA256.HashData("BIMassist Phase 3 cursor tests"u8);

    [Fact]
    public void Cursor_roundtrip_preserves_revision_bound_query_context()
    {
        var codec = new ReadCursorCodec(Key);
        var context = new ReadCursorContext(
            BridgeOperations.ListFamilies,
            "document-1",
            "revision-7",
            "d9f6c10d7334f7f5de7dc19cd3e7d8e9786f60d9bfc804f8fc97fc88ca62cd6f",
            25,
            "family-uid-25");

        string cursor = codec.Encode(context);
        ReadCursorContext restored = codec.Decode(cursor, context with { Offset = 0, LastKey = string.Empty });

        Assert.Equal(context, restored);
    }

    [Fact]
    public void Cursor_rejects_tampering()
    {
        var codec = new ReadCursorCodec(Key);
        var context = new ReadCursorContext(
            BridgeOperations.ListFamilies,
            "document-1",
            "revision-7",
            "d9f6c10d7334f7f5de7dc19cd3e7d8e9786f60d9bfc804f8fc97fc88ca62cd6f",
            25,
            "family-uid-25");
        string cursor = codec.Encode(context);
        char replacement = cursor[^2] == 'A' ? 'B' : 'A';
        string tampered = cursor[..^2] + replacement + cursor[^1];

        ReadCursorException error = Assert.Throws<ReadCursorException>(() => codec.Decode(tampered, context));

        Assert.Equal(BridgeErrorCodes.InvalidCursor, error.ErrorCode);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("document")]
    [InlineData("revision")]
    [InlineData("query")]
    public void Cursor_rejects_reuse_in_another_context(string mismatch)
    {
        var codec = new ReadCursorCodec(Key);
        var context = new ReadCursorContext(
            BridgeOperations.ListFamilies,
            "document-1",
            "revision-7",
            "d9f6c10d7334f7f5de7dc19cd3e7d8e9786f60d9bfc804f8fc97fc88ca62cd6f",
            25,
            "family-uid-25");
        string cursor = codec.Encode(context);
        ReadCursorContext expected = mismatch switch
        {
            "operation" => context with { Operation = BridgeOperations.ListParameters },
            "document" => context with { DocumentKey = "document-2" },
            "revision" => context with { DocumentRevision = "revision-8" },
            _ => context with { QueryHash = new string('a', 64) }
        };

        ReadCursorException error = Assert.Throws<ReadCursorException>(() => codec.Decode(cursor, expected));

        Assert.Equal(
            mismatch == "revision" ? BridgeErrorCodes.DocumentChanged : BridgeErrorCodes.InvalidCursor,
            error.ErrorCode);
    }
}
