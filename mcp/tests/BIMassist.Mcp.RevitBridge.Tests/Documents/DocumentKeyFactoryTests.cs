using BIMassist.Mcp.RevitBridge.Documents;

namespace BIMassist.Mcp.RevitBridge.Tests.Documents;

public sealed class DocumentKeyFactoryTests
{
    [Fact]
    public void Saved_path_identity_is_stable_across_case_and_separator_variants()
    {
        string first = DocumentKeyFactory.Create("session-1", "path:C:\\Models\\Model.rvt");
        string second = DocumentKeyFactory.Create("session-1", "path:c:/models/model.rvt");

        Assert.Equal(first, second);
        Assert.StartsWith("doc-", first, StringComparison.Ordinal);
        Assert.Equal(36, first.Length);
    }

    [Fact]
    public void Different_stable_identities_produce_different_keys()
    {
        Assert.NotEqual(
            DocumentKeyFactory.Create("session-1", "unsaved:101"),
            DocumentKeyFactory.Create("session-1", "unsaved:102"));
    }

    [Theory]
    [InlineData("", "path:model.rvt")]
    [InlineData("session-1", "")]
    [InlineData("session-1", "title-only")]
    public void Invalid_or_ambiguous_identifiers_are_rejected(string sessionId, string identity)
    {
        Assert.Throws<ArgumentException>(() => DocumentKeyFactory.Create(sessionId, identity));
    }
}
