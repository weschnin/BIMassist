using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;

internal static class SessionTests
{
    internal static void Run()
    {
        var target = new Document(); target.Symbols.Add(new FamilySymbol());
        var reference = new Document(); reference.Symbols.Add(new FamilySymbol { Bgk = new Parameter { Value = "09-25" } });
        var app = new UIApplication { ActiveUIDocument = new(target) };
        app.Application.Documents.AddRange([target, reference]);
        TaskDialog.Reset();
        var messages = new List<string>();
        var session = new BgkReferenceImportSession(app, messages.Add, (_, _) => reference);
        var externalEvent = ExternalEvent.Last!;
        foreach (var signal in new[] { ExternalEventRequest.Accepted, ExternalEventRequest.Pending })
        {
            ExternalEvent.NextRaise = signal;
            ApiContext.Allowed = false; session.Queue(target); session.Queue(reference); ApiContext.Allowed = true;
            if (target.Symbols[0].Bgk!.SetCount != (signal == ExternalEventRequest.Accepted ? 0 : 1)) throw new Exception("Queue touched model before Execute");
            externalEvent.Fire(app);
        }
        if (target.Symbols[0].Bgk!.SetCount != 2 || reference.Symbols[0].Bgk!.SetCount != 0 || externalEvent.Disposed)
            throw new Exception("Manager import must be repeatable and never replace the first queued target");
        Console.WriteLine("PASS BGK Manager API-free queue, duplicate guard, Accepted/Pending and repeatable imports");

        ApiContext.Allowed = false; session.Close(); ApiContext.Allowed = true;
        if (!externalEvent.Disposed) throw new Exception("Closing an idle BGK session must dispose its paired ExternalEvent immediately");
        app.Idle();
        if (app.IdlingSubscribers != 0) throw new Exception("Closed BGK session retained its Idling subscription");
        Console.WriteLine("PASS BGK Manager closes idle events and detaches Idling in API context");
        ExternalEvent.NextRaise = ExternalEventRequest.Accepted;
        target.CommitResult = TransactionStatus.Pending; target.IsModifiable = false; TaskDialog.Reset(); messages.Clear();
        session = new BgkReferenceImportSession(app, messages.Add, (_, _) => reference); externalEvent = ExternalEvent.Last!;
        ApiContext.Allowed = false; session.Queue(target); ApiContext.Allowed = true; externalEvent.Fire(app);
        if (TaskDialog.Shown.Contains("BGK-Übernahme abgeschlossen")) throw new Exception("Pending callback showed early success");
        ApiContext.Allowed = false; session.Close(); ApiContext.Allowed = true;
        int reportsBeforeRecovery = messages.Count;
        app.Idle();
        if (externalEvent.Disposed || app.IdlingSubscribers != 1) throw new Exception("Close disposed the event before pending transaction recovery");
        target.LastTransaction!.Status = TransactionStatus.Committed; target.IsModifiable = false; app.Idle();
        if (!externalEvent.Disposed || app.IdlingSubscribers != 0 || messages.Count != reportsBeforeRecovery || TaskDialog.Shown.Contains("BGK-Übernahme abgeschlossen"))
            throw new Exception("Closed pending recovery must clean up without retaining UI or showing stale success");
        Console.WriteLine("PASS BGK Manager close retains pending recovery and suppresses stale UI callbacks");

        foreach (var signal in new[] { ExternalEventRequest.Denied, ExternalEventRequest.TimedOut })
        {
            target.CommitResult = TransactionStatus.Committed; TaskDialog.Reset();
            session = new BgkReferenceImportSession(app, messages.Add, (_, _) => reference); externalEvent = ExternalEvent.Last!;
            ExternalEvent.NextRaise = signal;
            ApiContext.Allowed = false; session.Queue(target); ApiContext.Allowed = true;
            int writes = target.Symbols[0].Bgk!.SetCount;
            ExternalEvent.NextRaise = ExternalEventRequest.Accepted;
            ApiContext.Allowed = false; session.Queue(target); ApiContext.Allowed = true; externalEvent.Fire(app);
            if (target.Symbols[0].Bgk!.SetCount != writes + 1) throw new Exception("Denied/TimedOut event must release the request for an explicit retry");
            session.Close(); app.Idle();
        }
        Console.WriteLine("PASS BGK Manager Denied/TimedOut requests allow explicit retry");
    }
}