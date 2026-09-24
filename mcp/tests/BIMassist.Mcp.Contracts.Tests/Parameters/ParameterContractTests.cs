using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Parameters;

public sealed class ParameterContractTests
{
    [Fact]
    public void Shared_parameter_identity_roundtrip_preserves_guid_and_owner_context()
    {
        var identity = new ParameterIdentity
        {
            Kind = ParameterIdentityKind.SharedGuid,
            SharedGuid = Guid.Parse("9f51461a-bda5-4a03-8e7d-fb52e6e7d50c"),
            StableId = "shared:9f51461a-bda5-4a03-8e7d-fb52e6e7d50c",
            Name = "Brandschutzklasse",
            OwnerContext = "project"
        };

        string json = ContractJson.Serialize(identity);
        Assert.Contains("\"kind\":\"sharedGuid\"", json, StringComparison.Ordinal);
        ParameterIdentity restored = ContractJson.Deserialize<ParameterIdentity>(json);

        Assert.Equal(ParameterIdentityKind.SharedGuid, restored.Kind);
        Assert.Equal(identity.SharedGuid, restored.SharedGuid);
        Assert.Equal(identity.StableId, restored.StableId);
        Assert.Equal(identity.OwnerContext, restored.OwnerContext);
    }

    [Theory]
    [InlineData(ParameterValueKind.String)]
    [InlineData(ParameterValueKind.Integer)]
    [InlineData(ParameterValueKind.Double)]
    [InlineData(ParameterValueKind.Boolean)]
    [InlineData(ParameterValueKind.ElementReference)]
    [InlineData(ParameterValueKind.MaterialReference)]
    [InlineData(ParameterValueKind.TypeReference)]
    [InlineData(ParameterValueKind.None)]
    public void Parameter_value_kind_roundtrip_is_schema_stable(ParameterValueKind kind)
    {
        var value = new ParameterValue
        {
            Kind = kind,
            HasValue = kind is not ParameterValueKind.None,
            IsReadOnly = false,
            StringValue = kind is ParameterValueKind.String ? "EI90" : null,
            IntegerValue = kind is ParameterValueKind.Integer ? 7 : null,
            BooleanValue = kind is ParameterValueKind.Boolean ? true : null,
            DoubleValue = kind is ParameterValueKind.Double ? 1.0 : null,
            InternalDoubleValue = kind is ParameterValueKind.Double ? 3.280839895 : null,
            DisplayValue = kind is ParameterValueKind.Double ? "1,00 m" : null,
            SpecTypeId = kind is ParameterValueKind.Double ? "autodesk.spec.aec:length-2.0.0" : null,
            InputUnitTypeId = kind is ParameterValueKind.Double ? "autodesk.unit.unit:meters-1.0.1" : null,
            Reference = kind is ParameterValueKind.ElementReference or ParameterValueKind.MaterialReference or ParameterValueKind.TypeReference
                ? new ElementReferenceValue(
                    "unique-id-1",
                    42,
                    "Beton",
                    kind switch
                    {
                        ParameterValueKind.MaterialReference => ParameterReferenceKind.Material,
                        ParameterValueKind.TypeReference => ParameterReferenceKind.Type,
                        _ => ParameterReferenceKind.Element
                    })
                : null
        };

        string json = ContractJson.Serialize(value);
        ParameterValue restored = ContractJson.Deserialize<ParameterValue>(json);

        Assert.Equal(kind, restored.Kind);
        Assert.Equal(value.HasValue, restored.HasValue);
        Assert.Equal(value.InternalDoubleValue, restored.InternalDoubleValue);
        Assert.Equal(value.Reference?.UniqueId, restored.Reference?.UniqueId);
    }

    [Fact]
    public void Full_parameter_metadata_roundtrip_preserves_identity_binding_and_write_blocker()
    {
        var metadata = new ParameterMetadata
        {
            Identity = new ParameterIdentity
            {
                Kind = ParameterIdentityKind.BuiltIn,
                BuiltInId = -1001203,
                StableId = "builtin:ALL_MODEL_DESCRIPTION",
                Name = "Beschreibung"
            },
            Definition = new ParameterDefinitionMetadata(
                "Beschreibung",
                "autodesk.spec.aec:string.text-2.0.0",
                "autodesk.parameter.group:identityData-1.0.0",
                "Typbeschreibung",
                true,
                true),
            Binding = new ParameterBindingMetadata(
                ParameterBindingKind.ProjectType,
                ["OST_Doors", "OST_Windows"],
                null,
                "project"),
            StorageType = ParameterStorageType.String,
            IsReadOnly = true,
            Formula = "Type Name",
            BlockedReason = "FORMULA_CONTROLLED",
            Context = new ParameterContextMetadata
            {
                DocumentKey = "document-1",
                Target = new ParameterTarget { Kind = ParameterTargetKind.Document },
                IsTypeParameter = true,
                IsFamilyParameter = false
            },
            Unit = new ParameterUnitMetadata
            {
                SpecTypeId = "autodesk.spec.aec:string.text-2.0.0",
                UnitTypeId = null,
                Format = null
            },
            Worksharing = new ParameterWorksharingMetadata
            {
                IsWorkshared = true,
                IsOwnedByCurrentUser = false,
                Owner = "DOMAIN\\other.user",
                IsEditable = false
            }
        };

        ParameterMetadata restored = ContractJson.Deserialize<ParameterMetadata>(ContractJson.Serialize(metadata));

        Assert.Equal(ParameterBindingKind.ProjectType, restored.Binding.Kind);
        Assert.Equal(2, restored.Binding.CategoryIds.Count);
        Assert.True(restored.IsReadOnly);
        Assert.Equal("FORMULA_CONTROLLED", restored.BlockedReason);
        Assert.Equal("document-1", restored.Context.DocumentKey);
        Assert.Equal("DOMAIN\\other.user", restored.Worksharing.Owner);
    }
}
