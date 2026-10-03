using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Changes;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Changes;

public sealed class SetParameterPlanSelectionTests
{
    private static readonly Guid GuidA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GuidB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly ParameterTarget Target = new() { Kind = ParameterTargetKind.Element, UniqueId = "actual-uid", ElementId = 42 };

    [Fact]
    public void Selects_real_shared_guid_ignoring_client_name_and_stable_id()
    {
        ParameterReadRecord other = Record(Shared(GuidB, "Same Name", "shared:other"));
        ParameterReadRecord actual = Record(Shared(GuidA, "Actual Name", "shared:actual"));
        ResolvedSetParameterPlan result = SetParameterPlanSelection.Select(Snapshot(other, actual),
            Shared(GuidA, "Same Name", "client:untrusted"));
        Assert.Equal(Target, result.Target);
        Assert.Same(actual.Summary.Identity, result.Parameter);
        Assert.Same(actual.Value, result.Before);
        Assert.Equal("rev-1", result.DocumentRevision);
    }

    [Fact]
    public void Selects_real_built_in_id_not_client_name_or_stable_id()
    {
        ParameterReadRecord actual = Record(BuiltIn(-42, "Actual", "built-in:-42"));
        Assert.Same(actual.Summary.Identity, SetParameterPlanSelection.Select(Snapshot(actual),
            BuiltIn(-42, "Wrong name", "untrusted")).Parameter);
        Assert.Equal(BridgeErrorCodes.TargetNotFound, Failure(Snapshot(actual), BuiltIn(-43, "Actual", "built-in:-42")));
    }

    [Fact]
    public void Selects_project_parameter_by_definition_id_not_name_or_client_stable_id()
    {
        ParameterReadRecord other = Record(ProjectParameter(101, "Shared visible name", "parameter-element:other"));
        ParameterReadRecord actual = Record(ProjectParameter(202, "Actual project parameter", "parameter-element:actual"));

        ResolvedSetParameterPlan result = SetParameterPlanSelection.Select(Snapshot(other, actual),
            ProjectParameter(202, "Shared visible name", "client:untrusted"));

        Assert.Same(actual.Summary.Identity, result.Parameter);
    }

    [Fact]
    public void Missing_or_duplicate_real_identity_fails_closed()
    {
        ParameterIdentity requested = Shared(GuidA, "name", "client");
        Assert.Equal(BridgeErrorCodes.TargetNotFound, Failure(Snapshot(Record(Shared(GuidB, "name", "client"))), requested));
        Assert.Equal(BridgeErrorCodes.AmbiguousTarget, Failure(Snapshot(
            Record(Shared(GuidA, "one", "id-1")), Record(Shared(GuidA, "two", "id-2"))), requested));
    }

    [Fact]
    public void Rejects_non_string_storage_even_when_value_looks_like_string()
    {
        Assert.Equal(BridgeErrorCodes.TypeMismatch, Failure(Snapshot(Record(Shared(GuidA), storage: ParameterStorageType.Integer)), Shared(GuidA)));
    }

    [Theory]
    [InlineData(true, false, null, BridgeErrorCodes.ParameterReadOnly)]
    [InlineData(false, false, "parameter_read_only", BridgeErrorCodes.ParameterReadOnly)]
    [InlineData(false, false, "element_owned_by_other_user", BridgeErrorCodes.WorksharingOwnership)]
    [InlineData(false, false, "unexpected_block", BridgeErrorCodes.ParameterReadOnly)]
    [InlineData(false, true, null, BridgeErrorCodes.WorksharingOwnership)]
    public void Rejects_unwritable_metadata(bool readOnly, bool nonEditable, string? blocked, string error)
    {
        Assert.Equal(error, Failure(Snapshot(Record(Shared(GuidA), readOnly: readOnly,
            editable: !nonEditable, blocked: blocked)), Shared(GuidA)));
    }

