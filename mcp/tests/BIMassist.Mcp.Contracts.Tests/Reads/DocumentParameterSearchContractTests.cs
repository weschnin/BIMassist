using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;

namespace BIMassist.Mcp.Contracts.Tests.Reads;

public sealed class DocumentParameterSearchContractTests
{
    [Fact]
    public void Search_request_and_result_page_roundtrip_with_stable_identity_without_value()
    {
        var request = new SearchDocumentParametersRequest
        {
            Page = new PageRequest { PageSize = 5 },
            NameContains = "Width",
            CategoryId = "revit-category:-2000014",
            IncludeTypes = true
        };
        var result = new PageResult<DocumentParameterMatch>
        {
            Items = [new DocumentParameterMatch
            {
                Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "element-1", ElementId = 42 },
                Parameter = new ParameterIdentity
                {
                    Kind = ParameterIdentityKind.BuiltIn,
                    BuiltInId = -1001301,
                    StableId = "builtin:DOOR_WIDTH",
                    Name = "Width"
                },
                StorageType = ParameterStorageType.Double,
                IsReadOnly = true
            }],
            TotalCount = 1
        };

        Assert.Equal(request, ContractJson.Deserialize<SearchDocumentParametersRequest>(ContractJson.Serialize(request)));
        DocumentParameterMatch match = Assert.Single(ContractJson.Deserialize<PageResult<DocumentParameterMatch>>(ContractJson.Serialize(result)).Items);
        Assert.Equal("element-1", match.Target.UniqueId);
        Assert.Equal("builtin:DOOR_WIDTH", match.Parameter.StableId);
        Assert.DoesNotContain("value", ContractJson.Serialize(match), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_is_document_bound_read_and_validates_operation_payload()
    {
        Assert.Contains(BridgeOperations.SearchDocumentParameters, BridgeOperations.All);
        Assert.Contains(BridgeOperations.SearchDocumentParameters, BridgeOperations.Reads);
        Assert.DoesNotContain(BridgeOperations.SearchDocumentParameters, BridgeOperations.Changes);
        Assert.Contains(BridgeOperations.SearchDocumentParameters, BridgeOperations.RequiresSession);
        Assert.Contains(BridgeOperations.SearchDocumentParameters, BridgeOperations.RequiresDocument);

        BridgeRequest valid = Envelope("""{"page":{"pageSize":10},"nameContains":"Width","includeTypes":false}""");
        Assert.Equal(BridgeOperations.SearchDocumentParameters, ContractJson.Deserialize<BridgeRequest>(ContractJson.Serialize(valid)).Operation);

        foreach (string payload in new[]
        {
            "{}",
            """{"page":{"pageSize":10},"includeTypes":false}""",
            """{"page":{"pageSize":10},"nameContains":"Width"}""",
            """{"page":{"pageSize":0},"nameContains":"Width","includeTypes":false}""",
            """{"page":{"pageSize":10},"nameContains":"Width","includeTypes":false,"unknown":1}""",
            """{"page":{"pageSize":10},"nameContains":"Width","includeTypes":false,"categoryId":"revit-category:+7"}"""
        })
            Assert.Throws<JsonException>(() => ContractValidator.Validate(Envelope(payload)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Standalone_request_rejects_blank_name(string name)
    {
        var request = new SearchDocumentParametersRequest { Page = new PageRequest { PageSize = 1 }, NameContains = name, IncludeTypes = false };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(request));
    }

    [Fact]
    public void Standalone_request_rejects_oversized_name_and_missing_required_properties()
    {
        var request = new SearchDocumentParametersRequest
        {
            Page = new PageRequest { PageSize = 1 },
            NameContains = new string('a', ContractLimits.MaximumTextLength + 1),
            IncludeTypes = false
        };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(request));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<SearchDocumentParametersRequest>("""{"page":{"pageSize":1},"nameContains":"A"}"""));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<SearchDocumentParametersRequest>("""{"page":{"pageSize":1},"nameContains":"A","includeTypes":false,"categoryId":null}"""));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<SearchDocumentParametersRequest>("""{"page":{"pageSize":1},"nameContains":"A","nameContains":"B","includeTypes":false}"""));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<BridgeRequest>(
            """{"protocolVersion":"1","schemaVersion":"1","requestId":"search-1","sessionId":"session-1","documentKey":"document-1","operation":"parameter.search","payload":{"page":{"pageSize":1},"nameContains":"A","nameContains":"B","includeTypes":false}}"""));
    }

    [Fact]
    public void Match_and_page_reject_invalid_nested_identity_storage_and_nulls()
    {
        var match = new DocumentParameterMatch
        {
            Target = new ParameterTarget { Kind = ParameterTargetKind.Element, UniqueId = "element-1" },
            Parameter = new ParameterIdentity { Kind = ParameterIdentityKind.BuiltIn, BuiltInId = -42, StableId = "builtin:42", Name = "Width" },
            StorageType = ParameterStorageType.String,
            IsReadOnly = false
        };
        Assert.Throws<JsonException>(() => ContractJson.Serialize(match with { Parameter = match.Parameter with { BuiltInId = null } }));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(match with { Target = match.Target with { UniqueId = null } }));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(match with { StorageType = (ParameterStorageType)999 }));
        Assert.Throws<JsonException>(() => ContractJson.Serialize(new PageResult<DocumentParameterMatch> { Items = [match with { StorageType = (ParameterStorageType)999 }] }));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<DocumentParameterMatch>("""{"target":{"kind":"document"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"string"}"""));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<DocumentParameterMatch>("""{"target":{"kind":"element","uniqueId":"element-1","elementId":null},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width"},"storageType":"string","isReadOnly":false}"""));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<DocumentParameterMatch>("""{"target":{"kind":"document"},"parameter":{"kind":"builtIn","builtInId":-42,"stableId":"builtin:42","name":"Width","ownerContext":null},"storageType":"string","isReadOnly":false}"""));
    }

    private static BridgeRequest Envelope(string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "search-1",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = BridgeOperations.SearchDocumentParameters,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };
}
