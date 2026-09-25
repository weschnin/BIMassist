using BIMassist.Mcp.Contracts.Parameters;

namespace BIMassist.Mcp.RevitBridge.Reads;

internal sealed record ParameterReadSnapshot(
    string DocumentKey,
    string DocumentRevision,
    ParameterTarget Target,
    IReadOnlyList<ParameterReadRecord> Parameters);

internal interface IRevitParameterReadSource
{
    ParameterReadSnapshot ReadParameters(
        string sessionId,
        string documentKey,
        ParameterTarget target);
}
