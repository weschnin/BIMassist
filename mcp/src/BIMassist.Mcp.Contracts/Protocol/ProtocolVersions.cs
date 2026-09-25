namespace BIMassist.Mcp.Contracts.Protocol;

public static class ProtocolVersions
{
    public const string ProtocolVersion = "1";
    public const string SchemaVersion = "1";
    public const string AddonVersion = "0.1.4";
    public const int RevitMajor = 2026;

    public static bool IsCompatible(string? protocolVersion, string? schemaVersion) =>
        string.Equals(protocolVersion, ProtocolVersion, StringComparison.Ordinal) &&
        string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal);
}
