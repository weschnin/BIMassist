using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class ParameterValueReadServiceTests
{
    [Fact]
    public void Selected_parameter_values_are_filtered_sorted_and_revision_bound()
    {
        ParameterTarget target = new() { Kind = ParameterTargetKind.Document };
        ParameterReadRecord alpha = CreateRecord(target, "Alpha", -10, "A");
        ParameterReadRecord beta = CreateRecord(target, "Beta", -20, "B");
        var paginator = new StablePaginator(new ReadCursorCodec(SHA256.HashData("parameter value service test"u8)));
        var service = new ParameterValueReadService(paginator);
        var request = new GetParameterValuesRequest
        {
            Page = new PageRequest { PageSize = 10 },
            Target = target,
            Parameters = [beta.Summary.Identity]
        };

        PageResult<ParameterValueEntry> result = service.GetValues(
            [alpha, beta],
            request,
            "document-1",
            "revision-9");

        ParameterValueEntry entry = Assert.Single(result.Items);
        Assert.Equal("Beta", entry.Parameter.Name);
        Assert.Equal("B", entry.Value.StringValue);
        Assert.Equal(1, result.TotalCount);
    }

    private static ParameterReadRecord CreateRecord(
        ParameterTarget target,
        string name,
        long builtInId,
        string value)
    {
        var identity = new ParameterIdentity
        {
            Kind = ParameterIdentityKind.BuiltIn,
            BuiltInId = builtInId,
            StableId = $"built-in:{builtInId}",
            Name = name,
            DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
            IsInstance = true
        };
        var summary = new ParameterSummary
        {
            Identity = identity,
            StorageType = ParameterStorageType.String,
            BindingKind = ParameterBindingKind.None,
            IsReadOnly = false
        };
        var metadata = new ParameterMetadata
        {
            Identity = identity,
            Definition = new ParameterDefinitionMetadata(name, identity.DataTypeId, null, null, true, true),
            Binding = new ParameterBindingMetadata(ParameterBindingKind.None, [], null, "target-element"),
            StorageType = ParameterStorageType.String,
            IsReadOnly = false,
            Context = new ParameterContextMetadata
            {
                DocumentKey = "document-1",
                Target = target,
                IsTypeParameter = false,
                IsFamilyParameter = false
            },
            Worksharing = new ParameterWorksharingMetadata
            {
                IsWorkshared = false,
                IsOwnedByCurrentUser = false,
                IsEditable = true
            }
        };
        var parameterValue = new ParameterValue
        {
            Kind = ParameterValueKind.String,
            HasValue = true,
            IsReadOnly = false,
            StringValue = value
        };
        return new ParameterReadRecord(summary, metadata, parameterValue);
    }
}
