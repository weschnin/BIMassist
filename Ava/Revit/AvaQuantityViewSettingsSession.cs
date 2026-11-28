using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

/// <summary>Owns a single project-bound event. Only Execute/Idling access the model.</summary>
internal sealed class AvaQuantityViewSettingsSession : IExternalEventHandler
{
    internal const string SavedMessage = "Ansichtseinstellungen im gebundenen Revit-Projekt gespeichert.";
    private readonly object gate = new();
    private readonly Document document;
    private readonly Action<string> report;
    private readonly ExternalEvent externalEvent;
    private readonly UIApplication application;
    private string? pending, saving, lastReport;
    private bool signalled, executing, closed, disposed, deferSignal, subscribed;
    private Transaction? transaction;
    private bool commitAccepted;

    public AvaQuantityViewSettingsSession(Document document, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(report);
        if (!document.IsValidObject) throw new InvalidOperationException("Das gebundene Revit-Projekt ist geschlossen oder ungültig.");
        if (document.IsFamilyDocument) throw new InvalidOperationException("Ansichtseinstellungen benötigen ein Revit-Projekt, keine Familie.");
        this.document = document;
        this.report = report;
        application = new UIApplication(document.Application);
        externalEvent = ExternalEvent.Create(this);
        try
        {
            // Subscribe before any transaction can start, including unexpected Pending recovery.
            application.Idling += OnIdling;
            subscribed = true;
        }
        catch { externalEvent.Dispose(); throw; }
    }

    /// <summary>UI-thread safe: validation/snapshot plus scheduling, never document access.</summary>
    public void Queue(AvaQuantityViewSettings settings)
    {
        lock (gate)
        {
            if (closed) { Report("Die Einstellungssitzung ist geschlossen; die Änderung wurde nicht gespeichert."); return; }
            // Codec validation and a JSON snapshot sever all caller-owned collection references.
            pending = AvaQuantityViewSettingsCodec.Serialize(AvaQuantityViewSettingsCodec.Deserialize(AvaQuantityViewSettingsCodec.Serialize(settings)));
            Signal();
        }
    }

    public void Close()
    {
        lock (gate) { closed = true; DisposeIfComplete(); }
    }

