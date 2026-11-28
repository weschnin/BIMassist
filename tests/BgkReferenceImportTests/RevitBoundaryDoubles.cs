namespace Autodesk.Revit.DB;

public static class ApiContext { public static bool Allowed = true; public static void Require() { if (!Allowed) throw new Exception("Revit model access outside Execute/Idling"); } }

public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack, Pending, Error }
public enum BuiltInParameter { ASSEMBLY_CODE, ALL_MODEL_TYPE_COMMENTS }
public enum StorageType { String, Integer }
public sealed record ForgeTypeId(string Name);
public static class SpecTypeId { public static class String { public static ForgeTypeId Text = new("text"); } }
public sealed class Definition { public ForgeTypeId DataType = SpecTypeId.String.Text; public ForgeTypeId GetDataType() => DataType; }
public sealed record ElementId(long Value);
public sealed class Parameter
{
    public bool IsReadOnly, HasValue = true, IgnoreSet, ReturnFalse;
    public StorageType StorageType = StorageType.String;
    public Definition Definition = new();
    public string? Value = "old";
    public int SetCount;
    public string? AsString() => Value;
    public bool Set(string value) { SetCount++; if (!IgnoreSet) Value = value; return !ReturnFalse; }
}
public sealed class Family { public string Name = "Family"; }
public sealed class FamilySymbol
{
    public Family? Family = new();
    public string Name = "Type";
    public ElementId Id = new(1);
    public Parameter? Bgk = new();
    public Parameter Other = new();
    public Parameter? get_Parameter(BuiltInParameter id) { ApiContext.Require(); return id == BuiltInParameter.ASSEMBLY_CODE ? Bgk : Other; }
}
public sealed class Document
{
    public string Title = "Project", PathName = "";
    public object Identity = new();
    private bool valid = true;
    public bool IsValidObject { get { ApiContext.Require(); return valid; } set { valid = value; } }
    public bool IsFamilyDocument, IsReadOnly, IsModifiable;
    public readonly List<FamilySymbol> Symbols = [];
    public TransactionStatus CommitResult = TransactionStatus.Committed, StartResult = TransactionStatus.Started;
    public bool CorruptAfterCommit, ThrowRollback, ThrowDispose, ForcedModal;
    public Transaction? LastTransaction;
    public override bool Equals(object? obj) => obj is Document other && ReferenceEquals(Identity, other.Identity);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Identity);
    public object? GetElement(ElementId id) { ApiContext.Require(); return Symbols.SingleOrDefault(s => s.Id == id); }
}
public sealed class ElementSet { }
public sealed class FilteredElementCollector(Document document) : IEnumerable<FamilySymbol>
{
    public FilteredElementCollector OfClass(Type type) => this;
    public IEnumerator<FamilySymbol> GetEnumerator() => document.Symbols.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
public static class ModelPathUtils { public static string ConvertModelPathToUserVisiblePath(object path) => path.ToString()!; }
public sealed class FailureHandlingOptions
{
    public bool Forced;
    public FailureHandlingOptions SetForcedModalHandling(bool value) { Forced = value; return this; }
}
public sealed class Transaction : IDisposable
{
    private readonly Document document;
    private Dictionary<Parameter, string?> snapshot = [];
    public TransactionStatus Status;
    public bool Disposed;
    public Transaction(Document document, string name) { this.document = document; document.LastTransaction = this; }
    public TransactionStatus Start()
    {
        snapshot = document.Symbols.Where(s => s.Bgk != null).ToDictionary(s => s.Bgk!, s => s.Bgk!.Value);
        Status = document.StartResult; document.IsModifiable = Status == TransactionStatus.Started; return Status;
    }
    public FailureHandlingOptions GetFailureHandlingOptions() => new();
    public Transaction SetFailureHandlingOptions(FailureHandlingOptions options) { document.ForcedModal = options.Forced; return this; }
    public TransactionStatus Commit()
    {
        Status = document.CommitResult;
        if (Status == TransactionStatus.Committed && document.CorruptAfterCommit) foreach (var p in snapshot.Keys) p.Value = "corrupt";
        if (Status == TransactionStatus.RolledBack) foreach (var pair in snapshot) pair.Key.Value = pair.Value;
        document.IsModifiable = Status is TransactionStatus.Started or TransactionStatus.Pending; return Status;
    }
    public TransactionStatus GetStatus() { ApiContext.Require(); return Status; }
    public TransactionStatus RollBack()
    {
        if (document.ThrowRollback) throw new InvalidOperationException("rollback failed");
        foreach (var pair in snapshot) pair.Key.Value = pair.Value;
        document.IsModifiable = false; return Status = TransactionStatus.RolledBack;
    }
    public void Dispose()
    {
        if (document.ThrowDispose) throw new InvalidOperationException("dispose failed");
        if (Status == TransactionStatus.Started) RollBack();
        Disposed = true;
    }
}