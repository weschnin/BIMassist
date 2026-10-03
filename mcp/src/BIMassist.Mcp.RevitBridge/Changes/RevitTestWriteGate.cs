using Autodesk.Revit.DB;
using BIMassist.Mcp.Contracts.Protocol;

namespace BIMassist.Mcp.RevitBridge.Changes;

/// <summary>Independent Revit-process gate for the test-only parameter Apply path.</summary>
internal static class RevitTestWriteGate
{
    internal const string EnableEnvironmentVariable = "BIMASSIST_MCP_ENABLE_TEST_WRITES";
    internal const string DocumentPathEnvironmentVariable = "BIMASSIST_MCP_TEST_DOCUMENT_PATH";

    internal static bool IsAllowed(
        string? enabled,
        string? allowlistedPath,
        string? documentPath,
        bool isModelInCloud,
        bool isWorkshared,
        bool isFamilyDocument,
        bool isReadOnly,
        Func<string, DriveType>? driveTypeResolver = null,
        Func<string, FileAttributes>? attributesResolver = null)
    {
        if (!string.Equals(enabled, "1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(allowlistedPath) ||
            string.IsNullOrWhiteSpace(documentPath) ||
            isModelInCloud || isWorkshared || isFamilyDocument || isReadOnly)
            return false;

        try
        {
            driveTypeResolver ??= static root => new DriveInfo(root).DriveType;
            attributesResolver ??= File.GetAttributes;
            if (!IsLocalFilePath(allowlistedPath, driveTypeResolver, attributesResolver) ||
                !IsLocalFilePath(documentPath, driveTypeResolver, attributesResolver))
                return false;

            string allowed = Path.GetFullPath(allowlistedPath);
            string actual = Path.GetFullPath(documentPath);
            return string.Equals(Path.GetExtension(allowed), ".rvt", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(actual), ".rvt", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(actual, allowed, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool IsLocalFilePath(
        string path,
        Func<string, DriveType> driveTypeResolver,
        Func<string, FileAttributes> attributesResolver)
    {
        ArgumentNullException.ThrowIfNull(driveTypeResolver);
        ArgumentNullException.ThrowIfNull(attributesResolver);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            fullPath.StartsWith("//", StringComparison.Ordinal))
            return false;

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) ||
            driveTypeResolver(root) is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            return false;

        string current = root;
        string[] components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string component in components)
        {
            current = Path.Combine(current, component);
            if ((attributesResolver(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }

        return true;
    }

    internal static void EnsureAllowed(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsAllowed(
                Environment.GetEnvironmentVariable(EnableEnvironmentVariable),
                Environment.GetEnvironmentVariable(DocumentPathEnvironmentVariable),
                document.PathName,
                document.IsModelInCloud,
                document.IsWorkshared,
                document.IsFamilyDocument,
                document.IsReadOnly))
            throw new ChangePlanFailure(BridgeErrorCodes.DocumentNotWritable);
    }
}
