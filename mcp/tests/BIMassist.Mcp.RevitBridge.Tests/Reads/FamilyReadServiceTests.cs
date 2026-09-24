using System.Security.Cryptography;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Reads;

public sealed class FamilyReadServiceTests
{
    private static readonly byte[] Key = SHA256.HashData("BIMassist Phase 3 family reads"u8);

    [Fact]
    public void Family_list_filters_in_place_families_and_sorts_deterministically()
    {
        var service = new FamilyReadService(new StablePaginator(new ReadCursorCodec(Key)));
        FamilyReadRecord[] records =
        [
            CreateRecord("family-z", 30, "Window", isInPlace: false),
            CreateRecord("family-b", 20, "Door - Project", isInPlace: true),
            CreateRecord("family-a", 10, "door - Single", isInPlace: false)
        ];
        var request = new ListFamiliesRequest
        {
            Page = new PageRequest { PageSize = 25 },
            NameContains = "DOOR",
            IncludeInPlace = false
        };

        PageResult<FamilySummary> result = service.ListFamilies(
            records,
            request,
            "document-1",
            "revision-1");

        FamilySummary family = Assert.Single(result.Items);
        Assert.Equal("family-a", family.UniqueId);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public void Family_metadata_sorts_and_pages_types()
    {
        var service = new FamilyReadService(new StablePaginator(new ReadCursorCodec(Key)));
        FamilyReadRecord record = CreateRecord(
            "family-a",
            10,
            "Door - Single",
            isInPlace: false,
            new FamilyTypeSummary { UniqueId = "type-z", ElementId = 12, Name = "Z Type" },
            new FamilyTypeSummary { UniqueId = "type-a", ElementId = 11, Name = "A Type" });
        var request = new GetFamilyMetadataRequest
        {
            FamilyUniqueId = "family-a",
            Page = new PageRequest { PageSize = 1 }
        };

        FamilyMetadata result = service.GetFamilyMetadata(
            [record],
            request,
            "document-1",
            "revision-1");

        Assert.Equal("A Type", Assert.Single(result.Types.Items).Name);
        Assert.NotNull(result.Types.NextCursor);
        Assert.Equal(2, result.Types.TotalCount);
        Assert.Equal(2, result.Family.TypeCount);
    }

    private static FamilyReadRecord CreateRecord(
        string uniqueId,
        long elementId,
        string name,
        bool isInPlace,
        params FamilyTypeSummary[] types) => new(
            new FamilySummary
            {
                UniqueId = uniqueId,
                ElementId = elementId,
                Name = name,
                IsInPlace = isInPlace,
                IsEditable = !isInPlace,
                IsShared = false,
                TypeCount = types.Length
            },
            types);
}
