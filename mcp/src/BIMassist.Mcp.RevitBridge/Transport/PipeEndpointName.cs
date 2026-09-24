using System.Text.RegularExpressions;

namespace BIMassist.Mcp.RevitBridge.Transport;

public static partial class PipeEndpointName
{
    private const int MaximumEndpointLength = 180;

    public static string Create(string userSid, int processId, string protocolVersion)
    {
        if (string.IsNullOrWhiteSpace(userSid) || !SidPattern().IsMatch(userSid))
        {
            throw new ArgumentException("A valid Windows SID is required.", nameof(userSid));
        }

        if (processId <= 0)
        {
            throw new ArgumentException("A positive Revit process ID is required.", nameof(processId));
        }

        if (string.IsNullOrWhiteSpace(protocolVersion) || !VersionPattern().IsMatch(protocolVersion))
        {
            throw new ArgumentException("The protocol version must contain digits only.", nameof(protocolVersion));
        }

        string endpoint = $"bimassist.mcp.revit.{userSid.ToLowerInvariant()}.{processId}.v{protocolVersion}";
        if (endpoint.Length > MaximumEndpointLength)
        {
            throw new ArgumentException("The generated pipe endpoint is too long.", nameof(userSid));
        }

        return endpoint;
    }

    [GeneratedRegex("^S-1-(?:[0-9]+-)+[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SidPattern();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
