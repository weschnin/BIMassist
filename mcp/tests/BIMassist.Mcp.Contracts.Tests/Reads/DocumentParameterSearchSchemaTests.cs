using System.Text.Json;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.Contracts.Tests.Schemas;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class DocumentParameterSearchSchemaTests
{
    [Theory]
    [InlineData("""{"page":{"pageSize":5},"nameContains":"Width","includeTypes":false}""", true)]
    [InlineData("""{"page":{"pageSize":5},"nameContains":"Width","includeTypes":true,"categoryId":"revit-category:-2000014"}""", true)]
    [InlineData("""{"page":{"pageSize":5},"nameContains":"Width"}""", false)]
    [InlineData("""{"page":{"pageSize":5},"nameContains":" ","includeTypes":false}""", false)]
    [InlineData("""{"page":{"pageSize":5},"nameContains":"Width","includeTypes":false,"categoryId":"revit-category:+7"}""", false)]
    [InlineData("""{"page":{"pageSize":5},"nameContains":"Width","includeTypes":false,"categoryId":null}""", false)]
    [InlineData("""{"page":{"pageSize":0},"nameContains":"Width","includeTypes":false}""", false)]
    [InlineData("""{"page":{"pageSize":5},"nameContains":"Width","includeTypes":false,"unexpected":true}""", false)]
    public void Search_envelope_schema_and_runtime_agree(string payload, bool valid)
    {
        string json = $$"""{"protocolVersion":"1","schemaVersion":"1","requestId":"req-1","sessionId":"session-1","documentKey":"document-1","operation":"parameter.search","payload":{{payload}}}""";
        Assert.Equal(valid, ContractSchemaFixtureTests.Evaluate("bridge-request.schema.json", json).IsValid);
        if (valid)
            Assert.Equal(BridgeOperations.SearchDocumentParameters, ContractJson.Deserialize<BridgeRequest>(json).Operation);
        else
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Search_schema_and_runtime_reject_oversized_name()
    {
        string json = """{"protocolVersion":"1","schemaVersion":"1","requestId":"req-1","sessionId":"session-1","documentKey":"document-1","operation":"parameter.search","payload":{"page":{"pageSize":5},"nameContains":"X","includeTypes":false}}"""
            .Replace("\"nameContains\":\"X\"", "\"nameContains\":\"" + new string('x', ContractLimits.MaximumTextLength + 1) + "\"", StringComparison.Ordinal);
        Assert.False(ContractSchemaFixtureTests.Evaluate("bridge-request.schema.json", json).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Theory]
    [InlineData("revit-category:9223372036854775807", true)]
    [InlineData("revit-category:-9223372036854775808", true)]
    [InlineData("revit-category:9223372036854775808", false)]
    [InlineData("revit-category:-9223372036854775809", false)]
    [InlineData("revit-category:00", false)]
    [InlineData("revit-category:0", false)]
    public void Category_filter_signed_Int64_limits_match_schema_and_runtime(string categoryId, bool valid)
    {
        string payload = """{"page":{"pageSize":5},"nameContains":"Width","includeTypes":false,"categoryId":"CATEGORY"}"""
            .Replace("CATEGORY", categoryId, StringComparison.Ordinal);
        string json = $$"""{"protocolVersion":"1","schemaVersion":"1","requestId":"req-1","sessionId":"session-1","documentKey":"document-1","operation":"parameter.search","payload":{{payload}}}""";
        Assert.Equal(valid, ContractSchemaFixtureTests.Evaluate("bridge-request.schema.json", json).IsValid);
        if (valid)
            ContractJson.Deserialize<BridgeRequest>(json);
        else
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Fact]
    public void Search_requires_document_and_session_in_both_layers()
    {
        string json = """{"protocolVersion":"1","schemaVersion":"1","requestId":"req-1","operation":"parameter.search","payload":{"page":{"pageSize":5},"nameContains":"Width","includeTypes":false}}""";
        Assert.False(ContractSchemaFixtureTests.Evaluate("bridge-request.schema.json", json).IsValid);
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(json));
    }

    [Theory]
    [InlineData("""{"items":[{"target":{"kind":"element","uniqueId":"element-1","elementId":0},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"double","isReadOnly":false}],"totalCount":1}""", true)]
    [InlineData("""{"items":[{"target":{"kind":"element","uniqueId":"element-1","elementId":-1},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"double","isReadOnly":false}],"totalCount":1}""", false)]
    [InlineData("""{"items":[{"target":{"kind":"element","uniqueId":"element-1"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"double","isReadOnly":false}],"totalCount":1}""", true)]
    [InlineData("""{"items":[{"target":{"kind":"document"},"parameter":{"kind":"sharedGuid","sharedGuid":"e04b78d9-42d1-442b-bd80-6567a955d138","stableId":"guid:1","name":"Width"},"storageType":"string","isReadOnly":true}]}""", true)]
    [InlineData("""{"items":[{"target":{"kind":"element"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"double","isReadOnly":false}]}""", false)]
    [InlineData("""{"items":[{"target":{"kind":"document"},"parameter":{"kind":"builtIn","stableId":"builtin:42","name":"Width"},"storageType":"double","isReadOnly":false}]}""", false)]
    [InlineData("""{"items":[{"target":{"kind":"document"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"invalid","isReadOnly":false}]}""", false)]
    [InlineData("""{"items":[{"target":{"kind":"document"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"double"}]}""", false)]
    [InlineData("""{"items":[{"target":{"kind":"document"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"double","isReadOnly":false,"value":"bad"}]}""", false)]
    [InlineData("""{"items":[null]}""", false)]
    [InlineData("""{"items":[],"nextCursor":null}""", false)]
    [InlineData("""{"items":[{"target":{"kind":"element","uniqueId":"element-1","elementId":null},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"double","isReadOnly":false}]}""", false)]
    [InlineData("""{"items":[{"target":{"kind":"document"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width","ownerContext":null},"storageType":"double","isReadOnly":false}]}""", false)]
    public void Search_result_page_schema_and_runtime_agree(string json, bool valid)
    {
        Assert.Equal(valid, ContractSchemaFixtureTests.Evaluate("parameter-search-result.schema.json", json).IsValid);
        if (valid)
            Assert.NotEmpty(ContractJson.Deserialize<PageResult<DocumentParameterMatch>>(json).Items);
        else
            Assert.Throws<JsonException>(() => ContractJson.Deserialize<PageResult<DocumentParameterMatch>>(json));
    }
}
