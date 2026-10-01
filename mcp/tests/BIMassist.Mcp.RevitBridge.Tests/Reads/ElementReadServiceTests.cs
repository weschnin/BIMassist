using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class ElementReadServiceTests
{
    private static readonly byte[] Key = SHA256.HashData("BIMassist element reads"u8);

    [Fact]
    public void Element_list_filters_and_pages_in_stable_name_order()
    {
        var service = new ElementReadService(new StablePaginator(new ReadCursorCodec(Key)));
        ElementSummary[] elements =
        [
            Create("element-door-b", 20, "Door B", false, "revit-category:-2000014", "Doors", "type-door", "Single"),
            Create("type-door", 30, "Door Type", true, "revit-category:-2000014", "Doors"),
            Create("element-window", 40, "Door-like Window", false, "revit-category:-2000011", "Windows", "type-window", "Fixed"),
            Create("element-door-a", 10, "Door A", false, "revit-category:-2000014", "Doors", "type-door", "Single")
        ];
        var request = new ListElementsRequest
        {
            Page = new PageRequest { PageSize = 1 },
            NameContains = "door",
            CategoryId = "revit-category:-2000014",
            IncludeTypes = false
        };

        PageResult<ElementSummary> first = service.ListElements(elements, request, "document-1", "revision-1");
        PageResult<ElementSummary> second = service.ListElements(
            elements,
            request with { Page = new PageRequest { PageSize = 1, Cursor = first.NextCursor } },
            "document-1",
            "revision-1");
        PageResult<ElementSummary> withTypes = service.ListElements(
            elements,
            request with { Page = new PageRequest { PageSize = 10 }, IncludeTypes = true },
            "document-1",
            "revision-1");

        Assert.Equal("element-door-a", Assert.Single(first.Items).UniqueId);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal("element-door-b", Assert.Single(second.Items).UniqueId);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, second.TotalCount);
        Assert.Equal(3, withTypes.TotalCount);
        Assert.Contains(withTypes.Items, element => element.IsElementType);
    }

    private static ElementSummary Create(
        string uniqueId,
        long elementId,
        string name,
        bool isElementType,
        string categoryId,
        string categoryName,
        string? typeUniqueId = null,
        string? typeName = null) => new()
    {
        UniqueId = uniqueId,
        ElementId = elementId,
        Name = name,
        CategoryId = categoryId,
        CategoryName = categoryName,
        IsElementType = isElementType,
        TypeUniqueId = typeUniqueId,
        TypeName = typeName
    };
}
