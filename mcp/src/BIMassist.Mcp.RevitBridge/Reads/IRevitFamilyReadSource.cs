namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record FamilyReadSnapshot(
    string DocumentKey,
    string DocumentRevision,
    IReadOnlyList<FamilyReadRecord> Families);

internal interface IRevitFamilyReadSource
{
    FamilyReadSnapshot ReadFamilies(string sessionId, string documentKey);
}
