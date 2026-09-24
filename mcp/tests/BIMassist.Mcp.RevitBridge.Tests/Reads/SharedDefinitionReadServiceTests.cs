using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class SharedDefinitionReadServiceTests
{
    [Fact]
    public void Filters_by_group_and_name_then_paginates_deterministically()
    {
        var service = new SharedDefinitionReadService(
            new StablePaginator(new ReadCursorCodec(RandomNumberGenerator.GetBytes(32))));
        SharedDefinitionDescriptor[] definitions =
        [
            Definition("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Width", "Doors"),
            Definition("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Fire Rating", "Doors"),
            Definition("cccccccc-cccc-cccc-cccc-cccccccccccc", "Fire Rating", "Windows")
        ];
        var request = new ListSharedDefinitionsRequest
        {
            Page = new PageRequest { PageSize = 1 },
            GroupName = "doors",
            NameContains = "i"
        };

        PageResult<SharedDefinitionDescriptor> first = service.ListDefinitions(definitions, request, "doc-1", "rev-1");
        PageResult<SharedDefinitionDescriptor> second = service.ListDefinitions(
            definitions,
            request with { Page = new PageRequest { PageSize = 1, Cursor = first.NextCursor } },
            "doc-1",
            "rev-1");

        Assert.Equal(2, first.TotalCount);
        Assert.Equal("Fire Rating", Assert.Single(first.Items).Name);
        Assert.Equal("Width", Assert.Single(second.Items).Name);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public void Shared_guid_filter_is_exact()
    {
        var service = new SharedDefinitionReadService(
            new StablePaginator(new ReadCursorCodec(RandomNumberGenerator.GetBytes(32))));
        SharedDefinitionDescriptor wanted = Definition("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Fire Rating", "Doors");
        SharedDefinitionDescriptor other = Definition("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Width", "Doors");
        var request = new ListSharedDefinitionsRequest
        {
            Page = new PageRequest { PageSize = 10 },
            SharedGuid = wanted.SharedGuid
        };

        PageResult<SharedDefinitionDescriptor> page = service.ListDefinitions([other, wanted], request, "doc-1", "rev-1");

        Assert.Equal(wanted.SharedGuid, Assert.Single(page.Items).SharedGuid);
    }

    private static SharedDefinitionDescriptor Definition(string guid, string name, string group) => new()
    {
        SharedGuid = Guid.Parse(guid),
        Name = name,
        DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
        GroupName = group,
        Description = null,
        IsVisible = true,
        IsUserModifiable = true
    };
}
