using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class ProjectBindingReadServiceTests
{
    [Fact]
    public void Applies_binding_category_and_shared_filters_before_paging()
    {
        var service = new ProjectBindingReadService(
            new StablePaginator(new ReadCursorCodec(RandomNumberGenerator.GetBytes(32))));
        ProjectBindingDescriptor[] bindings =
        [
            Binding("Fire Rating", "shared:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ParameterIdentityKind.SharedGuid, ParameterBindingKind.ProjectInstance, "revit-category:-2000014"),
            Binding("Fire Rating Type", "parameter-element:42", ParameterIdentityKind.ParameterElement, ParameterBindingKind.ProjectType, "revit-category:-2000014"),
            Binding("Window Rating", "shared:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", ParameterIdentityKind.SharedGuid, ParameterBindingKind.ProjectInstance, "revit-category:-2000016")
        ];
        var request = new ListProjectBindingsRequest
        {
            Page = new PageRequest { PageSize = 10 },
            NameContains = "fire",
            BindingKind = ParameterBindingKind.ProjectInstance,
            CategoryId = "revit-category:-2000014",
            IsShared = true
        };

        PageResult<ProjectBindingDescriptor> page = service.ListBindings(bindings, request, "doc-1", "rev-1");

        ProjectBindingDescriptor item = Assert.Single(page.Items);
        Assert.Equal("Fire Rating", item.Definition.Name);
        Assert.Equal(ParameterIdentityKind.SharedGuid, item.Identity.Kind);
    }

    private static ProjectBindingDescriptor Binding(
        string name,
        string stableId,
        ParameterIdentityKind identityKind,
        ParameterBindingKind bindingKind,
        string categoryId)
    {
        Guid? sharedGuid = identityKind == ParameterIdentityKind.SharedGuid
            ? Guid.Parse(stableId["shared:".Length..])
            : null;
        return new ProjectBindingDescriptor
        {
            Identity = new ParameterIdentity
            {
                Kind = identityKind,
                SharedGuid = sharedGuid,
                StableId = stableId,
                Name = name,
                DefinitionId = identityKind == ParameterIdentityKind.ParameterElement ? 42 : null,
                DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
                IsInstance = bindingKind == ParameterBindingKind.ProjectInstance
            },
            Definition = new ParameterDefinitionMetadata(
                name,
                "autodesk.spec.aec:string.text-2.0.0",
                "autodesk.parameter.group:identityData-1.0.0",
                null,
                true,
                true),
            Binding = new ParameterBindingMetadata(
                bindingKind,
                [categoryId],
                null,
                "project")
        };
    }
}