    [Fact]
    public void Rejects_non_user_modifiable_definition_and_read_only_before()
    {
        Assert.Equal(BridgeErrorCodes.ParameterReadOnly, Failure(Snapshot(Record(Shared(GuidA), userModifiable: false)), Shared(GuidA)));
        Assert.Equal(BridgeErrorCodes.ParameterReadOnly, Failure(Snapshot(Record(Shared(GuidA), value: new ParameterValue
        {
            Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = true, StringValue = "before"
        })), Shared(GuidA)));
    }

    [Fact]
    public void Rejects_missing_or_non_string_before_value()
    {
        Assert.Equal(BridgeErrorCodes.TargetNotFound, Failure(Snapshot(Record(Shared(GuidA), includeValue: false)), Shared(GuidA)));
        Assert.Equal(BridgeErrorCodes.TypeMismatch, Failure(Snapshot(Record(Shared(GuidA), value: new ParameterValue
        {
            Kind = ParameterValueKind.Integer, HasValue = true, IsReadOnly = false, IntegerValue = 3
        })), Shared(GuidA)));
    }

    [Fact]
    public void Accepts_actual_unset_string_before_value()
    {
        var unset = new ParameterValue { Kind = ParameterValueKind.None, HasValue = false, IsReadOnly = false };
        Assert.Same(unset, SetParameterPlanSelection.Select(Snapshot(Record(Shared(GuidA), value: unset)), Shared(GuidA)).Before);
    }

    private static string Failure(ParameterReadSnapshot snapshot, ParameterIdentity requested) =>
        Assert.Throws<ChangePlanFailure>(() => SetParameterPlanSelection.Select(snapshot, requested)).Code;

    private static ParameterReadSnapshot Snapshot(params ParameterReadRecord[] records) => new("doc-1", "rev-1", Target, records);
    private static ParameterIdentity Shared(Guid guid, string name = "name", string stableId = "shared:actual") =>
        new() { Kind = ParameterIdentityKind.SharedGuid, SharedGuid = guid, Name = name, StableId = stableId };
    private static ParameterIdentity BuiltIn(long id, string name, string stableId) =>
        new() { Kind = ParameterIdentityKind.BuiltIn, BuiltInId = id, Name = name, StableId = stableId };
    private static ParameterIdentity ProjectParameter(long id, string name, string stableId) =>
        new() { Kind = ParameterIdentityKind.ParameterElement, DefinitionId = id, OwnerContext = "document:doc-1", DataTypeId = "autodesk.spec.aec:string.text-2.0.0", Name = name, StableId = stableId };

    private static ParameterReadRecord Record(ParameterIdentity identity, ParameterStorageType storage = ParameterStorageType.String,
        bool readOnly = false, bool editable = true, string? blocked = null, ParameterValue? value = default, bool includeValue = true,
        bool userModifiable = true)
    {
        var before = value ?? new ParameterValue { Kind = ParameterValueKind.String, HasValue = true, IsReadOnly = readOnly, StringValue = "before" };
        var summary = new ParameterSummary { Identity = identity, StorageType = storage, BindingKind = ParameterBindingKind.None, IsReadOnly = readOnly };
        var metadata = new ParameterMetadata
        {
            Identity = identity, StorageType = storage, IsReadOnly = readOnly, BlockedReason = blocked,
            Definition = new ParameterDefinitionMetadata(identity.Name, null, null, null, true, userModifiable),
            Binding = new ParameterBindingMetadata(ParameterBindingKind.None, [], null, "target-element"),
            Context = new ParameterContextMetadata { DocumentKey = "doc-1", Target = Target, IsTypeParameter = false, IsFamilyParameter = false },
            Worksharing = new ParameterWorksharingMetadata { IsWorkshared = !editable, IsOwnedByCurrentUser = false, IsEditable = editable }
        };
        return new ParameterReadRecord(summary, metadata, includeValue ? before : null);
    }
}
