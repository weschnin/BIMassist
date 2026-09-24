using System.Diagnostics;
using System.Security.Principal;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using BIMassist.Mcp.Contracts.Protocol;
using BIMassist.Mcp.Contracts.Sessions;
using BIMassist.Mcp.RevitBridge.Adapters;
using BIMassist.Mcp.RevitBridge.Dispatch;
using BIMassist.Mcp.RevitBridge.Documents;
using BIMassist.Mcp.RevitBridge.Lifecycle;
using BIMassist.Mcp.RevitBridge.Sessions;
using BIMassist.Mcp.RevitBridge.Transport;

namespace BIMassist.Mcp.RevitBridge;

public sealed class App : IExternalApplication
{
    private readonly object _lifecycleSync = new();
    private BridgeRuntime? _runtime;
    private RevitSessionRegistry? _sessions;
    private DocumentRevisionService? _revisions;
    private RevitExternalEventRaiser? _externalEventRaiser;
    private UIControlledApplication? _controlledApplication;
    private int _shuttingDown;

    public Result OnStartup(UIControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        lock (_lifecycleSync)
        {
            if (_runtime is not null)
            {
                return Result.Failed;
            }

            try
            {
                string sessionId = $"session-{Guid.NewGuid():N}";
                var session = new SessionDescriptor
                {
                    SessionId = sessionId,
                    RevitProcessId = Environment.ProcessId,
                    RevitMajor = ProtocolVersions.RevitMajor,
                    BridgeVersion = ProtocolVersions.AddonVersion,
                    ProtocolVersion = ProtocolVersions.ProtocolVersion,
                    SchemaVersion = ProtocolVersions.SchemaVersion,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    Documents = []
                };
                _sessions = new RevitSessionRegistry(session);
                _revisions = new DocumentRevisionService(sessionId);

                var deferredSignal = new DeferredExternalEventSignal();
                var queue = new BridgeRequestQueue(deferredSignal);
                var handler = new BridgeExternalEventHandler(queue, _sessions, _revisions);
                _externalEventRaiser = new RevitExternalEventRaiser(ExternalEvent.Create(handler));
                deferredSignal.Bind(_externalEventRaiser);

                string endpoint = PipeEndpointName.Create(
                    GetCurrentUserSid(),
                    Process.GetCurrentProcess().Id,
                    ProtocolVersions.ProtocolVersion);
                var listener = new NamedPipeListener(endpoint, queue);
                var queueComponent = new BridgeQueueRuntimeComponent(queue, deferredSignal);
                _runtime = new BridgeRuntime([queueComponent, listener]);

                _controlledApplication = application;
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;
                _runtime.StartAsync().GetAwaiter().GetResult();
                return Result.Succeeded;
            }
            catch
            {
                CleanupAfterFailedStartup();
                return Result.Failed;
            }
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        Interlocked.Exchange(ref _shuttingDown, 1);
        lock (_lifecycleSync)
        {
            bool succeeded = true;
            if (_controlledApplication is not null)
            {
                _controlledApplication.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                _controlledApplication = null;
            }

            try
            {
                if (_runtime is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    _runtime.StopAsync(timeout.Token).GetAwaiter().GetResult();
                    _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch
            {
                succeeded = false;
            }
            finally
            {
                _sessions?.BeginShutdown();
                _externalEventRaiser?.Dispose();
                _externalEventRaiser = null;
                _runtime = null;
                _sessions = null;
                _revisions = null;
            }

            return succeeded ? Result.Succeeded : Result.Failed;
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        if (Volatile.Read(ref _shuttingDown) != 0 || _sessions is null || _revisions is null)
        {
            return;
        }

        Document document = args.GetDocument();
        string key = RevitContextSnapshotProvider.CreateDocumentKey(
            document,
            _sessions.GetSnapshot().SessionId);
        _revisions.MarkChanged(key);
    }

    private void CleanupAfterFailedStartup()
    {
        if (_controlledApplication is not null)
        {
            _controlledApplication.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            _controlledApplication = null;
        }

        try
        {
            _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _sessions?.BeginShutdown();
        _externalEventRaiser?.Dispose();
        _externalEventRaiser = null;
        _runtime = null;
        _sessions = null;
        _revisions = null;
    }

    private static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
    }
}
