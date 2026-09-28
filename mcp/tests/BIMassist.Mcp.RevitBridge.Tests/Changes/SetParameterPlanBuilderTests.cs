using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class SetParameterPlanBuilderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T18:00:00Z");

    [Fact]
    public void Builds_a_single_hashed_operation_using_bridge_observed_before_identity_and_revision()
    {
        ChangePlan plan = SetParameterPlanBuilder.Build(Request(), ActualTarget(), ActualIdentity(), Before(), "session-1:4", Now);

        Assert.Equal("session-1:4", plan.ExpectedRevision);
        Assert.Equal("doc-1", plan.DocumentKey);
        Assert.Equal(Now.AddMinutes(5), plan.ExpiresAtUtc);
        Assert.Equal(ChangePlanHasher.ComputeHash(plan), plan.PlanHash);
        ChangeOperation operation = Assert.Single(plan.Operations);
        Assert.Equal(ChangeOperationKind.SetParameterValue, operation.Kind);
        Assert.Equal("actual-uid", operation.Target.UniqueId);
        Assert.Equal(42, operation.Target.ElementId);
        Assert.Equal("Actual Revit name", operation.Parameter.Name);
        Assert.Equal("old", operation.Before?.StringValue);
        Assert.Equal("new", operation.After?.StringValue);
        Assert.Empty(operation.Options);
    }

    [Fact]
    public void Stale_revision_is_rejected_before_plan_creation()
    {
        ChangePlanFailure error = Assert.Throws<ChangePlanFailure>(() =>
            SetParameterPlanBuilder.Build(Request(), ActualTarget(), ActualIdentity(), Before(), "session-1:5", Now));
        Assert.Equal(BridgeErrorCodes.DocumentChanged, error.Code);
    }

    [Theory]
    [InlineData("other-uid", 42)]
    [InlineData("actual-uid", 43)]
    public void Target_must_match_bridge_resolved_identity(string uniqueId, long elementId)
    {
        ParameterTarget wrong = ActualTarget() with { UniqueId = uniqueId, ElementId = elementId };
        ChangePlanFailure error = Assert.Throws<ChangePlanFailure>(() =>
            SetParameterPlanBuilder.Build(Request(), wrong, ActualIdentity(), Before(), "session-1:4", Now));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, error.Code);
    }

    [Fact]
    public void Actual_parameter_must_match_requested_api_identity()
    {
        ParameterIdentity wrong = ActualIdentity() with { BuiltInId = -1001204 };
        ChangePlanFailure error = Assert.Throws<ChangePlanFailure>(() =>
            SetParameterPlanBuilder.Build(Request(), ActualTarget(), wrong, Before(), "session-1:4", Now));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, error.Code);
    }

    [Fact]
    public void Read_only_parameter_cannot_be_planned()
    {
        ChangePlanFailure error = Assert.Throws<ChangePlanFailure>(() =>
            SetParameterPlanBuilder.Build(Request(), ActualTarget(), ActualIdentity(), Before() with { IsReadOnly = true }, "session-1:4", Now));
        Assert.Equal(BridgeErrorCodes.ParameterReadOnly, error.Code);
    }

    private static BridgeRequest Request() => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-1", SessionId = "session-1", DocumentKey = "doc-1",
        ExpectedRevision = "session-1:4", IdempotencyKey = "plan-key-1",
        Operation = BridgeOperations.PlanSetParameterValues,
        Payload = JsonDocument.Parse("""{"target":{"kind":"element","uniqueId":"actual-uid","elementId":42},"parameter":{"kind":"builtIn","builtInId":-1001203,"stableId":"built-in:-1001203","name":"Client name"},"after":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"new"}}""").RootElement.Clone()
    };

    private static ParameterTarget ActualTarget() => new()
    {
        Kind = ParameterTargetKind.Element, UniqueId = "actual-uid", ElementId = 42
    };

    private static ParameterIdentity ActualIdentity() => new()
    {
        Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001203,
        StableId = "built-in:-1001203", Name = "Actual Revit name"
    };

    private static ParameterValue Before() => new()
    {
        Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "old"
    };
}
