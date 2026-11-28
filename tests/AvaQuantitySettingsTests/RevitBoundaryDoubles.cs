// Boundary doubles only: storage/session/codec/recovery code is linked unchanged.
using System.Collections;
using Autodesk.Revit.DB.ExtensibleStorage;

internal static class Api
{
    internal static bool InContext;
    internal static void Require() { if (!InContext) throw new InvalidOperationException("Revit API outside callback"); }
    internal static void Run(Action action) { var old = InContext; InContext = true; try { action(); } finally { InContext = old; } }
}

namespace Autodesk.Revit.ApplicationServices
{
    public sealed class Application
    {
        internal event EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs>? Idling;
        public void Idle() => Api.Run(() => Idling?.Invoke(this, new()));
        internal void Add(EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs> handler) { Api.Require(); Idling += handler; }
        internal void Remove(EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs> handler) { Api.Require(); Idling -= handler; }
    }
}
namespace Autodesk.Revit.DB
{
    public enum TransactionStatus { Uninitialized, Started, Pending, Committed, RolledBack, Error }
    public sealed class Document
    {
        public bool Valid = true, ReadOnly, Family;
        internal bool Modifiable;
        public bool IsValidObject { get { Api.Require(); return Valid; } }
        public bool IsModifiable { get { Api.Require(); return Modifiable; } }
        public bool IsReadOnly { get { Api.Require(); return ReadOnly; } }
        public bool IsFamilyDocument { get { Api.Require(); return Family; } }
        public Autodesk.Revit.ApplicationServices.Application Application { get { Api.Require(); return App; } }
        internal readonly Autodesk.Revit.ApplicationServices.Application App = new();
        public List<DataStorage> Stores = [];
        public int TransactionStarts;
        public TransactionStatus CommitResult = TransactionStatus.Committed;
        public bool ThrowCommit, ThrowSet, CorruptSet, ForcedModal, CorruptAfterCommit;
        public Action? DuringCommit;
        public Transaction? LastTransaction;
    }
    public class Element
    {
        internal Dictionary<Guid, Entity> Entities = [];
        public Entity GetEntity(Schema schema) { Api.Require(); return Entities.TryGetValue(schema.GUID, out var entity) ? entity.Clone() : new Entity(); }
        public IList<Guid> GetEntitySchemaGuids() { Api.Require(); return Entities.Keys.ToList(); }
        public virtual void SetEntity(Entity entity) { Api.Require(); Entities[entity.Schema!.GUID] = entity.Clone(); }
    }
    public sealed class FilteredElementCollector : IEnumerable<Element>, IDisposable
    {
        private readonly Document document;
        public FilteredElementCollector(Document document) { Api.Require(); this.document = document; }
        public FilteredElementCollector OfClass(Type _) => this;
        public IEnumerator<Element> GetEnumerator() { Api.Require(); return document.Stores.Cast<Element>().GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { }
    }
    public sealed class FailureHandlingOptions
    {
        public bool Forced;
        public FailureHandlingOptions SetForcedModalHandling(bool value) { Forced = value; return this; }
    }
    public sealed class Transaction : IDisposable
    {
        private readonly Document document;
        private List<(DataStorage Store, Dictionary<Guid, Entity> Entities)> snapshot = [];
        public TransactionStatus Status = TransactionStatus.Uninitialized;
        public bool Disposed;
        public Transaction(Document document, string _) { Api.Require(); this.document = document; document.LastTransaction = this; }
        public TransactionStatus Start()
        {
            Api.Require(); document.TransactionStarts++; document.Modifiable = true;
            snapshot = document.Stores.Select(s => (s, s.Entities.ToDictionary(e => e.Key, e => e.Value.Clone()))).ToList();
            return Status = TransactionStatus.Started;
        }
        public FailureHandlingOptions GetFailureHandlingOptions() { Api.Require(); return new(); }
        public Transaction SetFailureHandlingOptions(FailureHandlingOptions options) { Api.Require(); document.ForcedModal = options.Forced; return this; }
        public TransactionStatus GetStatus() { Api.Require(); return Status; }
        public TransactionStatus Commit()
        {
            Api.Require(); document.DuringCommit?.Invoke();
            if (document.ThrowCommit) throw new InvalidOperationException("commit failed");
            if (document.CommitResult == TransactionStatus.RolledBack) return RollBack();
            Status = document.CommitResult;
            if (Status == TransactionStatus.Committed) { document.Modifiable = false; CorruptIfRequested(); }
            return Status;
        }
        public void CompletePending(TransactionStatus status)
        {
            Api.Require();
            if (status == TransactionStatus.RolledBack) { RollBack(); return; }
            Status = status; document.Modifiable = false; CorruptIfRequested();
        }
        private void CorruptIfRequested()
        {
            if (!document.CorruptAfterCommit) return;
            foreach (var store in document.Stores)
                foreach (var entity in store.Entities.Values) entity.Values["Json"] = "corrupt";
        }
        public TransactionStatus RollBack()
        {
            Api.Require(); document.Stores = snapshot.Select(s => s.Store).ToList();
            foreach (var entry in snapshot) entry.Store.Entities = entry.Entities;
            document.Modifiable = false; return Status = TransactionStatus.RolledBack;
        }
        public void Dispose()
        {
            Api.Require();
            if (Status is TransactionStatus.Pending or TransactionStatus.Started) throw new Exception("Disposed unresolved transaction");
            Disposed = true;
        }
    }
}
namespace Autodesk.Revit.DB.ExtensibleStorage
{
    public enum AccessLevel { Public, Vendor, Application }
    public enum ContainerType { Simple, Array, Map }
    public sealed class Field(string name, Type type)
    {
        public string FieldName { get; } = name;
        public Type ValueType { get; } = type;
        public ContainerType ContainerType { get; set; } = ContainerType.Simple;
    }
    public sealed class Schema
    {
        internal static readonly Dictionary<Guid, Schema> Registry = [];
        public Guid GUID { get; init; }
        public string SchemaName { get; set; } = "";
        internal List<Field> Fields = [];
        public static Schema? Lookup(Guid guid) { Api.Require(); return Registry.GetValueOrDefault(guid); }
        public Field GetField(string name) { Api.Require(); return Fields.SingleOrDefault(f => f.FieldName == name)!; }
        public IList<Field> ListFields() { Api.Require(); return Fields; }
    }
    public sealed class SchemaBuilder(Guid guid)
    {
        private readonly Schema schema = new() { GUID = guid };
        public void SetSchemaName(string name) { Api.Require(); schema.SchemaName = name; }
        public void SetReadAccessLevel(AccessLevel _) { Api.Require(); }
        public void SetWriteAccessLevel(AccessLevel _) { Api.Require(); }
        public Field AddSimpleField(string name, Type type) { Api.Require(); var field = new Field(name, type); schema.Fields.Add(field); return field; }
        public Schema Finish() { Api.Require(); Schema.Registry.Add(schema.GUID, schema); return schema; }
    }
    public sealed class Entity
    {
        public Schema? Schema { get; }
        public bool Valid = true;
        internal Dictionary<string, object> Values = [];
        public Entity() { Valid = false; }
        public Entity(Schema schema) { Api.Require(); Schema = schema; }
        public bool IsValid() { Api.Require(); return Valid && Schema != null; }
        public T Get<T>(string field) { Api.Require(); return (T)Values[field]; }
        public void Set<T>(string field, T value) { Api.Require(); Values[field] = value!; }
        internal Entity Clone() { var copy = Schema == null ? new Entity() : new Entity(Schema); copy.Valid = Valid; copy.Values = new(Values); return copy; }
    }
    public sealed class DataStorage : Autodesk.Revit.DB.Element
    {
        private Autodesk.Revit.DB.Document document = null!;
        public static DataStorage Create(Autodesk.Revit.DB.Document document)
        {
            Api.Require(); if (!document.Modifiable) throw new Exception("No transaction");
            var store = new DataStorage { document = document }; document.Stores.Add(store); return store;
        }
        public override void SetEntity(Entity entity)
        {
            Api.Require(); if (!document.Modifiable) throw new Exception("No transaction");
            if (document.ThrowSet) throw new InvalidOperationException("write failed");
            base.SetEntity(entity);
            if (document.CorruptSet) Entities[entity.Schema!.GUID].Values["Json"] = "invalid";
        }
    }
}
namespace Autodesk.Revit.UI.Events { public sealed class IdlingEventArgs : EventArgs { } }
namespace Autodesk.Revit.UI
{
    public enum ExternalEventRequest { Accepted, Pending, Denied, TimedOut }
    public interface IExternalEventHandler { void Execute(UIApplication app); string GetName(); }
    public sealed class UIApplication(Autodesk.Revit.ApplicationServices.Application application)
    {
        public Autodesk.Revit.DB.Document? ActiveDocument;
        public event EventHandler<Events.IdlingEventArgs> Idling { add => application.Add(value); remove => application.Remove(value); }
    }
    public sealed class ExternalEvent : IDisposable
    {
        public static ExternalEvent? Last;
        public static ExternalEventRequest NextRaise = ExternalEventRequest.Accepted;
        private readonly IExternalEventHandler handler;
        public bool Disposed, Scheduled;
        public int RaiseCount;
        private ExternalEvent(IExternalEventHandler handler) => this.handler = handler;
        public static ExternalEvent Create(IExternalEventHandler handler) { Api.Require(); return Last = new(handler); }
        public ExternalEventRequest Raise()
        {
            if (Disposed) throw new ObjectDisposedException(nameof(ExternalEvent));
            RaiseCount++; var result = NextRaise; NextRaise = ExternalEventRequest.Accepted;
            if (result is ExternalEventRequest.Accepted or ExternalEventRequest.Pending) Scheduled = true;
            return result;
        }
        public void Fire(UIApplication app)
        {
            if (!Scheduled || Disposed) throw new Exception("Event unavailable");
            Scheduled = false; Api.Run(() => handler.Execute(app));
        }
        // ExternalEvent.Dispose is deliberately legal without model API context.
        public void Dispose() { Disposed = true; }
    }
}
