using System.Security.Cryptography;
using System.Text;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record ReadCursorContext(
    string Operation,
    string DocumentKey,
    string DocumentRevision,
    string QueryHash,
    int Offset,
    string LastKey);

internal sealed class ReadCursorException : Exception
{
    internal ReadCursorException(string errorCode)
        : base("The read cursor is invalid for this request.")
    {
        ErrorCode = errorCode;
    }

    internal string ErrorCode { get; }
}

internal sealed class ReadCursorCodec
{
    private const byte FormatVersion = 1;
    private const int MacLength = 32;
    private const int MaximumDecodedBytes = 4_096;
    private readonly byte[] _key;

    internal ReadCursorCodec(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("Cursor signing keys must contain at least 32 bytes.", nameof(key));
        }

        _key = key.ToArray();
    }

    internal string Encode(ReadCursorContext context)
    {
        ValidateContext(context, requirePosition: true);
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write(context.Operation);
            writer.Write(context.DocumentKey);
            writer.Write(context.DocumentRevision);
            writer.Write(context.QueryHash);
            writer.Write(context.Offset);
            writer.Write(context.LastKey);
        }

        byte[] payloadBytes = payload.ToArray();
        byte[] mac = HMACSHA256.HashData(_key, payloadBytes);
        byte[] cursorBytes = new byte[payloadBytes.Length + mac.Length];
        Buffer.BlockCopy(payloadBytes, 0, cursorBytes, 0, payloadBytes.Length);
        Buffer.BlockCopy(mac, 0, cursorBytes, payloadBytes.Length, mac.Length);
        return Base64UrlEncode(cursorBytes);
    }

    internal ReadCursorContext Decode(string cursor, ReadCursorContext expected)
    {
        ValidateExpected(expected);
        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(cursor);
        }
        catch (FormatException)
        {
            throw Invalid();
        }

        if (bytes.Length <= MacLength || bytes.Length > MaximumDecodedBytes)
        {
            throw Invalid();
        }

        int payloadLength = bytes.Length - MacLength;
        ReadOnlySpan<byte> payload = bytes.AsSpan(0, payloadLength);
        ReadOnlySpan<byte> suppliedMac = bytes.AsSpan(payloadLength, MacLength);
        byte[] expectedMac = HMACSHA256.HashData(_key, payload);
        if (!CryptographicOperations.FixedTimeEquals(suppliedMac, expectedMac))
        {
            throw Invalid();
        }

        ReadCursorContext actual;
        try
        {
            using var stream = new MemoryStream(bytes, 0, payloadLength, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadByte() != FormatVersion)
            {
                throw Invalid();
            }

            actual = new ReadCursorContext(
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadInt32(),
                reader.ReadString());
            if (stream.Position != payloadLength)
            {
                throw Invalid();
            }
            ValidateContext(actual, requirePosition: true);
        }
        catch (ReadCursorException)
        {
            throw;
        }
        catch (Exception error) when (error is EndOfStreamException or IOException or DecoderFallbackException or ArgumentException)
        {
            throw Invalid();
        }

        if (!string.Equals(actual.DocumentRevision, expected.DocumentRevision, StringComparison.Ordinal))
        {
            throw new ReadCursorException(BridgeErrorCodes.DocumentChanged);
        }
        if (!string.Equals(actual.Operation, expected.Operation, StringComparison.Ordinal) ||
            !string.Equals(actual.DocumentKey, expected.DocumentKey, StringComparison.Ordinal) ||
            !string.Equals(actual.QueryHash, expected.QueryHash, StringComparison.Ordinal))
        {
            throw Invalid();
        }

        return actual;
    }

    private static void ValidateExpected(ReadCursorContext context) => ValidateContext(context, requirePosition: false);

    private static void ValidateContext(ReadCursorContext context, bool requirePosition)
    {
        if (string.IsNullOrWhiteSpace(context.Operation) || context.Operation.Length > ContractLimits.MaximumIdentifierLength ||
            string.IsNullOrWhiteSpace(context.DocumentKey) || context.DocumentKey.Length > ContractLimits.MaximumDocumentKeyLength ||
            string.IsNullOrWhiteSpace(context.DocumentRevision) || context.DocumentRevision.Length > ContractLimits.MaximumRevisionLength ||
            !IsHash(context.QueryHash) ||
            (requirePosition && (context.Offset < 0 || (context.Offset > 0 && string.IsNullOrWhiteSpace(context.LastKey)))) ||
            context.LastKey.Length > ContractLimits.MaximumCursorLength)
        {
            throw Invalid();
        }
    }

    private static bool IsHash(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > ContractLimits.MaximumCursorLength)
        {
            throw new FormatException();
        }

        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException()
        };
        return Convert.FromBase64String(normalized);
    }

    private static ReadCursorException Invalid() => new(BridgeErrorCodes.InvalidCursor);
}