    public void Execute(UIApplication app)
    {
        string? state;
        lock (gate)
        {
            signalled = false;
            // A second signal may be consumed during a nested Revit event loop. The Idling
            // owner below must remain responsible for the pending snapshot in that case.
            if (disposed || executing || transaction != null) return;
            state = pending; pending = null; executing = true; deferSignal = true;
        }
        try
        {
            if (state == null) return;
            if (!document.IsValidObject)
                throw new InvalidOperationException("Das gebundene Revit-Projekt ist geschlossen oder ungültig.");
            if (document.IsModifiable)
            {
                // An ordinary busy document is not a failed save. A newer queued state wins.
                lock (gate) { pending ??= state; }
                Report("Revit ist beschäftigt; die Ansichtseinstellungen bleiben zur Übernahme vorgemerkt.");
                return;
            }
            if (document.IsReadOnly) throw new InvalidOperationException("Das gebundene Revit-Projekt ist schreibgeschützt.");
            var previous = AvaQuantityViewSettingsStorage.Load(document);
            if (previous != null && string.Equals(AvaQuantityViewSettingsCodec.Serialize(previous), state, StringComparison.Ordinal)) return;

            saving = state;
            commitAccepted = false;
            transaction = new Transaction(document, "AVA – Mengenermittlungsansicht speichern");
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Die Revit-Transaktion konnte nicht gestartet werden.");
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetForcedModalHandling(true));
            AvaQuantityViewSettingsStorage.Save(document, AvaQuantityViewSettingsCodec.Deserialize(state));
            var result = transaction.Commit();
            commitAccepted = result is TransactionStatus.Committed or TransactionStatus.Pending;
            if (!commitAccepted) throw new InvalidOperationException("Revit hat die Ansichtseinstellungen nicht übernommen.");
        }
        catch (Exception ex)
        {
            commitAccepted = false;
            Report("Ansichtseinstellungen nicht bestätigt: " + ex.Message);
        }
        finally
        {
            ResolveTransaction();
            lock (gate)
            {
                executing = false;
                // Never re-raise while Execute is still on Revit's callback stack. Idling
                // retries both busy Raise outcomes and a snapshot queued during this save.
                DisposeIfComplete();
                UnsubscribeIfDisposed();
            }
        }
    }

    private void OnIdling(object? sender, IdlingEventArgs args)
    {
        lock (gate)
        {
            if (executing) return;
            executing = true;
        }
        try { ResolveTransaction(); }
        finally
        {
            lock (gate)
            {
                executing = false; deferSignal = false;
                Signal(); DisposeIfComplete(); UnsubscribeIfDisposed();
            }
        }
    }

    private void ResolveTransaction()
    {
        var current = transaction;
        if (current == null) return;
        var recovery = X31ReviewTransactionRecovery.Resolve(current.GetStatus, current.RollBack,
            TransactionStatus.Started, TransactionStatus.Pending,
            status => status is TransactionStatus.Committed or TransactionStatus.RolledBack or TransactionStatus.Uninitialized);
        if (!recovery.CanDispose)
        {
            Report("Revit-Transaktion noch nicht bestätigt; die Ereignissitzung bleibt zur Wiederherstellung erhalten."
                + (recovery.Error == null ? "" : " " + recovery.Error.Message));
            return;
        }

        string outcome;
        try
        {
            if (recovery.Status != TransactionStatus.Committed || !commitAccepted)
                outcome = "Ansichtseinstellungen nicht bestätigt: Revit hat die Änderung nicht übernommen.";
            else
            {
                var readback = AvaQuantityViewSettingsStorage.Load(document);
                if (readback == null || !string.Equals(AvaQuantityViewSettingsCodec.Serialize(readback), saving, StringComparison.Ordinal))
                    throw new InvalidOperationException("Die Rückleseprüfung nach Commit ist fehlgeschlagen.");
                outcome = SavedMessage;
            }
        }
        catch (Exception ex) { outcome = "Ansichtseinstellungen nicht bestätigt: " + ex.Message; }
        try { current.Dispose(); }
        catch (Exception ex)
        {
            commitAccepted = false;
            Report("Transaktionsbereinigung nicht bestätigt: " + ex.Message);
            return;
        }
        transaction = null; saving = null; commitAccepted = false;
        Report(outcome);
    }

    private void UnsubscribeIfDisposed()
    {
        // Only API callbacks detach the Revit event. UI Close may dispose an idle
        // ExternalEvent immediately; its subscription is detached on the next Idling.
        if (!disposed || !subscribed) return;
        try { application.Idling -= OnIdling; subscribed = false; }
        catch (Exception ex) { Report("Ereignisbereinigung nicht bestätigt: " + ex.Message); }
    }

    public string GetName() => "AVA – projektgebundene Ansichtseinstellungen";

    private void Signal()
    {
        if (pending == null || executing || deferSignal || transaction != null || signalled || disposed) return;
        try
        {
            var result = externalEvent.Raise();
            signalled = result is ExternalEventRequest.Accepted or ExternalEventRequest.Pending;
            if (!signalled) Report("Revit-Ereignis beschäftigt; die letzte Ansichtseinstellung bleibt zur Wiederholung vorgemerkt (" + result + ").");
        }
        catch (Exception ex) { Report("Revit-Ereignis nicht bestätigt; die Änderung bleibt vorgemerkt: " + ex.Message); }
    }

    private void DisposeIfComplete()
    {
        if (!closed || disposed || executing || transaction != null || pending != null) return;
        try { externalEvent.Dispose(); disposed = true; }
        catch (Exception ex) { Report("Revit-Ereignisbereinigung nicht bestätigt: " + ex.Message); }
    }

    private void Report(string message)
    {
        // A closed window/report sink must never break transaction cleanup or scheduling.
        if (string.Equals(lastReport, message, StringComparison.Ordinal)) return;
        lastReport = message;
        try { report(message); } catch { }
    }
}
