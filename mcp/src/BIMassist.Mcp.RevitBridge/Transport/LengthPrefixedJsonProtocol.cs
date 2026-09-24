using System.Buffers.Binary;
using System.Text;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.RevitBridge.Transport;

public static class LengthPrefixedJsonProtocol
{
    private const int PrefixLength = sizeof(int);

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[PrefixLength];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (payloadLength <= 0 || payloadLength > ContractLimits.MaximumContractBytes)
        {
            throw new InvalidDataException("The bridge frame length is outside the allowed range.");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return ContractJson.Deserialize<T>(payload);
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string json = ContractJson.Serialize(value);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length == 0 || payload.Length > ContractLimits.MaximumContractBytes)
        {
            throw new InvalidDataException("The bridge frame length is outside the allowed range.");
        }

        byte[] prefix = new byte[PrefixLength];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
