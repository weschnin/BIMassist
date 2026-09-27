using System.Security.Cryptography;
using Autodesk.Revit.UI;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Reads;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Dispatch;

public sealed class BridgeExternalEventHandler : IExternalEventHandler
{
    private readonly BridgeRequestQueue _queue;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;
    private readonly StablePaginator _paginator;
    private readonly MetadataSnapshotService _metadataSnapshotService;

    public BridgeExternalEventHandler(
        BridgeRequestQueue queue,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        var cursorCodec = new ReadCursorCodec(RandomNumberGenerator.GetBytes(32));
        _paginator = new StablePaginator(cursorCodec);
        _metadataSnapshotService = new MetadataSnapshotService(cursorCodec);
    }

    public void Execute(UIApplication application)
    {
        var context = new RevitContextSnapshotProvider(application, _sessions, _revisions);
        var familySource = new RevitFamilyReadSource(application, _sessions, _revisions);
        var familyService = new FamilyReadService(_paginator);
        var sharedDefinitionSource = new RevitSharedDefinitionReadSource(application, _sessions, _revisions);
        var sharedDefinitionService = new SharedDefinitionReadService(_paginator);
        var projectBindingSource = new RevitProjectBindingReadSource(application, _sessions, _revisions);
        var projectBindingService = new ProjectBindingReadService(_paginator);
        var parameterSource = new RevitParameterReadSource(application, _sessions, _revisions);
        var parameterService = new ParameterReadService(_paginator);
        var parameterValueService = new ParameterValueReadService(_paginator);
        var readDispatcher = new RevitReadOperationDispatcher(
            familySource,
            familyService,
            sharedDefinitionSource,
            sharedDefinitionService,
            projectBindingSource,
            projectBindingService,
            parameterSource,
            parameterService,
            parameterValueService,
            _metadataSnapshotService,
            _revisions.GetCurrent);
        var processor = new BridgeRequestProcessor(context, readDispatcher);
        _queue.ExecuteNext(processor.Process);
    }

    public string GetName() => "BIMassist MCP Bridge Request Dispatcher";
}
