using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.RevitBridge.Changes;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class StringWritePreconditionsTests
{
    [Fact]
    public void Changed_before_value_rejects_write_without_display_fallback()
    {
        ChangeOperation operation = Operation();
        Assert.Equal(BridgeErrorCodes.DocumentChanged, Assert.Throws<ChangePlanFailure>(() =>
            StringWritePreconditions.Check(operation, "different", true)).Code);
    }

    [Fact]
    public void Exactly_one_string_operation_is_required()
    {
        Assert.Equal(BridgeErrorCodes.InvalidRequest, Assert.Throws<ChangePlanFailure>(() =>
            StringWritePreconditions.Single([])).Code);
        Assert.Equal(-1001203, StringWritePreconditions.Single([Operation()]).Parameter.BuiltInId);
    }

    [Fact]
    public void Empty_string_parameter_with_no_value_can_be_planned_for_first_write()
    {
        ChangeOperation operation = Operation() with
        {
            Before = new ParameterValue { Kind = ParameterValueKind.None, HasValue = false, IsReadOnly = false }
        };
        Assert.Same(operation, StringWritePreconditions.Single([operation]));
        StringWritePreconditions.Check(operation, null, false);
    }

    [Fact]
    public void Current_null_and_empty_are_not_equivalent()
    {
        Assert.Equal(BridgeErrorCodes.DocumentChanged, Assert.Throws<ChangePlanFailure>(() =>
            StringWritePreconditions.Check(Operation(), null, false)).Code);
    }

    private static ChangeOperation Operation() => new()
    {
        OperationId = "op-1", Kind = ChangeOperationKind.SetParameterValue,
        Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "uid", ElementId = 10 },
        Parameter = new ParameterIdentity { Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -1001203, StableId = "built-in:-1001203", Name = "Description" },
        Before = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "" },
        After = new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = false, StringValue = "new" },
        Options = new Dictionary<string, string>()
    };
}
