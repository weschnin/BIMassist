using System.Text.RegularExpressions;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.Server.Transport;

public static partial class BridgeEndpointName
{
    public static string Create(string userSid, int processId)
    {
        if (string.IsNullOrWhiteSpace(userSid) || !SidPattern().IsMatch(userSid))
        {
            throw new ArgumentException("A valid Windows SID is required.", nameof(userSid));
        }

        if (processId <= 0)
        {
            throw new ArgumentException("A positive Revit process ID is required.", nameof(processId));
        }

        return $"bimassist.mcp.revit.{userSid.ToLowerInvariant()}.{processId}.v{ProtocolVersions.ProtocolVersion}";
    }

    [GeneratedRegex("^S-1-(?:[0-9]+-)+[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SidPattern();
}
