using System.Diagnostics;
using System.Security.Cryptography;
using Autodesk.Revit.UI;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Changes;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Reads;
using BIMassist.Mcp.RevitBridge.Sessions;

namespace BIMassist.Mcp.RevitBridge.Dispatch;

public sealed class BridgeExternalEventHandler : IExternalEventHandler
{
    private readonly BridgeRequestQueue _queue;
    private readonly RevitSessionRegistry _sessions;
    private readonly DocumentRevisionService _revisions;
    private readonly ChangePlanRegistry _plans;
    // Separate process-lifetime PLAN registry: the existing single-write Apply never sees it.
    private readonly ChangePlanRegistry _batchPlans;
    private readonly StablePaginator _paginator;
    private readonly MetadataSnapshotService _metadataSnapshotService;

    public BridgeExternalEventHandler(
        BridgeRequestQueue queue,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions)
        : this(queue, sessions, revisions, new ChangePlanRegistry())
    {
    }

    internal BridgeExternalEventHandler(
        BridgeRequestQueue queue,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions,
        ChangePlanRegistry plans)
        : this(queue, sessions, revisions, plans, new ChangePlanRegistry())
    {
    }

    internal BridgeExternalEventHandler(
        BridgeRequestQueue queue,
        RevitSessionRegistry sessions,
        DocumentRevisionService revisions,
        ChangePlanRegistry plans,
        ChangePlanRegistry batchPlans)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _batchPlans = batchPlans ?? throw new ArgumentNullException(nameof(batchPlans));
        var cursorCodec = new ReadCursorCodec(RandomNumberGenerator.GetBytes(32));
        _paginator = new StablePaginator(cursorCodec);
        _metadataSnapshotService = new MetadataSnapshotService(cursorCodec);
    }

    public void Execute(UIApplication application)
    {
        long callbackStarted = Stopwatch.GetTimestamp();
        var context = new RevitContextSnapshotProvider(application, _sessions, _revisions);
        var familySource = new RevitFamilyReadSource(application, _sessions, _revisions);
        var familyService = new FamilyReadService(_paginator);
        var elementSource = new RevitElementReadSource(application, _sessions, _revisions);
        var elementService = new ElementReadService(_paginator);
        var documentParameterSearchSource = new RevitDocumentParameterSearchSource(application, _sessions, _revisions);
        var documentParameterSearchService = new DocumentParameterSearchService(_paginator);
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
            _revisions.GetCurrent,
            elementSource,
            elementService,
            documentParameterSearchSource,
            documentParameterSearchService);
        var planSource = new RevitSetParameterPlanSource(application, _sessions, _revisions, parameterSource);
        var planDispatcher = new SetParameterPlanDispatcher(planSource, _plans, () => DateTimeOffset.UtcNow);
        var batchDispatcher = new BatchSetParameterPlanDispatcher(
            (payload, sessionId, documentKey, expectedRevision, now) =>
                planSource.BuildBatchPlan(payload, sessionId, documentKey, expectedRevision, now),
            _batchPlans, () => DateTimeOffset.UtcNow);
        var applyDispatcher = new RevitStringChangePlanApply(application, _sessions, _revisions, _plans);
        var processor = new BridgeRequestProcessor(context, readDispatcher, planDispatcher, applyDispatcher, batchDispatcher);
        _queue.ExecuteNextWithContext(queued => processor.Process(queued.Request, queued.CancellationToken, callbackStarted));
    }

    public string GetName() => "BIMassist MCP Bridge Request Dispatcher";
}
