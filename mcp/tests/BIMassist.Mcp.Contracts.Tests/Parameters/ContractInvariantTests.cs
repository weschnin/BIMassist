using System.Text.Json;
using BIMassist.Mcp.Contracts.Changes;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Parameters;

public sealed class ContractInvariantTests
{
    [Fact]
    public void Parameter_identity_rejects_discriminator_extraneous_fields()
    {
        const string json = """
            {"kind":"builtIn","sharedGuid":"9f51461a-bda5-4a03-8e7d-fb52e6e7d50c","stableId":"builtin:-1001203","name":"Comments"}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterIdentity>(json));
    }

    [Fact]
    public void Standalone_element_reference_requires_stable_bounded_identity()
    {
        var missing = new ElementReferenceValue("   ", 0, null, ParameterReferenceKind.Element);
        var oversized = new ElementReferenceValue(
            new string('u', ContractLimits.MaximumIdentifierLength + 1),
            0,
            null,
            ParameterReferenceKind.Element);

        Assert.Throws<JsonException>(() => ContractJson.Serialize(missing));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(oversized));
    }

    [Fact]
    public void Parameter_value_rejects_oversized_text_payload()
    {
        var value = new ParameterValue
        {
            Kind = ParameterValueKind.String,
            HasValue = true,
            IsReadOnly = false,
            StringValue = new string('x', ContractLimits.MaximumValueTextLength + 1)
        };

        Assert.Throws<JsonException>(() => ContractJson.Serialize(value));
    }

    [Fact]
    public void Standalone_binding_rejects_duplicate_categories_and_none_with_categories()
    {
        var duplicate = new ParameterBindingState
        {
            Kind = ParameterBindingKind.ProjectInstance,
            CategoryIds = ["-2000011", "-2000011"]
        };
        var noneWithCategory = new ParameterBindingState
        {
            Kind = ParameterBindingKind.None,
            CategoryIds = ["-2000011"]
        };

        Assert.Throws<JsonException>(() => ContractJson.Serialize(duplicate));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(noneWithCategory));
    }

    [Fact]
    public void Parameter_metadata_rejects_empty_definition_and_context_fields()
    {
        const string json = """
            {
              "identity":{"kind":"builtIn","stableId":"builtin:-1001203","name":"Comments"},
              "definition":{"name":"","dataTypeId":"autodesk.spec:string","isVisible":true,"isUserModifiable":true},
              "binding":{"kind":"projectInstance","categoryIds":["-2000011"],"origin":"project"},
              "storageType":"string","isReadOnly":false,
              "context":{"documentKey":"document-1","ownerUniqueId":"owner-1","ownerElementId":42,"isTypeParameter":false,"isFamilyParameter":false},
              "worksharing":{"isWorkshared":false,"isOwnedByCurrentUser":false,"isEditable":true}
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterMetadata>(json));
    }

    [Fact]
    public void Element_target_rejects_non_positive_element_id()
    {
        const string json = """
            {"kind":"element","elementId":0}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterTarget>(json));
    }

    [Fact]
    public void Set_value_operation_rejects_unrelated_binding_state()
    {
        const string json = """
            {
              "operationId":"operation-1","kind":"setParameterValue",
              "target":{"kind":"element","elementId":42},
              "parameter":{"kind":"builtIn","stableId":"builtin:-1001203","name":"Comments"},
              "before":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"old"},
              "after":{"kind":"string","hasValue":true,"isReadOnly":false,"stringValue":"new"},
              "afterBinding":{"kind":"projectInstance","categoryIds":["-2000011"]},
              "options":{}
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangeOperation>(json));
    }

    [Fact]
    public void Bind_operation_requires_non_empty_project_binding_categories()
    {
        const string json = """
            {
              "operationId":"operation-1","kind":"bindSharedParameter",
              "target":{"kind":"document"},
              "parameter":{"kind":"sharedGuid","sharedGuid":"a91c05cc-d53d-4ec0-8a01-aaf7bfed7c57","stableId":"shared:a91c05cc-d53d-4ec0-8a01-aaf7bfed7c57","name":"Rating"},
              "afterBinding":{"kind":"projectInstance","categoryIds":[]},
              "options":{}
            }
            """;

        ChangeOperation operation = System.Text.Json.JsonSerializer.Deserialize<ChangeOperation>(json, ContractJson.Options)!;
        Assert.Equal(ChangeOperationKind.BindSharedParameter, operation.Kind);
        Assert.Empty(operation.AfterBinding!.CategoryIds);
        Assert.Throws<JsonException>(() => ContractValidator.Validate(operation));
    }

    [Fact]
    public void Shared_parameter_identity_requires_guid()
    {
        const string json = """
            {
              "kind":"sharedGuid",
              "stableId":"shared:missing",
              "name":"Brandschutzklasse"
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterIdentity>(json));
    }

    [Fact]
    public void Element_target_requires_stable_identifier()
    {
        const string json = """
            {"kind":"element"}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterTarget>(json));
    }

    [Fact]
    public void Built_in_parameter_identity_requires_explicit_api_id()
    {
        const string missingId = """
            {"kind":"builtIn","stableId":"builtin:ALL_MODEL_DESCRIPTION","name":"Description"}
            """;
        const string valid = """
            {"kind":"builtIn","builtInId":-1001203,"stableId":"builtin:ALL_MODEL_DESCRIPTION","name":"Description"}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterIdentity>(missingId));
        Assert.Equal(-1001203, ContractJson.Deserialize<ParameterIdentity>(valid).BuiltInId);
    }

    [Fact]
    public void Family_target_rejects_name_only_identity()
    {
        const string json = """
            {"kind":"family","familyName":"Door - Single"}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterTarget>(json));
    }

    [Fact]
    public void Family_type_target_accepts_stable_symbol_identity_with_display_names()
    {
        const string json = """
            {"kind":"familyType","uniqueId":"symbol-uid-1","elementId":42,"familyName":"Door - Single","typeName":"900 x 2100"}
            """;

        ParameterTarget target = ContractJson.Deserialize<ParameterTarget>(json);

        Assert.Equal("symbol-uid-1", target.UniqueId);
        Assert.Equal(42, target.ElementId);
    }

    [Fact]
    public void Parameter_value_requires_matching_discriminator_payload()
    {
        const string json = """
            {
              "kind":"string",
              "hasValue":true,
              "isReadOnly":false,
              "integerValue":42
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterValue>(json));
    }

    [Fact]
    public void Empty_parameter_value_cannot_contain_a_payload()
    {
        const string json = """
            {
              "kind":"none",
              "hasValue":false,
              "isReadOnly":false,
              "stringValue":"unexpected"
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterValue>(json));
    }

    [Fact]
    public void Formula_controlled_value_requires_blocking_reason()
    {
        const string json = """
            {
              "kind":"double",
              "hasValue":true,
              "isReadOnly":true,
              "internalDoubleValue":1.25,
              "specTypeId":"autodesk.spec.aec:length-2.0.0",
              "inputUnitTypeId":"autodesk.unit.unit:meters-1.0.1",
              "formula":"Width * 2"
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ParameterValue>(json));
    }

    [Fact]
    public void Change_plan_requires_its_canonical_hash()
    {
        const string json = """
            {
              "planId":"plan-1",
              "sessionId":"session-1",
              "documentKey":"document-1",
              "expectedRevision":"revision-1",
              "createdAtUtc":"2026-09-21T12:00:00Z",
              "expiresAtUtc":"2026-09-21T12:05:00Z",
              "operations":[],
              "warnings":[]
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ChangePlan>(json));
    }
}
