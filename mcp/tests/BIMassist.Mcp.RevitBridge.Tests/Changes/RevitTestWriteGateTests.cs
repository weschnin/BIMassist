using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class RevitTestWriteGateTests
{
    private const string AllowedPath = @"C:\MCP-Test\Disposable.rvt";

    [Fact]
    public void Requires_explicit_bridge_opt_in_and_exact_local_test_file_path()
    {
        Assert.True(IsAllowed("1", AllowedPath, @"c:\mcp-test\disposable.rvt", false, false, false, false));
        Assert.False(IsAllowed("true", AllowedPath, AllowedPath, false, false, false, false));
        Assert.False(IsAllowed("1", @"C:\MCP-Test\Other.rvt", AllowedPath, false, false, false, false));
    }

    [Theory]
    [InlineData(null, false, false, false, false)]
    [InlineData("", false, false, false, false)]
    [InlineData("1", true, false, false, false)]
    [InlineData("1", false, true, false, false)]
    [InlineData("1", false, false, true, false)]
    [InlineData("1", false, false, false, true)]
    public void Denies_unsaved_cloud_workshared_family_or_read_only_documents(
        string? path, bool cloud, bool workshared, bool family, bool readOnly)
    {
        Assert.False(IsAllowed("1", AllowedPath, path, cloud, workshared, family, readOnly));
    }

    [Fact]
    public void Rejects_unc_paths_and_mapped_network_drives()
    {
        Assert.False(RevitTestWriteGate.IsLocalFilePath(@"\\server\share\Disposable.rvt", _ => DriveType.Fixed, _ => FileAttributes.Normal));
        Assert.False(RevitTestWriteGate.IsLocalFilePath(@"Z:\MCP-Test\Disposable.rvt", _ => DriveType.Network, _ => FileAttributes.Normal));
        Assert.True(RevitTestWriteGate.IsLocalFilePath(AllowedPath, _ => DriveType.Fixed, _ => FileAttributes.Normal));
    }

    [Fact]
    public void Rejects_reparse_points_in_any_path_component()
    {
        static FileAttributes GetAttributes(string component) =>
            component.EndsWith(@"\junction", StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Normal;

        Assert.False(RevitTestWriteGate.IsLocalFilePath(
            @"C:\MCP-Test\junction\Disposable.rvt", _ => DriveType.Fixed, GetAttributes));
        Assert.False(IsAllowed(
            "1",
            @"C:\MCP-Test\junction\Disposable.rvt",
            @"C:\MCP-Test\junction\Disposable.rvt",
            false,
            false,
            false,
            false,
            GetAttributes));
    }

    [Fact]
    public void Denies_when_path_attributes_are_inaccessible()
    {
        Assert.False(IsAllowed(
            "1", AllowedPath, AllowedPath, false, false, false, false,
            _ => throw new UnauthorizedAccessException("inaccessible")));
    }

    [Fact]
    public void Denies_non_rvt_files_even_when_path_matches()
    {
        Assert.False(IsAllowed("1", @"C:\MCP-Test\Disposable.rfa", @"C:\MCP-Test\Disposable.rfa", false, false, false, false));
    }

    private static bool IsAllowed(
        string? enabled,
        string? allowlistedPath,
        string? documentPath,
        bool isModelInCloud,
        bool isWorkshared,
        bool isFamilyDocument,
        bool isReadOnly,
        Func<string, FileAttributes>? attributesResolver = null) =>
        RevitTestWriteGate.IsAllowed(
            enabled,
            allowlistedPath,
            documentPath,
            isModelInCloud,
            isWorkshared,
            isFamilyDocument,
            isReadOnly,
            _ => DriveType.Fixed,
            attributesResolver ?? (_ => FileAttributes.Normal));
}
