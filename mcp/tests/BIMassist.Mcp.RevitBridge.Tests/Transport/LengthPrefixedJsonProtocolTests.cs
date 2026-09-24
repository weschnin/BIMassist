using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Transport;

namespace BIMassist.Mcp.RevitBridge.Tests.Transport;

public sealed class LengthPrefixedJsonProtocolTests
{
    [Fact]
    public async Task Protocol_roundtrips_a_strict_bridge_request()
    {
        var request = new BridgeRequest
        {
            ProtocolVersion = ProtocolVersions.ProtocolVersion,
            SchemaVersion = ProtocolVersions.SchemaVersion,
            RequestId = "request-1",
            Operation = BridgeOperations.GetStatus,
            Payload = JsonDocument.Parse("{}").RootElement.Clone()
        };
        await using var stream = new MemoryStream();

        await LengthPrefixedJsonProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        BridgeRequest restored = await LengthPrefixedJsonProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None);

        Assert.Equal(request.ProtocolVersion, restored.ProtocolVersion);
        Assert.Equal(request.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(request.RequestId, restored.RequestId);
        Assert.Equal(request.Operation, restored.Operation);
        Assert.Equal("{}", restored.Payload.GetRawText());
    }

    [Fact]
    public async Task Protocol_rejects_oversized_frame_before_reading_payload()
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, ContractLimits.MaximumContractBytes + 1);
        await using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LengthPrefixedJsonProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Protocol_rejects_truncated_payload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{}");
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payload.Length + 3);
        payload.CopyTo(frame, 4);
        await using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            LengthPrefixedJsonProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Protocol_rejects_zero_length_frame()
    {
        await using var stream = new MemoryStream(new byte[4]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LengthPrefixedJsonProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }
}
