using Autodesk.Revit.UI;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Dispatch;

public sealed class BridgeExternalEventHandler : IExternalEventHandler
{
    private readonly BridgeRequestQueue _queue;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;

    public BridgeExternalEventHandler(
        BridgeRequestQueue queue,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
    }

    public void Execute(UIApplication application)
    {
        var context = new RevitContextSnapshotProvider(application, _sessions, _revisions);
        var processor = new BridgeRequestProcessor(context);
        _queue.ExecuteNext(processor.Process);
    }

    public string GetName() => "BIMassist MCP Bridge Request Dispatcher";
}
