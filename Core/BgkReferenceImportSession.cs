using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMassist.Commands;

namespace BIMassist.Core;

/// <summary>The manager queues a captured project; only Execute reads the Revit model.</summary>
internal sealed class BgkReferenceImportSession : IExternalEventHandler
{
    private readonly ExternalEvent externalEvent;
    private readonly UIApplication application;
    private readonly BgkReferenceImportWriter writer = new();
    private readonly Func<UIApplication, IReadOnlyList<Document>, Document?>? selectReference;
    private Action<string>? report;
    private Document? pending;
    private bool executing, closed, disposed, subscribed;

    internal BgkReferenceImportSession(UIApplication application, Action<string> report,
        Func<UIApplication, IReadOnlyList<Document>, Document?>? selectReference = null)
    {
        this.application = application;
        this.selectReference = selectReference;
        this.report = report;
        externalEvent = ExternalEvent.Create(this);
        writer.Report = Notify;
        try { application.Idling += OnIdling; subscribed = true; }
        catch { externalEvent.Dispose(); throw; }
    }

    internal void Queue(Document? target)
    {
        if (closed) return;
        if (pending != null || executing || writer.IsBusy) { Notify("Die vorherige BGK-Übernahme wird noch verarbeitet."); return; }
        if (target == null) { report?.Invoke("Zuerst ein Revit-Projekt öffnen."); return; }
        pending = target;
        try
        {
            var result = externalEvent.Raise();
            if (result is ExternalEventRequest.Accepted or ExternalEventRequest.Pending) return;
            pending = null;
            report?.Invoke("BGK-Übernahme nicht angenommen. Bitte erneut auslösen: " + result);
        }
        catch (Exception ex) { pending = null; report?.Invoke("BGK-Übernahme nicht angenommen: " + ex.Message); }
    }

    public void Execute(UIApplication app)
    {
        if (closed || executing || pending == null) return;
        var target = pending; pending = null; executing = true;
        try
        {
            string message = "";
            LastResult = BgkReferenceImportWorkflow.Run(app, target, writer, ref message, selectReference);
            LastMessage = message;
            if (LastResult != Result.Succeeded) Notify(LastResult == Result.Cancelled ? "BGK-Übernahme abgebrochen." : message);
        }
        finally { executing = false; }
    }

    internal Result LastResult { get; private set; } = Result.Cancelled;
    internal string LastMessage { get; private set; } = "";

    internal Result RunInContext(UIApplication app, Document? target, out string message)
    {
        pending = target;
        Execute(app);
        message = LastMessage;
        return LastResult;
    }

    private void OnIdling(object? sender, IdlingEventArgs args)
    {
        if (executing) return;
        executing = true;
        try { writer.Resolve(); }
        finally
        {
            executing = false; DisposeIfComplete();
            if (disposed && subscribed) { application.Idling -= OnIdling; subscribed = false; }
        }
    }

    internal void Close()
    {
        closed = true; pending = null; report = null; writer.Committed = null;
        DisposeIfComplete();
        // Idling owns recovery and detaches its subscription in valid API context.
    }

    private void DisposeIfComplete()
    {
        if (!closed || disposed || executing || writer.IsBusy) return;
        externalEvent.Dispose(); disposed = true; writer.Report = null;
    }

    private void Notify(string message) { if (closed) return; try { report?.Invoke(message); } catch { } }

    public string GetName() => "BIMassist – BGK aus Referenzprojekt übernehmen";
}