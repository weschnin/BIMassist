using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class StrictWriteIdentityResolverTests
{
    [Fact]
    public void Supports_2026_long_element_ids_without_narrowing()
    {
        long id = (long)int.MaxValue + 1;
        var element = new LargeElement("uid-large", id);
        LargeElement result = StrictWriteIdentityResolver.Resolve(
            element.UniqueId, id, _ => element, _ => element,
            item => item.UniqueId, item => item.ElementId);
        Assert.Same(element, result);
    }

    private sealed record LargeElement(string UniqueId, long ElementId);

    private sealed record Element(string UniqueId, int ElementId);

    [Fact]
    public void Both_matching_identifiers_resolve_the_same_element()
    {
        var element = new Element("uid-42", 42);
        Element resolved = StrictWriteIdentityResolver.Resolve(
            "uid-42", 42, uid => uid == element.UniqueId ? element : null,
            id => id == element.ElementId ? element : null,
            e => e.UniqueId, e => e.ElementId);

        Assert.Same(element, resolved);
    }

    [Fact]
    public void Both_identifiers_resolving_different_elements_are_ambiguous()
    {
        var first = new Element("uid-42", 42);
        var second = new Element("uid-99", 99);
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => StrictWriteIdentityResolver.Resolve(
            "uid-42", 99, _ => first, _ => second, e => e.UniqueId, e => e.ElementId));

        Assert.Equal(BridgeErrorCodes.AmbiguousTarget, failure.Code);
    }

    [Fact]
    public void Returned_identity_must_match_both_requested_identifiers()
    {
        var wrong = new Element("uid-elsewhere", 13);
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => StrictWriteIdentityResolver.Resolve(
            "uid-42", 42, _ => wrong, _ => wrong, e => e.UniqueId, e => e.ElementId));

        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_supplied_require_both_lookups_even_if_the_other_succeeds(bool uniqueIdMissing)
    {
        var element = new Element("uid-42", 42);
        int uniqueCalls = 0, elementCalls = 0;
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => StrictWriteIdentityResolver.Resolve(
            "uid-42", 42,
            _ => { uniqueCalls++; return uniqueIdMissing ? null : element; },
            _ => { elementCalls++; return uniqueIdMissing ? element : null; },
            e => e.UniqueId, e => e.ElementId));

        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
        Assert.Equal(1, uniqueCalls);
        Assert.Equal(1, elementCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Single_supplied_identity_resolves_without_calling_the_other_lookup(bool useUniqueId)
    {
        var element = new Element("uid-42", 42);
        Element resolved = StrictWriteIdentityResolver.Resolve(
            useUniqueId ? "uid-42" : null, useUniqueId ? null : 42,
            _ => useUniqueId ? element : throw new Exception("unused lookup"),
            _ => useUniqueId ? throw new Exception("unused lookup") : element,
            e => e.UniqueId, e => e.ElementId);

        Assert.Same(element, resolved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Single_supplied_identity_must_match_returned_identity(bool useUniqueId)
    {
        var wrong = new Element("other", 13);
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => StrictWriteIdentityResolver.Resolve(
            useUniqueId ? "uid-42" : null, useUniqueId ? null : 42,
            _ => wrong, _ => wrong, e => e.UniqueId, e => e.ElementId));

        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Single_supplied_identity_missing_from_lookup_is_not_found(bool useUniqueId)
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => StrictWriteIdentityResolver.Resolve<Element>(
            useUniqueId ? "uid-42" : null, useUniqueId ? null : 42,
            _ => null, _ => null, e => e.UniqueId, e => e.ElementId));

        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void No_identity_fails_without_invoking_lookups()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => StrictWriteIdentityResolver.Resolve<Element>(
            null, null, _ => throw new Exception("unused lookup"), _ => throw new Exception("unused lookup"),
            e => e.UniqueId, e => e.ElementId));

        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void Blank_unique_id_is_a_target_failure_without_invoking_lookups()
    {
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => StrictWriteIdentityResolver.Resolve<Element>(
            " ", 42, _ => throw new Exception("invalid lookup input"),
            _ => throw new Exception("must not fall back"), e => e.UniqueId, e => e.ElementId));

        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void Equivalent_identity_from_distinct_wrappers_is_accepted()
    {
        var first = new Element("uid-42", 42);
        var second = new Element("uid-42", 42);
        Element resolved = StrictWriteIdentityResolver.Resolve(
            "uid-42", 42, _ => first, _ => second, e => e.UniqueId, e => e.ElementId);

        Assert.Same(first, resolved);
    }
}
