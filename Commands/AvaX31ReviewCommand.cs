using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;
using BIMassist.Ava.Views;
using BIMassist.Views;

namespace BIMassist.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class AvaX31ReviewCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uiapp = commandData.Application;
            var uiDocument = uiapp.ActiveUIDocument;
            if (uiDocument == null) throw new InvalidOperationException("Zuerst ein Revit-Projekt öffnen.");
            if (uiDocument.Document.IsFamilyDocument) throw new InvalidOperationException("Die X31-Prüfung steht nur in einem Revit-Projekt zur Verfügung.");

            var dialog = AvaFileDialogs.OpenX31();
            if (dialog.ShowDialog() != true) return Result.Cancelled;
            var review = X31ReviewReader.ReadFile(dialog.FileName);
            var missingIds = X31ReviewReader.FindMissingElementIds(review, text =>
                long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                && uiDocument.Document.GetElement(new ElementId(value)) != null);
            if (missingIds.Count > 0)
            {
                var shown = string.Join(", ", missingIds.Take(100));
                if (missingIds.Count > 100) shown += $", … und {missingIds.Count - 100} weitere";
                TaskDialog.Show("AVA – X31 prüfen", $"Die X31-Mengenermittlung enthält ElementIDs, die im aktiven Revit-Modell nicht vorhanden sind. Die Prüfansicht wird nicht geladen.\n\nFehlende ElementIDs ({missingIds.Count}):\n{shown}");
                return Result.Cancelled;
            }
            var includedIds = review.Rows.SelectMany(row => row.ElementIdsForSelection)
                .Select(id => long.Parse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
                .ToHashSet();
            var handler = new X31ReviewExternalEventHandler(uiDocument.Document, includedIds);
            var externalEvent = ExternalEvent.Create(handler);
            void Post(Action setRequest)
            {
                try
                {
                    setRequest();
                    if (externalEvent.Raise() != ExternalEventRequest.Accepted)
                    {
                        handler.CancelRequest();
                        handler.Window?.ShowStatus("Revit hat die Anforderung nicht angenommen. Bitte erneut versuchen.");
                    }
                }
                catch (Exception ex) { handler.Window?.ShowStatus("Die Anforderung konnte nicht gesendet werden: " + ex.Message); }
            }
            var window = new X31ReviewWindow(review,
                (plan, isolate) => Post(() => handler.SetRequest(plan, isolate)),
                isolate => Post(() => handler.SetMissingRequest(isolate)));
            handler.Window = window;
            window.Closed += (_, _) =>
            {
                handler.Window = null;
                externalEvent.Dispose();
            };
            WpfOwner.ShowModeless(window, uiapp);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = "Die X31-Prüfung konnte nicht geöffnet werden.\n\n" + ex.Message;
            TaskDialog.Show("AVA – X31 prüfen", message);
            return Result.Failed;
        }
    }

    private sealed class X31ReviewExternalEventHandler(Document sourceDocument, IReadOnlySet<long> includedIds) : IExternalEventHandler
    {
        private X31SelectionPlan? request;
        private bool isolate;
        private bool missingRequest;
        private Transaction? pendingIsolationTransaction;
        private UIApplication? pendingIsolationApplication;
        private string? pendingIsolationFailure;
        private int pendingIsolationCount;
        private bool pendingIsolationStatusErrorReported;
        private TransactionStatus? pendingIsolationFinalStatus;
        private bool pendingReset;

        public X31ReviewWindow? Window { get; set; }

        public string GetName() => "BIMassist X31 review selection and active-view difference";

        public void SetRequest(X31SelectionPlan plan, bool isolateRequest)
        {
            if (request != null || missingRequest) throw new InvalidOperationException("Eine Auswahl/Isolation wartet bereits auf Revit.");
            request = plan ?? throw new ArgumentNullException(nameof(plan));
            isolate = isolateRequest;
        }

        public void SetMissingRequest(bool isolateRequest)
        {
            if (request != null || missingRequest) throw new InvalidOperationException("Eine Auswahl/Isolation wartet bereits auf Revit.");
            missingRequest = true;
            isolate = isolateRequest;
        }

        public void CancelRequest()
        {
            request = null;
            missingRequest = false;
            isolate = false;
        }

        public void Execute(UIApplication app)
        {
            var plan = request;
            var isolateRequest = isolate;
            var selectMissing = missingRequest;
            CancelRequest();
            try
            {
                if (pendingIsolationTransaction != null)
                {
                    ReportIsolationStatus("Revit verarbeitet noch die vorherige temporäre Isolation. Bitte warten Sie auf das Ergebnis.");
                    return;
                }
                if (pendingIsolationApplication != null)
                {
                    TryUnsubscribeFromIdling();
                    if (pendingIsolationApplication != null)
                    {
                        ReportIsolationStatus("Revit konnte die vorherige Ereignisüberwachung noch nicht beenden.");
                        return;
                    }
                }
                var uiDocument = app.ActiveUIDocument;
                if (uiDocument == null)
                {
                    ReportIsolationStatus("Es ist kein Revit-Projektdokument aktiv. Öffnen Sie eines und starten Sie die X31-Prüfung erneut.");
                    return;
                }
                if (!sourceDocument.IsValidObject)
                {
                    ReportIsolationStatus("Das Revit-Dokument, für das diese X31-Prüfung geöffnet wurde, ist nicht mehr gültig. Öffnen Sie die Prüfung für das aktuelle Dokument erneut.");
                    return;
                }
                var activeDocument = uiDocument.Document;
                if (!activeDocument.IsValidObject)
                {
                    ReportIsolationStatus("Das aktive Revit-Dokument ist nicht mehr gültig. Öffnen Sie die X31-Prüfung für das aktuelle Dokument erneut.");
                    return;
                }
                if (!sourceDocument.Equals(activeDocument))
                {
                    ReportIsolationStatus($"Die X31-Prüfung gehört zu '{sourceDocument.Title}', aktiv ist '{activeDocument.Title}'. Öffnen Sie die Prüfung für das aktive Dokument erneut.");
                    return;
                }
                if (!selectMissing && (plan == null || plan.ElementIds.Count == 0))
                {
                    ReportIsolationStatus("Keine gültigen ElementIDs zum Auswählen vorhanden.");
                    return;
                }

                var document = uiDocument.Document;
                var activeView = uiDocument.ActiveView;
                if (selectMissing && (activeView.IsTemplate || !FilteredElementCollector.IsViewValidForElementIteration(document, activeView.Id)))
                {
                    ReportIsolationStatus("Die aktive Projektansicht unterstützt keine elementbasierte Auswahl. Bitte eine gültige Modellansicht aktivieren.");
                    return;
                }
                // Snapshot while the view still has its user's temporary-hide state;
                // resetting that mode first would silently include hidden elements.
                var missingCandidates = selectMissing
                    ? X31ReviewViewCandidates.Find(document, uiDocument.ActiveView, includedIds)
                    : Array.Empty<ElementId>();
                uiDocument.Selection.SetElementIds(Array.Empty<ElementId>());
                if (!ClearPreviousTemporaryIsolation(app, uiDocument, document)) return;
                var seeds = new List<ElementId>();
                var missing = new List<string>();
                if (selectMissing)
                    seeds.AddRange(missingCandidates);
                else foreach (var text in plan!.ElementIds)
                {
                    if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                    {
                        missing.Add(text);
                        continue;
                    }
                    var id = new ElementId(value);
                    if (document.GetElement(id) == null) missing.Add(text);
                    else seeds.Add(id);
                }
                seeds = seeds.Distinct().ToList();
                if (seeds.Count == 0)
                {
                    ReportIsolationStatus(selectMissing
                        ? "Keine nicht enthaltenen Modellkandidaten im Ansichts- und Zuschneidebereich gefunden (Bounding-Box-Prüfung; keine Garantie für tatsächliche Sichtbarkeit)."
                        : "Keine der ausgewählten ElementIDs ist im aktiven Revit-Dokument vorhanden.");
                    return;
                }

                if (!isolateRequest)
                {
                    uiDocument.Selection.SetElementIds(seeds);
                    ReportIsolationStatus($"{seeds.Count} Revit-Element(e) selektiert." + (missing.Count > 0 ? $" {missing.Count} IDs fehlen im Dokument." : string.Empty));
                    return;
                }
                if (!selectMissing && (plan!.SelectionIncomplete || missing.Count > 0))
                {
                    uiDocument.Selection.SetElementIds(seeds);
                    ReportIsolationStatus("Teil-Isolation verhindert: Die X31-Zuordnung ist unvollständig oder ElementIDs fehlen. Vorhandene Elemente wurden nur selektiert.");
                    return;
                }

                var allIds = selectMissing ? seeds : X31ElementSelectionPlanner.ExpandGroupMembers(seeds.Select(id => id.Value), rawId =>
                {
                    var element = document.GetElement(new ElementId(rawId));
                    return element is Group group ? group.GetMemberIds().Select(member => member.Value) : null;
                }).Select(id => new ElementId(id)).ToList();
                uiDocument.Selection.SetElementIds(seeds);
                // Temporary view isolation changes view state and requires a transaction;
                // selection alone remains outside it and the takeoff/export stays read-only.
                var transaction = new Transaction(document, "AVA – X31 temporär isolieren");
                var retained = false;
                var subscribed = false;
                var committed = false;
                try
                {
                    // Register before starting: a failed subscription can never leave
                    // an unresolved Pending transaction without a completion callback.
                    app.Idling += OnIsolationTransactionIdling;
                    subscribed = true;
                    // Modal failure handling finishes synchronously in Revit. Retain the
                    // transaction until Idling nevertheless if the API reports Pending.
                    try
                    {
                        if (transaction.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("Die Revit-Transaktion für die temporäre Isolation konnte nicht gestartet werden.");
                        var options = transaction.GetFailureHandlingOptions().SetForcedModalHandling(true);
                        transaction.SetFailureHandlingOptions(options);
                        uiDocument.ActiveView.IsolateElementsTemporary(allIds);
                        var commitStatus = transaction.Commit();
                        if (commitStatus == TransactionStatus.Pending)
                        {
                            RetainPendingIsolation(app, transaction, allIds.Count, null);
                            retained = true;
                            ReportIsolationStatus("Revit verarbeitet die temporäre Isolation noch. Das Ergebnis ist nicht bestätigt; bitte Revit-Meldungen prüfen.");
                            return;
                        }
                        if (commitStatus != TransactionStatus.Committed)
                            throw new InvalidOperationException("Die temporäre Isolation konnte nicht in Revit übernommen werden.");
                        committed = true;
                        ReportIsolationStatus($"{allIds.Count} Revit-Element(e) temporär isoliert.");
                    }
                    catch (Exception ex)
                    {
                        TransactionStatus status;
                        try
                        {
                            status = transaction.GetStatus();
                        }
                        catch (Exception statusError)
                        {
                            // The state cannot be read. Try an explicit rollback before
                            // letting Dispose perform its documented last-resort cleanup.
                            try
                            {
                                status = transaction.RollBack();
                            }
                            catch (Exception rollbackError)
                            {
                                throw new InvalidOperationException("Transaktionsstatus nicht lesbar; Rücknahme nicht bestätigt: " + statusError.Message + "; " + rollbackError.Message, rollbackError);
                            }
                        }
                        if (status == TransactionStatus.Started)
                        {
                            try
                            {
                                status = transaction.RollBack();
                            }
                            catch (Exception rollbackError)
                            {
                                // Idling cannot fire while this transaction is Started.
                                // Dispose is the last-resort rollback, not proof of success.
                                throw new InvalidOperationException("Revit konnte die Isolation nicht zurücknehmen; Rücknahme nicht bestätigt: " + rollbackError.Message, rollbackError);
                            }
                        }
                        if (status == TransactionStatus.Pending)
                        {
                            RetainPendingIsolation(app, transaction, allIds.Count, ex.Message);
                            retained = true;
                            ReportIsolationStatus("Revit verarbeitet die Rücknahme der Isolation noch. Das Ergebnis ist nicht bestätigt; bitte Revit-Meldungen prüfen.");
                            return;
                        }
                        throw;
                    }
                }
                finally
                {
                    if (!retained)
                    {
                        try
                        {
                            if (subscribed) app.Idling -= OnIsolationTransactionIdling;
                        }
                        catch (Exception cleanupError)
                        {
                            pendingIsolationApplication = app;
                            ReportIsolationStatus("Revit konnte die Ereignisüberwachung nicht entfernen: " + cleanupError.Message);
                        }
                        finally
                        {
                            try
                            {
                                transaction.Dispose();
                            }
                            catch (Exception cleanupError)
                            {
                                ReportIsolationStatus(committed
                                    ? "Revit hat die Isolation übernommen; die Transaktion konnte nicht freigegeben werden: " + cleanupError.Message
                                    : "Revit konnte die Transaktion nicht freigeben; Rücknahme nicht bestätigt: " + cleanupError.Message);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ReportIsolationStatus("Revit konnte die Auswahl/Isolation nicht ausführen: " + ex.Message);
            }
        }

        private bool ClearPreviousTemporaryIsolation(UIApplication app, UIDocument uiDocument, Document document)
        {
            if (!uiDocument.ActiveView.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate)) return true;
            var transaction = new Transaction(document, "AVA – vorherige temporäre Isolation aufheben");
            var retained = false;
            var subscribed = false;
            var committed = false;
            try
            {
                // The callback must exist before a Commit/RollBack can become Pending.
                app.Idling += OnIsolationTransactionIdling;
                subscribed = true;
                try
                {
                    if (transaction.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Die vorherige temporäre Isolation konnte nicht aufgehoben werden.");
                    transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetForcedModalHandling(true));
                    uiDocument.ActiveView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    var status = transaction.Commit();
                    if (status == TransactionStatus.Pending)
                    {
                        RetainPendingIsolation(app, transaction, 0, null);
                        pendingReset = true;
                        retained = true;
                        ReportIsolationStatus("Revit verarbeitet das Aufheben der vorherigen Isolation noch. Ergebnis unbestätigt; nach Abschluss bitte erneut versuchen.");
                        return false;
                    }
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit hat das Aufheben der vorherigen temporären Isolation nicht bestätigt.");
                    committed = true;
                    return true;
                }
                catch (Exception ex)
                {
                    TransactionStatus status;
                    try { status = transaction.GetStatus(); }
                    catch (Exception statusError)
                    {
                        try { status = transaction.RollBack(); }
                        catch (Exception rollbackError)
                        {
                            // Unknown: retain and block subsequent transactions; never dispose
                            // an object that could still be Pending in Revit failure handling.
                            RetainPendingIsolation(app, transaction, 0, ex.Message);
                            pendingReset = retained = true;
                            ReportIsolationStatus("Status und Rücknahme der vorherigen Isolation unbestätigt: "
                                + statusError.Message + "; " + rollbackError.Message + ". Bitte Revit-Meldungen prüfen und nach Abschluss erneut versuchen.");
                            return false;
                        }
                    }
                    if (status == TransactionStatus.Started)
                    {
                        try { status = transaction.RollBack(); }
                        catch (Exception rollbackError)
                        {
                            // Started cannot be resolved by Idling; Dispose is the last-resort
                            // rollback, and this result cannot be reported as a successful reset.
                            throw new InvalidOperationException("Rücknahme der vorherigen Isolation nicht bestätigt: " + rollbackError.Message, rollbackError);
                        }
                    }
                    if (status == TransactionStatus.Pending)
                    {
                        RetainPendingIsolation(app, transaction, 0, ex.Message);
                        pendingReset = retained = true;
                        ReportIsolationStatus("Revit verarbeitet die Rücknahme der vorherigen Isolation noch. Ergebnis unbestätigt; nach Abschluss bitte erneut versuchen.");
                        return false;
                    }
                    throw;
                }
            }
            finally
            {
                if (!retained)
                {
                    try
                    {
                        if (subscribed) app.Idling -= OnIsolationTransactionIdling;
                    }
                    catch (Exception cleanupError)
                    {
                        pendingIsolationApplication = app;
                        ReportIsolationStatus("Revit konnte die Ereignisüberwachung nicht entfernen: " + cleanupError.Message);
                    }
                    try { transaction.Dispose(); }
                    catch (Exception cleanupError)
                    {
                        ReportIsolationStatus(committed
                            ? "Revit hat das Aufheben der Isolation übernommen; die Transaktion konnte nicht freigegeben werden: " + cleanupError.Message
                            : "Revit konnte die Rücknahme der vorherigen Isolation nicht bestätigen: " + cleanupError.Message);
                    }
                }
            }
        }

        private void RetainPendingIsolation(UIApplication app, Transaction transaction, int count, string? failure)
        {
            pendingIsolationTransaction = transaction;
            pendingIsolationApplication = app;
            pendingIsolationCount = count;
            pendingIsolationFailure = failure;
            pendingIsolationStatusErrorReported = false;
            pendingIsolationFinalStatus = null;
        }

        private void OnIsolationTransactionIdling(object? sender, IdlingEventArgs args)
        {
            var transaction = pendingIsolationTransaction;
            if (transaction == null)
            {
                TryUnsubscribeFromIdling();
                return;
            }
            var recovery = X31ReviewTransactionRecovery.Resolve(
                () => pendingIsolationFinalStatus ?? transaction.GetStatus(), transaction.RollBack,
                TransactionStatus.Started, TransactionStatus.Pending,
                candidate => candidate is TransactionStatus.Committed or TransactionStatus.RolledBack);
            if (recovery.Error is { } recoveryError)
            {
                // Keep the transaction and retry status/rollback on a later idle event.
                ReportIsolationErrorOnce("Revit konnte die Isolationstransaktion noch nicht abschließen: " + recoveryError.Message);
                return;
            }
            if (!recovery.CanDispose) return;
            var status = recovery.Status;
            pendingIsolationFinalStatus = status;

            try
            {
                transaction.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already closed after Revit completed failure handling.
            }
            catch (Exception cleanupError)
            {
                ReportIsolationErrorOnce("Revit konnte die abgeschlossene Isolation noch nicht freigeben: " + cleanupError.Message);
                return;
            }
            pendingIsolationTransaction = null;
            var failure = pendingIsolationFailure;
            var wasReset = pendingReset;
            pendingReset = false;
            pendingIsolationFailure = null;
            pendingIsolationFinalStatus = null;
            pendingIsolationStatusErrorReported = false;
            TryUnsubscribeFromIdling();
            if (wasReset)
            {
                ReportIsolationStatus(failure != null
                    ? "Das Aufheben der vorherigen Isolation wurde nicht bestätigt: " + failure + ". Bitte Revit-Meldungen prüfen und danach erneut versuchen."
                    : status == TransactionStatus.Committed
                        ? "Die vorherige Isolation wurde aufgehoben. Bitte die Auswahl/Isolation erneut ausführen."
                        : "Die vorherige Isolation wurde nicht aufgehoben. Bitte Revit-Meldungen prüfen und danach erneut versuchen.");
            }
            else if (failure != null)
                ReportIsolationStatus("Revit konnte die Isolation nicht ausführen: " + failure);
            else if (status == TransactionStatus.Committed)
                ReportIsolationStatus($"{pendingIsolationCount} Revit-Element(e) temporär isoliert.");
            else
                ReportIsolationStatus("Die temporäre Isolation wurde von Revit nicht übernommen.");
        }

        private void TryUnsubscribeFromIdling()
        {
            var app = pendingIsolationApplication;
            if (app == null) return;
            try
            {
                app.Idling -= OnIsolationTransactionIdling;
                pendingIsolationApplication = null;
            }
            catch (Exception cleanupError)
            {
                ReportIsolationErrorOnce("Revit konnte die Ereignisüberwachung noch nicht entfernen: " + cleanupError.Message);
            }
        }

        private void ReportIsolationErrorOnce(string message)
        {
            if (pendingIsolationStatusErrorReported) return;
            pendingIsolationStatusErrorReported = true;
            ReportIsolationStatus(message);
        }

        private void ReportIsolationStatus(string message)
        {
            try { Window?.ShowStatus(message); }
            catch (Exception) { /* The modeless window may be closing during Idling. */ }
        }
    }
}