using System.Security.Cryptography;
using System.Text;

namespace BIMassist.Mcp.RevitBridge.Documents;

public static class DocumentKeyFactory
{
    private static readonly string[] AllowedPrefixes = ["path:", "cloud:", "central:", "unsaved:"];

    public static string Create(string sessionId, string stableIdentity)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(stableIdentity) ||
            !AllowedPrefixes.Any(prefix => stableIdentity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A stable path, cloud, central, or unsaved identity is required.", nameof(stableIdentity));
        }

        int separator = stableIdentity.IndexOf(':');
        string prefix = stableIdentity[..(separator + 1)].ToLowerInvariant();
        string value = stableIdentity[(separator + 1)..].Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("The stable document identity cannot be empty.", nameof(stableIdentity));
        }

        string normalizedValue = value.Replace('\\', '/').Normalize(NormalizationForm.FormC).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sessionId}\n{prefix}{normalizedValue}"));
        return $"doc-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
