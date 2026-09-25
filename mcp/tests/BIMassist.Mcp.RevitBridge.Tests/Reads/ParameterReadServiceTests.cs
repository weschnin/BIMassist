using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class ParameterReadServiceTests
{
    [Fact]
    public void List_filters_and_pages_materialized_parameter_records()
    {
        var service = new ParameterReadService(
            new StablePaginator(new ReadCursorCodec(RandomNumberGenerator.GetBytes(32))));
        ParameterTarget target = ElementTarget();
        ParameterReadRecord[] records =
        [
            Record("Comments", "parameter-element:42", ParameterStorageType.String, ParameterBindingKind.ProjectInstance, false, target),
            Record("Fire Rating", "shared:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ParameterStorageType.String, ParameterBindingKind.ProjectInstance, false, target),
            Record("Width", "built-in:-1001301", ParameterStorageType.Double, ParameterBindingKind.ProjectType, true, target)
        ];
        var request = new ListParametersRequest
        {
            Page = new PageRequest { PageSize = 10 },
            Target = target,
            NameContains = "t",
            StorageType = ParameterStorageType.String,
            BindingKind = ParameterBindingKind.ProjectInstance,
            IsReadOnly = false
        };

        PageResult<ParameterSummary> page = service.ListParameters(records, request, "doc-1", "rev-1");

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(["Comments", "Fire Rating"], page.Items.Select(item => item.Identity.Name));
    }

    [Fact]
    public void Metadata_lookup_uses_stable_parameter_identity()
    {
        var service = new ParameterReadService(
            new StablePaginator(new ReadCursorCodec(RandomNumberGenerator.GetBytes(32))));
        ParameterTarget target = ElementTarget();
        ParameterReadRecord wanted = Record("Fire Rating", "shared:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ParameterStorageType.String, ParameterBindingKind.ProjectInstance, false, target);
        var request = new GetParameterMetadataRequest
        {
            Target = target,
            Parameter = wanted.Summary.Identity
        };

        ParameterMetadata result = service.GetParameterMetadata([wanted], request);

        Assert.Equal(wanted.Summary.Identity.StableId, result.Identity.StableId);
        Assert.Equal(target, result.Context.Target);
    }

    private static ParameterReadRecord Record(
        string name,
        string stableId,
        ParameterStorageType storageType,
        ParameterBindingKind bindingKind,
        bool isReadOnly,
        ParameterTarget target)
    {
        ParameterIdentity identity = stableId.StartsWith("shared:", StringComparison.Ordinal)
            ? new ParameterIdentity
            {
                Kind = ParameterIdentityKind.SharedGuid,
                SharedGuid = Guid.Parse(stableId["shared:".Length..]),
                StableId = stableId,
                Name = name,
                DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
                IsInstance = bindingKind is ParameterBindingKind.ProjectInstance
            }
            : stableId.StartsWith("built-in:", StringComparison.Ordinal)
                ? new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.BuiltIn,
                    BuiltInId = long.Parse(stableId["built-in:".Length..]),
                    StableId = stableId,
                    Name = name,
                    DataTypeId = "autodesk.spec.aec:length-2.0.0",
                    IsInstance = bindingKind is ParameterBindingKind.ProjectInstance
                }
                : new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.ParameterElement,
                    StableId = stableId,
                    Name = name,
                    OwnerContext = "doc-1",
                    DefinitionId = 42,
                    DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
                    IsInstance = bindingKind is ParameterBindingKind.ProjectInstance
                };
        var summary = new ParameterSummary
        {
            Identity = identity,
            StorageType = storageType,
            BindingKind = bindingKind,
            IsReadOnly = isReadOnly
        };
        var metadata = new ParameterMetadata
        {
            Identity = identity,
            Definition = new ParameterDefinitionMetadata(name, identity.DataTypeId!, null, null, true, true),
            Binding = new ParameterBindingMetadata(bindingKind, ["revit-category:-2000011"], null, "element"),
            StorageType = storageType,
            IsReadOnly = isReadOnly,
            Context = new ParameterContextMetadata
            {
                DocumentKey = "doc-1",
                Target = target,
                IsTypeParameter = bindingKind is ParameterBindingKind.ProjectType,
                IsFamilyParameter = false
            },
            Worksharing = new ParameterWorksharingMetadata
            {
                IsWorkshared = false,
                IsOwnedByCurrentUser = false,
                IsEditable = !isReadOnly
            }
        };
        return new ParameterReadRecord(summary, metadata);
    }

    private static ParameterTarget ElementTarget() => new()
    {
        Kind = ParameterTargetKind.Element,
        UniqueId = "element-uid-1",
        ElementId = 100
    };
}
