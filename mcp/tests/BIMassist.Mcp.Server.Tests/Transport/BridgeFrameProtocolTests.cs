using System.Buffers.Binary;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Server.Transport;

namespace BIMassist.Mcp.Server.Tests.Transport;

public sealed class BridgeFrameProtocolTests
{
    [Fact]
    public async Task Round_trips_a_contract_request_over_a_length_prefixed_frame()
    {
        using JsonDocument payload = JsonDocument.Parse("{}");
        var request = new BridgeRequest
        {
            ProtocolVersion = "1",
            SchemaVersion = "1",
            RequestId = "request-1",
            Operation = BridgeOperations.GetStatus,
            Payload = payload.RootElement.Clone()
        };
        await using var stream = new MemoryStream();

        await BridgeFrameProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        BridgeRequest actual = await BridgeFrameProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None);

        Assert.Equal(request.ProtocolVersion, actual.ProtocolVersion);
        Assert.Equal(request.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(request.RequestId, actual.RequestId);
        Assert.Equal(request.Operation, actual.Operation);
        Assert.Equal(request.Payload.GetRawText(), actual.Payload.GetRawText());
    }

    [Fact]
    public async Task Rejects_an_oversized_frame_before_reading_its_payload()
    {
        await using var stream = new MemoryStream();
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, ContractLimits.MaximumContractBytes + 1);
        await stream.WriteAsync(prefix);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BridgeFrameProtocol.ReadAsync<BridgeRequest>(stream, CancellationToken.None));
    }
}
