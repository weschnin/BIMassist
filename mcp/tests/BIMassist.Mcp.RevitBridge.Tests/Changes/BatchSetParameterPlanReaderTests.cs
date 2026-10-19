using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class BatchSetParameterPlanReaderTests
{
    [Fact]
    public void Resolves_each_entry_in_request_order_and_aborts_whole_batch_on_second_failure()
    {
        var request = new PlanSetStringParametersBatchRequest
        {
            DocumentKey = "doc-1",
            Items = [Item(1), Item(2)]
        };
        int calls = 0;
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanReader.Read(
            request, "doc-1", "rev-1", item =>
            {
                calls++;
                if (calls == 2) throw new ChangePlanFailure(BridgeErrorCodes.TargetNotFound);
                return new ResolvedSetParameterPlan(item.Target, item.Parameter,
                    new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                        IsReadOnly = false, StringValue = "old" }, "rev-1");
            }, () => "rev-1"));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Rejects_revision_change_between_entries_without_returning_partial_result()
    {
        var request = new PlanSetStringParametersBatchRequest
        {
            DocumentKey = "doc-1", Items = [Item(1), Item(2)]
        };
        int reads = 0;
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanReader.Read(
            request, "doc-1", "rev-1", item =>
            {
                reads++;
                return new ResolvedSetParameterPlan(item.Target, item.Parameter,
                    new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                        IsReadOnly = false, StringValue = "old" }, "rev-1");
            }, () => reads == 2 ? "rev-2" : "rev-1"));
        Assert.Equal(BridgeErrorCodes.DocumentChanged, failure.Code);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Rejects_missing_second_observation_as_structured_failure()
    {
        var request = new PlanSetStringParametersBatchRequest
        {
            DocumentKey = "doc-1", Items = [Item(1), Item(2)]
        };
        ChangePlanFailure failure = Assert.Throws<ChangePlanFailure>(() => BatchSetParameterPlanReader.Read(
            request, "doc-1", "rev-1", item => item.Target.ElementId == 2 ? null! :
                new ResolvedSetParameterPlan(item.Target, item.Parameter,
                    new ParameterValue { Kind = ParameterValueKind.String, HasValue = true,
                        IsReadOnly = false, StringValue = "old" }, "rev-1"), () => "rev-1"));
        Assert.Equal(BridgeErrorCodes.TargetNotFound, failure.Code);
    }

    [Fact]
    public void Revit_source_has_internal_unwired_batch_read_entry_point()
    {
        var entryPoint = typeof(RevitSetParameterPlanSource).GetMethod("ReadBatch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(PlanSetStringParametersBatchRequest), typeof(string), typeof(string), typeof(string)], null);
        Assert.NotNull(entryPoint);
        Assert.Equal(typeof(IReadOnlyList<ResolvedSetParameterPlan>), entryPoint.ReturnType);
    }

    [Fact]
    public void Revit_source_has_internal_unwired_batch_plan_entry_point()
    {
        var entryPoint = typeof(RevitSetParameterPlanSource).GetMethod("BuildBatchPlan",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(PlanSetStringParametersBatchRequest), typeof(string), typeof(string),
                typeof(string), typeof(DateTimeOffset)], null);
        Assert.NotNull(entryPoint);
        Assert.Equal(typeof(ChangePlan), entryPoint.ReturnType);
    }

    private static StringParameterBatchItem Item(int index) => new()
    {
        Target = new ParameterTarget { Kind = ParameterTargetKind.Element,
            UniqueId = $"uid-{index}", ElementId = index },
        Parameter = new ParameterIdentity { Kind = ParameterIdentityKind.BuiltIn,
            BuiltInId = -1000 - index, StableId = $"built-in:{-1000 - index}", Name = "Name" },
        NewString = "new"
    };
}
