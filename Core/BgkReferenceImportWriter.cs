using Autodesk.Revit.DB;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;

namespace BIMassist.Core;

internal sealed class BgkReferenceImportWriter
{
    private Transaction? transaction;
    private Document? target;
    private (ElementId Id, string Value)[] expected = [];
    private bool commitAccepted, checkedOutcome;
    internal bool IsBusy => transaction != null;
    internal Action? Committed { get; set; }
    internal Action<string>? Report { get; set; }
    internal string? LastError { get; private set; }

    internal bool Write(Document target, IReadOnlyList<FamilySymbol> symbols, IReadOnlyList<BgkTransferDecision> plan)
    {
        if (IsBusy) throw new InvalidOperationException("Die vorherige BGK-Transaktion ist noch nicht abgeschlossen.");
        if (!target.IsValidObject || target.IsFamilyDocument || target.IsReadOnly || target.IsModifiable)
            throw new InvalidOperationException("Die BGK-Übernahme benötigt ein gültiges, beschreibbares Revit-Projekt ohne laufende Transaktion.");
        this.target = target;
        LastError = null; commitAccepted = false; checkedOutcome = false;
        expected = plan.Where(x => x.Status == BgkTransferStatus.Copy).Select(decision =>
        {
            var symbol = symbols.Single(x => (x.Family?.Name ?? "") == decision.Target.FamilyName && x.Name == decision.Target.TypeName);
            return (symbol.Id, decision.Value!);
        }).ToArray();
        transaction = new Transaction(target, "BGK aus Referenzprojekt übernehmen");
        try
        {
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Die BGK-Transaktion konnte nicht gestartet werden.");
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetForcedModalHandling(true));
            foreach (var entry in expected)
            {
                var parameter = (target.GetElement(entry.Id) as FamilySymbol)?.get_Parameter(BuiltInParameter.ASSEMBLY_CODE)
                    ?? throw new InvalidOperationException("ASSEMBLY_CODE verschwand während der Übernahme.");
                if (parameter.IsReadOnly || parameter.StorageType != StorageType.String
                    || !parameter.Definition.GetDataType().Equals(SpecTypeId.String.Text))
                    throw new InvalidOperationException("ASSEMBLY_CODE ist nicht mehr als Text beschreibbar.");
                if (!parameter.Set(entry.Value)) throw new InvalidOperationException("BGK konnte nicht gesetzt werden.");
            }
            var status = transaction.Commit();
            commitAccepted = status is TransactionStatus.Committed or TransactionStatus.Pending;
            if (!commitAccepted) throw new InvalidOperationException("Revit hat die BGK-Transaktion nicht übernommen.");
        }
        finally { Resolve(); }
        if (LastError != null) throw new InvalidOperationException(LastError);
        return !IsBusy;
    }

    /// <summary>Called only from Execute/Idling; reuse the existing tested terminal-recovery policy.</summary>
    internal void Resolve()
    {
        var current = transaction;
        if (current == null) return;
        var recovery = X31ReviewTransactionRecovery.Resolve(current.GetStatus, current.RollBack,
            TransactionStatus.Started, TransactionStatus.Pending,
            status => status is TransactionStatus.Committed or TransactionStatus.RolledBack or TransactionStatus.Uninitialized);
        if (!recovery.CanDispose)
        {
            Notify("BGK-Transaktion noch nicht bestätigt; die Sitzung bleibt zur Wiederherstellung erhalten."
                + (recovery.Error == null ? "" : " " + recovery.Error.Message));
            return;
        }
        if (!checkedOutcome)
        {
            checkedOutcome = true;
            try
            {
                if (recovery.Status != TransactionStatus.Committed || !commitAccepted)
                    throw new InvalidOperationException("Revit hat die BGK-Transaktion nicht übernommen.");
                foreach (var entry in expected)
                {
                    var readback = (target!.GetElement(entry.Id) as FamilySymbol)?.get_Parameter(BuiltInParameter.ASSEMBLY_CODE)?.AsString();
                    if (!string.Equals(readback, entry.Value, StringComparison.Ordinal))
                        throw new InvalidOperationException("Die BGK-Rückleseprüfung nach Commit ist fehlgeschlagen.");
                }
                Notify("BGK-Übernahme abgeschlossen; ASSEMBLY_CODE nach Commit geprüft.");
                var callback = Committed; Committed = null;
                try { callback?.Invoke(); }
                catch (Exception ex) { Notify("BGK-Übernahme bestätigt; Ergebnisanzeige/Bericht fehlgeschlagen: " + ex.Message); }
            }
            catch (Exception ex) { LastError = ex.Message; Committed = null; Notify("BGK-Übernahme nicht bestätigt: " + ex.Message); }
        }
        try { current.Dispose(); }
        catch (Exception ex) { Notify("Transaktionsbereinigung noch nicht bestätigt: " + ex.Message); return; }
        transaction = null; target = null; expected = []; Committed = null;
    }

    private void Notify(string message) { try { Report?.Invoke(message); } catch { } }
}