using System.Security.Cryptography;
using System.Text.Json;
using BIMassist.Mcp.Contracts.Parameters;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Reads;
using BIMassist.Mcp.Contracts.Serialization;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Reads;

namespace BIMassist.Mcp.RevitBridge.Tests.Adapters;

public sealed class RevitReadOperationDispatcherTests
{
    [Fact]
    public void Family_list_is_materialized_serialized_and_revision_bound()
    {
        var record = new FamilyReadRecord(
            new FamilySummary
            {
                UniqueId = "family-uid-1",
                ElementId = 42,
                Name = "Door - Single",
                IsInPlace = false,
                IsEditable = true,
                IsShared = false,
                TypeCount = 1
            },
            [new FamilyTypeSummary { UniqueId = "type-uid-1", ElementId = 43, Name = "900 x 2100" }]);
        var source = new FakeFamilySource(new FamilyReadSnapshot("document-1", "revision-3", [record]));
        byte[] key = SHA256.HashData("BIMassist dispatcher test"u8);
        var familyService = new FamilyReadService(new StablePaginator(new ReadCursorCodec(key)));
        var dispatcher = new RevitReadOperationDispatcher(source, familyService);
        BridgeRequest request = CreateRequest(
            BridgeOperations.ListFamilies,
            "{\"page\":{\"pageSize\":25},\"includeInPlace\":false}");

        ReadOperationResult result = dispatcher.Process(request);
        PageResult<FamilySummary> page = ContractJson.Deserialize<PageResult<FamilySummary>>(result.Result.GetRawText());

        Assert.Equal("revision-3", result.DocumentRevision);
        Assert.Equal("session-1", source.SessionId);
        Assert.Equal("document-1", source.DocumentKey);
        Assert.Equal("family-uid-1", Assert.Single(page.Items).UniqueId);
    }

    [Fact]
    public void Shared_definitions_are_materialized_serialized_and_revision_bound()
    {
        var definition = new SharedDefinitionDescriptor
        {
            SharedGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Name = "Fire Rating",
            DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
            GroupName = "Doors",
            IsVisible = true,
            IsUserModifiable = true
        };
        var familySource = new FakeFamilySource(new FamilyReadSnapshot("document-1", "revision-4", []));
        var sharedSource = new FakeSharedDefinitionSource(
            new SharedDefinitionReadSnapshot("document-1", "revision-4", [definition]));
        var paginator = new StablePaginator(new ReadCursorCodec(SHA256.HashData("shared dispatcher test"u8)));
        var dispatcher = new RevitReadOperationDispatcher(
            familySource,
            new FamilyReadService(paginator),
            sharedSource,
            new SharedDefinitionReadService(paginator));
        BridgeRequest request = CreateRequest(
            BridgeOperations.ListSharedDefinitions,
            "{\"page\":{\"pageSize\":25}}");

        ReadOperationResult result = dispatcher.Process(request);
        PageResult<SharedDefinitionDescriptor> page =
            ContractJson.Deserialize<PageResult<SharedDefinitionDescriptor>>(result.Result.GetRawText());

        Assert.Equal("revision-4", result.DocumentRevision);
        Assert.Equal("session-1", sharedSource.SessionId);
        Assert.Equal("document-1", sharedSource.DocumentKey);
        Assert.Equal(definition.SharedGuid, Assert.Single(page.Items).SharedGuid);
    }

    [Fact]
    public void Project_bindings_are_materialized_serialized_and_revision_bound()
    {
        var binding = new ProjectBindingDescriptor
        {
            Identity = new ParameterIdentity
            {
                Kind = ParameterIdentityKind.ParameterElement,
                StableId = "parameter-element:parameter-uid-1",
                Name = "Comments",
                OwnerContext = "document-1",
                DefinitionId = 42,
                DataTypeId = "autodesk.spec.aec:string.text-2.0.0",
                IsInstance = true
            },
            Definition = new ParameterDefinitionMetadata(
                "Comments",
                "autodesk.spec.aec:string.text-2.0.0",
                "autodesk.parameter.group:identityData-1.0.0",
                null,
                true,
                true),
            Binding = new ParameterBindingMetadata(
                ParameterBindingKind.ProjectInstance,
                ["revit-category:-2000011"],
                null,
                "project")
        };
        var familySource = new FakeFamilySource(new FamilyReadSnapshot("document-1", "revision-5", []));
        var sharedSource = new FakeSharedDefinitionSource(
            new SharedDefinitionReadSnapshot("document-1", "revision-5", []));
        var bindingSource = new FakeProjectBindingSource(
            new ProjectBindingReadSnapshot("document-1", "revision-5", [binding]));
        var paginator = new StablePaginator(new ReadCursorCodec(SHA256.HashData("binding dispatcher test"u8)));
        var dispatcher = new RevitReadOperationDispatcher(
            familySource,
            new FamilyReadService(paginator),
            sharedSource,
            new SharedDefinitionReadService(paginator),
            bindingSource,
            new ProjectBindingReadService(paginator));
        BridgeRequest request = CreateRequest(
            BridgeOperations.ListProjectBindings,
            "{\"page\":{\"pageSize\":25}}");

        ReadOperationResult result = dispatcher.Process(request);
        PageResult<ProjectBindingDescriptor> page =
            ContractJson.Deserialize<PageResult<ProjectBindingDescriptor>>(result.Result.GetRawText());

        Assert.Equal("revision-5", result.DocumentRevision);
        Assert.Equal("session-1", bindingSource.SessionId);
        Assert.Equal("document-1", bindingSource.DocumentKey);
        Assert.Equal("parameter-element:parameter-uid-1", Assert.Single(page.Items).Identity.StableId);
    }

    private static BridgeRequest CreateRequest(string operation, string payload) => new()
    {
        ProtocolVersion = ProtocolVersions.ProtocolVersion,
        SchemaVersion = ProtocolVersions.SchemaVersion,
        RequestId = "request-read-dispatcher",
        SessionId = "session-1",
        DocumentKey = "document-1",
        Operation = operation,
        Payload = JsonDocument.Parse(payload).RootElement.Clone()
    };

    private sealed class FakeProjectBindingSource(ProjectBindingReadSnapshot snapshot) : IRevitProjectBindingReadSource
    {
        public string? SessionId { get; private set; }
        public string? DocumentKey { get; private set; }

        public ProjectBindingReadSnapshot ReadProjectBindings(string sessionId, string documentKey)
        {
            SessionId = sessionId;
            DocumentKey = documentKey;
            return snapshot;
        }
    }

    private sealed class FakeSharedDefinitionSource(SharedDefinitionReadSnapshot snapshot) : IRevitSharedDefinitionReadSource
    {
        public string? SessionId { get; private set; }
        public string? DocumentKey { get; private set; }

        public SharedDefinitionReadSnapshot ReadSharedDefinitions(string sessionId, string documentKey)
        {
            SessionId = sessionId;
            DocumentKey = documentKey;
            return snapshot;
        }
    }

    private sealed class FakeFamilySource(FamilyReadSnapshot snapshot) : IRevitFamilyReadSource
    {
        public string? SessionId { get; private set; }
        public string? DocumentKey { get; private set; }

        public FamilyReadSnapshot ReadFamilies(string sessionId, string documentKey)
        {
            SessionId = sessionId;
            DocumentKey = documentKey;
            return snapshot;
        }
    }
}
