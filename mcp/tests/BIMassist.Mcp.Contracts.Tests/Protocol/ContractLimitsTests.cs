using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Protocol;

public sealed class ContractLimitsTests
{
    [Fact]
    public void Oversized_cursors_are_rejected()
    {
        var request = new PageRequest
        {
            PageSize = 1,
            Cursor = new string('c', ContractLimits.MaximumCursorLength + 1)
        };
        var result = new PageResult<string>
        {
            Items = ["item"],
            NextCursor = new string('c', ContractLimits.MaximumCursorLength + 1)
        };

        Assert.Throws<JsonException>(() => ContractJson.Serialize(request));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(result));
    }

    [Fact]
    public void Maximum_page_size_is_accepted()
    {
        var request = new PageRequest
        {
            PageSize = ContractLimits.MaximumPageSize,
            Cursor = "cursor-1"
        };

        PageRequest restored = ContractJson.Deserialize<PageRequest>(ContractJson.Serialize(request));

        Assert.Equal(ContractLimits.MaximumPageSize, restored.PageSize);
        Assert.Equal("cursor-1", restored.Cursor);
    }

    [Fact]
    public void Page_size_above_maximum_is_rejected()
    {
        string json = $$"""
            {"pageSize":{{ContractLimits.MaximumPageSize + 1}}}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PageRequest>(json));
    }

    [Fact]
    public void Large_page_result_roundtrip_preserves_order_and_cursor()
    {
        var result = new PageResult<string>
        {
            Items = Enumerable.Range(0, 1_000).Select(index => $"item-{index:D4}").ToArray(),
            NextCursor = "cursor-next",
            TotalCount = 2_000
        };

        PageResult<string> restored = ContractJson.Deserialize<PageResult<string>>(
            ContractJson.Serialize(result));

        Assert.Equal(1_000, restored.Items.Count);
        Assert.Equal("item-0000", restored.Items[0]);
        Assert.Equal("item-0999", restored.Items[^1]);
        Assert.Equal("cursor-next", restored.NextCursor);
        Assert.Equal(2_000, restored.TotalCount);
    }

    [Fact]
    public void Page_result_rejects_null_items()
    {
        const string json = """
            {"items":[null]}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PageResult<string>>(json));
    }

    [Fact]
    public void Page_result_rejects_negative_total_count()
    {
        const string json = """
            {"items":[],"totalCount":-1}
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PageResult<string>>(json));
    }

    [Fact]
    public void Page_result_rejects_more_than_maximum_items()
    {
        string items = string.Join(',', Enumerable.Repeat("\"item\"", ContractLimits.MaximumPageSize + 1));
        string json = $"{{\"items\":[{items}]}}";

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<PageResult<string>>(json));
    }

    [Fact]
    public void Payload_above_byte_limit_is_rejected()
    {
        string oversized = new('x', ContractLimits.MaximumPayloadBytes + 1);
        string json = $$"""
            {
              "protocolVersion":"1",
              "schemaVersion":"1",
              "requestId":"request-large",
              "operation":"status.get",
              "payload":{"value":"{{oversized}}"}
            }
            """;

        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }
}
