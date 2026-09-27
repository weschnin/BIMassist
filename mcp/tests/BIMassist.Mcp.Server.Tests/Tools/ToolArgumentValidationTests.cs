using System.Text.Json;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Server.Tools;

namespace BIMassist.Mcp.Server.Tests.Tools;

public sealed class ToolArgumentValidationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"1\"")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("2147483648")]
    public void Rejects_invalid_revit_process_ids(string processIdJson)
    {
        using JsonDocument value = JsonDocument.Parse(processIdJson);
        IDictionary<string, JsonElement> arguments = new Dictionary<string, JsonElement>
        {
            ["revitProcessId"] = value.RootElement.Clone()
        };

        Assert.Throws<ArgumentException>(() => ToolArgumentValidator.Validate(
            arguments,
            allowedKeys: ["revitProcessId"],
            requiredKeys: ["revitProcessId"]));
    }

    [Fact]
    public void Rejects_unexpected_top_level_argument_keys()
    {
        using JsonDocument value = JsonDocument.Parse("1");
        IDictionary<string, JsonElement> arguments = new Dictionary<string, JsonElement>
        {
            ["revitProcessId"] = value.RootElement.Clone(),
            ["unexpected"] = value.RootElement.Clone()
        };

        Assert.Throws<ArgumentException>(() => ToolArgumentValidator.Validate(
            arguments,
            allowedKeys: ["revitProcessId"],
            requiredKeys: ["revitProcessId"]));
    }

    [Fact]
    public void Rejects_unknown_nested_read_request_properties_during_default_json_binding()
    {
        const string json = """
            {"page":{"pageSize":10,"unexpected":1},"includeInPlace":false}
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ListFamiliesRequest>(json));
    }
}
