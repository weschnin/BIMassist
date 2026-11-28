using Autodesk.Revit.DB;
using BIMassist.Ava.Core;
using BIMassist.Core;

internal static class WriterTests
{
    internal static void Run()
    {
        var target = new Document { CorruptAfterCommit = true };
        var symbol = new FamilySymbol(); target.Symbols.Add(symbol);
        var plan = BgkReferenceTransfer.CreatePlan([new("Family", "Type", true)], [new("Family", "Type", "09-25")]);
        bool rejected = false;
        try { new BgkReferenceImportWriter().Write(target, target.Symbols, plan); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("Commit alone must not report success when ASSEMBLY_CODE readback differs");
        Console.WriteLine("PASS BGK import rejects corrupt post-commit readback");
        target = new Document { CommitResult = TransactionStatus.Pending };
        target.Symbols.Add(new FamilySymbol());
        var writer = new BgkReferenceImportWriter();
        try { writer.Write(target, target.Symbols, plan); } catch (InvalidOperationException) { }
        if (target.LastTransaction!.Disposed) throw new Exception("An unfinished Pending BGK transaction must remain owned, not disposed");
        Console.WriteLine("PASS BGK import retains Pending transaction ownership");
        foreach (var invalid in new[] { new Document { IsValidObject = false }, new Document { IsFamilyDocument = true }, new Document { IsReadOnly = true }, new Document { IsModifiable = true } })
        {
            invalid.Symbols.Add(new FamilySymbol());
            rejected = false;
            try { new BgkReferenceImportWriter().Write(invalid, invalid.Symbols, plan); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || invalid.Symbols[0].Bgk!.SetCount != 0) throw new Exception("The writer must reject invalid/family/read-only/busy target projects before any Set");
        }
        Console.WriteLine("PASS BGK import validates target projects before writing");
        target = new Document { StartResult = TransactionStatus.Uninitialized }; target.Symbols.Add(new FamilySymbol());
        rejected = false;
        try { new BgkReferenceImportWriter().Write(target, target.Symbols, plan); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected || target.Symbols[0].Bgk!.SetCount != 0) throw new Exception("A rejected transaction Start must prevent every parameter write");
        Console.WriteLine("PASS BGK import checks transaction Start");
        foreach (var parameter in new[] { new Parameter { IsReadOnly = true }, new Parameter { StorageType = StorageType.Integer }, new Parameter { Definition = new Definition { DataType = new("not-text") } } })
        {
            target = new Document(); target.Symbols.Add(new FamilySymbol { Bgk = parameter });
            rejected = false;
            try { new BgkReferenceImportWriter().Write(target, target.Symbols, plan); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || parameter.SetCount != 0) throw new Exception("Revalidate ASSEMBLY_CODE writability and text spec immediately before Set");
        }
        Console.WriteLine("PASS BGK import revalidates parameter contract before Set");
        foreach (var terminal in new[] { TransactionStatus.Committed, TransactionStatus.RolledBack })
        {
            target = new Document { CommitResult = TransactionStatus.Pending }; target.Symbols.Add(new FamilySymbol());
            int confirmed = 0; writer = new() { Committed = () => confirmed++ };
            if (writer.Write(target, target.Symbols, plan) || !writer.IsBusy || confirmed != 0 || !target.ForcedModal)
                throw new Exception("Pending must not confirm success or release ownership, and modal failure handling is required");
            target.LastTransaction!.Status = terminal; target.IsModifiable = false;
            writer.Resolve(); writer.Resolve();
            if (writer.IsBusy || !target.LastTransaction.Disposed || confirmed != (terminal == TransactionStatus.Committed ? 1 : 0))
                throw new Exception("Terminal recovery must release ownership and confirm exactly once only after commit readback");
        }
        Console.WriteLine("PASS BGK import resolves retained Pending commits/rollbacks exactly once");

        target = new Document { ThrowRollback = true }; target.Symbols.Add(new FamilySymbol { Bgk = new() { ReturnFalse = true } });
        writer = new();
        try { writer.Write(target, target.Symbols, plan); } catch (InvalidOperationException) { }
        if (!writer.IsBusy || target.LastTransaction!.Disposed) throw new Exception("A failed rollback must retain the Started transaction");
        target.ThrowRollback = false; writer.Resolve();
        if (writer.IsBusy || target.Symbols[0].Bgk!.Value != "old") throw new Exception("Started recovery must retry rollback in API context");
        Console.WriteLine("PASS BGK import retains failed rollback and retries safely");

        target = new Document { ThrowDispose = true }; target.Symbols.Add(new FamilySymbol());
        int success = 0; writer = new() { Committed = () => success++ };
        writer.Write(target, target.Symbols, plan);
        if (!writer.IsBusy || success != 1 || writer.LastError != null) throw new Exception("Cleanup failure must not erase verified committed success");
        target.ThrowDispose = false; writer.Resolve();
        if (writer.IsBusy || success != 1) throw new Exception("Dispose retry must not replay completion");
        Console.WriteLine("PASS BGK import preserves verified success across cleanup retry");
    }
}