using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;

internal static class Program
{
    private static int passed, failed;
    private static void Main()
    {
        Test("distinct projects reuse exactly one independent settings store", IndependentProjects);
        Test("save requires explicit transaction even for existing store", RequiresTransaction);
        Test("load rejects unsupported entity version", UnsupportedVersion);
        Test("load rejects duplicate project stores", DuplicateStores);
        Test("load rejects incompatible registered schema", InvalidSchema);
        Test("save readback failure rolls back to previous committed settings", ReadbackFailure);
        Test("UI queue snapshots and coalesces latest state; close drains then disposes", QueueAndClose);
        Test("identical persisted state opens no transaction", IdenticalState);
        Test("ordinary raise busy retains latest state across close", BusyRaise);
        Test("Pending event request safely executes latest coalesced state", PendingRaise);
        Test("busy document defers without losing settings", BusyDocument);
        Test("queue during callback survives and does not raise inside callback", QueueDuringCallback);
        Test("bound invalid document fails before transaction or other project access", InvalidDocument);
        Test("write and commit exceptions roll back and report without success", FailedWrite);
        Test("rolled back commit retains previous state and reports", RolledBackWrite);
        Test("pending transaction survives close until Idling terminal readback", PendingTransaction);
        Test("post-commit readback failure never reports saved", PostCommitReadback);
        Test("idle close disposes immediately without model access", IdleClose);
        Test("switching active project still saves only the bound project", SwitchedProject);
        Test("invalid UI state preserves earlier pending snapshot", InvalidQueue);
        Test("state queued during Pending transaction drains after recovery", QueueDuringPending);
        Test("invalid entity is rejected instead of creating replacement history", InvalidEntity);
        Test("closed report sink cannot break save and event cleanup", ThrowingReportSink);
        Console.WriteLine($"{passed} passed; {failed} failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static AvaQuantityViewSettings State(string grouping) => new() { Grouping = grouping };
    private static string Json(AvaQuantityViewSettings state) => AvaQuantityViewSettingsCodec.Serialize(state);
    private static void Save(Document document, AvaQuantityViewSettings state) => Api.Run(() =>
    {
        using var transaction = new Transaction(document, "fixture"); transaction.Start();
        try { AvaQuantityViewSettingsStorage.Save(document, state); transaction.Commit(); }
        catch { transaction.RollBack(); throw; }
    });
    private static AvaQuantityViewSettings? Load(Document document)
    {
        AvaQuantityViewSettings? result = null; Api.Run(() => result = AvaQuantityViewSettingsStorage.Load(document)); return result;
    }
    private static void IndependentProjects()
    {
        var first = new Document(); var second = new Document();
        Equal<AvaQuantityViewSettings?>(null, Load(first)); Equal(0, first.Stores.Count);
        Save(first, State("first")); Save(second, State("second"));
        var originalStore = first.Stores.Single();
        Save(first, State("latest"));
        Equal("latest", Load(first)!.Grouping); Equal("second", Load(second)!.Grouping);
        Equal(1, first.Stores.Count); Equal(1, second.Stores.Count);
        Equal(originalStore, first.Stores.Single());
    }
    private static void RequiresTransaction()
    {
        var document = new Document(); Save(document, State("old"));
        Api.Run(() => Reject<InvalidOperationException>(() => AvaQuantityViewSettingsStorage.Save(document, State("new"))));
        Equal("old", Load(document)!.Grouping);
    }
    private static void UnsupportedVersion()
    {
        var document = new Document(); Save(document, State("old"));
        document.Stores.Single().Entities[SchemaGuid].Values["SchemaVersion"] = 2;
        Reject<InvalidDataException>(() => Load(document));
    }
    private static readonly Guid SchemaGuid = new("dcd81d44-26cc-4abd-a9da-94f234406470");
    private static void DuplicateStores()
    {
        var document = new Document(); Save(document, State("old"));
        Api.Run(() =>
        {
            using var transaction = new Transaction(document, "duplicate fixture"); transaction.Start();
            DataStorage.Create(document).SetEntity(document.Stores[0].GetEntity(Schema.Lookup(SchemaGuid)!)); transaction.Commit();
        });
        Reject<InvalidDataException>(() => Load(document));
        Reject<InvalidDataException>(() => Save(document, State("new")));
        Equal(2, document.Stores.Count);
    }
    private static void InvalidSchema()
    {
        var document = new Document();
        Api.Run(() =>
        {
            var builder = new SchemaBuilder(SchemaGuid); builder.SetSchemaName("Unrelated");
            builder.AddSimpleField("Json", typeof(int)); builder.Finish();
        });
        Reject<InvalidDataException>(() => Load(document));
        Reject<InvalidDataException>(() => Save(document, State("new")));
        Equal(0, document.Stores.Count);
    }
    private static void ReadbackFailure()
    {
        var document = new Document(); Save(document, State("old")); document.CorruptSet = true;
        Reject<InvalidDataException>(() => Save(document, State("new")));
        Equal("old", Load(document)!.Grouping);
    }
    private static void QueueAndClose()
    {
        var document = new Document(); var reports = new List<string>();
        AvaQuantityViewSettingsSession? session = null;
        Api.Run(() => session = new AvaQuantityViewSettingsSession(document, reports.Add));
        var signal = ExternalEvent.Last!;
        Equal(0, document.TransactionStarts); Equal(0, document.Stores.Count);
        session!.Queue(State("first"));
        var columns = new List<AvaQuantityColumnSetting> { new("Result", 0, true, 80) };
        session.Queue(State("latest") with { Columns = columns }); columns.Clear();
        Equal(0, document.TransactionStarts); Equal(0, document.Stores.Count); Equal(1, signal.RaiseCount);
        session.Close(); Check(!signal.Disposed, "Close discarded queued work");
        signal.Fire(new UIApplication(document.App));
        Equal("latest", Load(document)!.Grouping); Equal(1, Load(document)!.Columns.Count);
        Equal(1, document.TransactionStarts); Check(signal.Disposed, "Event retained after close/save completion");
        Check(document.ForcedModal, "Failure handling was not forced modal");
        Check(reports.Any(r => r.Contains("gespeichert")), "Committed readback did not report success");
        session.Queue(State("rejected")); Check(reports.Last().Contains("geschlossen"), "Queue after Close was not reported");
        document.App.Idle();
    }
    private static (AvaQuantityViewSettingsSession Session, ExternalEvent Signal, List<string> Reports) Open(Document document)
    {
        AvaQuantityViewSettingsSession? session = null; var reports = new List<string>();
        Api.Run(() => session = new AvaQuantityViewSettingsSession(document, reports.Add));
        return (session!, ExternalEvent.Last!, reports);
    }
    private static void IdenticalState()
    {
        var document = new Document(); Save(document, State("old")); var starts = document.TransactionStarts;
        var (session, signal, _) = Open(document); session.Queue(State("old"));
        signal.Fire(new UIApplication(document.App)); Equal(starts, document.TransactionStarts);
        session.Queue(State("new")); document.App.Idle(); signal.Fire(new UIApplication(document.App));
        session.Queue(State("new")); document.App.Idle(); signal.Fire(new UIApplication(document.App));
        Equal(starts + 1, document.TransactionStarts); session.Close(); document.App.Idle();
    }
    private static void BusyRaise()
    {
        foreach (var request in new[] { ExternalEventRequest.Denied, ExternalEventRequest.TimedOut })
        {
            var document = new Document(); var (session, signal, reports) = Open(document);
            ExternalEvent.NextRaise = request; session.Queue(State("latest")); session.Close();
            Check(!signal.Disposed, "Busy raise discarded closed session");
            document.App.Idle(); Check(signal.Scheduled, "No retry after ordinary busy raise");
            signal.Fire(new UIApplication(document.App)); Equal("latest", Load(document)!.Grouping);
            Check(signal.Disposed, "Busy close did not finish"); Check(reports.Count > 0, "No status reported");
        }
    }
    private static void PendingRaise()
    {
        var document = new Document(); var (session, signal, _) = Open(document);
        ExternalEvent.NextRaise = ExternalEventRequest.Pending;
        session.Queue(State("one")); session.Queue(State("two"));
        Equal(1, signal.RaiseCount); signal.Fire(new UIApplication(document.App));
        Equal("two", Load(document)!.Grouping); session.Close(); document.App.Idle();
    }
    private static void BusyDocument()
    {
        var document = new Document(); var (session, signal, _) = Open(document);
        document.Modifiable = true; session.Queue(State("one")); signal.Fire(new UIApplication(document.App));
        Equal(0, document.TransactionStarts); Equal(0, document.Stores.Count);
        session.Queue(State("latest")); session.Close(); document.Modifiable = false;
        document.App.Idle(); signal.Fire(new UIApplication(document.App));
        Equal("latest", Load(document)!.Grouping); Check(signal.Disposed, "Deferred document did not finish");
    }
    private static void QueueDuringCallback()
    {
        var document = new Document(); var (session, signal, _) = Open(document);
        document.DuringCommit = () =>
        {
            document.DuringCommit = null;
            // Simulate a modeless/reentrant UI notification during a callback.
            var old = Api.InContext; Api.InContext = false;
            try { session.Queue(State("latest")); session.Close(); } finally { Api.InContext = old; }
            Equal(1, signal.RaiseCount); Check(!signal.Disposed, "Closed an executing event");
        };
        session.Queue(State("first")); signal.Fire(new UIApplication(document.App));
        // A new signal is issued from Idling, not from the active Execute callback.
        Equal(1, signal.RaiseCount); document.App.Idle(); Equal(2, signal.RaiseCount);
        signal.Fire(new UIApplication(document.App)); Equal("latest", Load(document)!.Grouping);
        Equal(2, document.TransactionStarts); Check(signal.Disposed, "Follow-up callback not drained");
    }
    private static void InvalidDocument()
    {
        var document = new Document(); var other = new Document(); var (session, signal, reports) = Open(document);
        session.Queue(State("invalid")); session.Close(); document.Valid = false;
        signal.Fire(new UIApplication(other.App) { ActiveDocument = other });
        Equal(0, document.TransactionStarts); Equal(0, other.TransactionStarts); Equal(0, other.Stores.Count);
        Check(reports.Any(r => r.Contains("geschlossen") || r.Contains("ungültig")), "Missing invalid-document error");
        Check(!reports.Any(r => r.Contains("gespeichert")), "Invalid document reported saved");
        Check(signal.Disposed, "Invalid-document failure did not release closed event");
    }
    private static void FailedWrite()
    {
        foreach (var commitFailure in new[] { false, true })
        {
            var document = new Document(); Save(document, State("old")); var (session, signal, reports) = Open(document);
            document.ThrowSet = !commitFailure; document.ThrowCommit = commitFailure;
            session.Queue(State("new")); session.Close(); signal.Fire(new UIApplication(document.App));
            Equal("old", Load(document)!.Grouping); Check(!document.Modifiable, "Failed write left an open transaction");
            Check(document.LastTransaction!.Disposed, "Terminal failed transaction not disposed");
            Check(reports.Count > 0 && !reports.Any(r => r.Contains("gespeichert")), "Failed write reported saved");
            Check(signal.Disposed, "Failed closed session was not disposed");
        }
    }
    private static void RolledBackWrite()
    {
        var document = new Document(); Save(document, State("old")); var (session, signal, reports) = Open(document);
        document.CommitResult = TransactionStatus.RolledBack;
        session.Queue(State("new")); session.Close(); signal.Fire(new UIApplication(document.App));
        Equal("old", Load(document)!.Grouping); Check(signal.Disposed, "Rolled-back event not disposed");
        Check(reports.Count > 0 && !reports.Any(r => r.Contains("gespeichert")), "Rollback reported saved");
    }
    private static void PendingTransaction()
    {
        foreach (var terminal in new[] { TransactionStatus.Committed, TransactionStatus.RolledBack })
        {
            var document = new Document(); Save(document, State("old")); var (session, signal, reports) = Open(document);
            document.CommitResult = TransactionStatus.Pending;
            session.Queue(State("new")); session.Close(); signal.Fire(new UIApplication(document.App));
            Check(!signal.Disposed, "Disposed event while transaction Pending");
            Check(!document.LastTransaction!.Disposed, "Disposed Pending transaction");
            Check(!reports.Any(r => r.Contains("gespeichert")), "Pending reported saved");
            document.App.Idle(); Check(!document.LastTransaction.Disposed, "Idling disposed unresolved transaction");
            Api.Run(() => document.LastTransaction.CompletePending(terminal)); document.App.Idle();
            Equal(terminal == TransactionStatus.Committed ? "new" : "old", Load(document)!.Grouping);
            Equal(terminal == TransactionStatus.Committed, reports.Any(r => r.Contains("gespeichert")));
            Check(signal.Disposed && document.LastTransaction.Disposed, "Terminal pending cleanup incomplete");
        }
    }
    private static void PostCommitReadback()
    {
        var document = new Document(); Save(document, State("old")); var (session, signal, reports) = Open(document);
        document.CorruptAfterCommit = true; session.Queue(State("new")); session.Close(); signal.Fire(new UIApplication(document.App));
        Check(!reports.Any(r => r.Contains("gespeichert")), "Unverified commit reported saved");
        Check(reports.Any(r => r.Contains("nicht bestätigt")), "No postcommit uncertainty reported");
        Check(signal.Disposed, "Postcommit uncertainty leaked the event");
    }
    private static void IdleClose()
    {
        var document = new Document(); var (session, signal, _) = Open(document);
        session.Close(); session.Close(); Check(signal.Disposed, "Idle close retained event");
        Equal(0, document.TransactionStarts); document.App.Idle();
    }
    private static void SwitchedProject()
    {
        var bound = new Document(); var active = new Document(); Save(active, State("other"));
        var (session, signal, _) = Open(bound); session.Queue(State("bound")); session.Close();
        signal.Fire(new UIApplication(active.App) { ActiveDocument = active });
        Equal("bound", Load(bound)!.Grouping); Equal("other", Load(active)!.Grouping);
        Equal(1, bound.TransactionStarts); Equal(1, active.TransactionStarts); Check(signal.Disposed, "Switched-project cleanup failed");
    }
    private static void InvalidQueue()
    {
        var document = new Document(); var (session, signal, _) = Open(document);
        session.Queue(State("valid"));
        Reject<InvalidDataException>(() => session.Queue(State("invalid") with { SchemaVersion = 2 }));
        Equal(0, document.TransactionStarts); Equal(1, signal.RaiseCount);
        session.Close(); signal.Fire(new UIApplication(document.App)); Equal("valid", Load(document)!.Grouping);
    }
    private static void QueueDuringPending()
    {
        var document = new Document(); var (session, signal, _) = Open(document);
        document.CommitResult = TransactionStatus.Pending;
        session.Queue(State("first")); signal.Fire(new UIApplication(document.App));
        var firstTransaction = document.LastTransaction!;
        session.Queue(State("second")); session.Queue(State("latest")); session.Close();
        Equal(1, signal.RaiseCount); Check(!signal.Disposed, "Lost queue during pending transaction");
        Api.Run(() => firstTransaction.CompletePending(TransactionStatus.Committed));
        document.App.Idle(); Check(firstTransaction.Disposed, "First transaction not released before next write");
        Equal(2, signal.RaiseCount); document.CommitResult = TransactionStatus.Committed;
        signal.Fire(new UIApplication(document.App));
        Equal("latest", Load(document)!.Grouping); Equal(2, document.TransactionStarts); Equal(1, document.Stores.Count);
        Check(signal.Disposed, "Pending follow-up event was not released");
    }
    private static void InvalidEntity()
    {
        var document = new Document(); Save(document, State("old"));
        document.Stores.Single().Entities[SchemaGuid].Valid = false;
        Reject<InvalidDataException>(() => Load(document)); Reject<InvalidDataException>(() => Save(document, State("new")));
        Equal(1, document.Stores.Count);
    }
    private static void ThrowingReportSink()
    {
        var document = new Document(); AvaQuantityViewSettingsSession? session = null;
        Api.Run(() => session = new AvaQuantityViewSettingsSession(document, _ => throw new ObjectDisposedException("window")));
        var signal = ExternalEvent.Last!; session!.Queue(State("new")); session.Close();
        signal.Fire(new UIApplication(document.App)); Equal("new", Load(document)!.Grouping);
        Check(signal.Disposed && document.LastTransaction!.Disposed, "Report sink broke transaction/event cleanup");
    }
    internal static void Test(string name, Action body)
    {
        Schema.Registry.Clear(); ExternalEvent.Last = null; ExternalEvent.NextRaise = ExternalEventRequest.Accepted;
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected '{expected}'; actual '{actual}'");
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
    }
}
