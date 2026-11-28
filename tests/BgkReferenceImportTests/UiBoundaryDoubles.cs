using Autodesk.Revit.DB;
namespace Autodesk.Revit.Attributes
{
    public enum TransactionMode { Manual }
    [AttributeUsage(AttributeTargets.Class)] public sealed class TransactionAttribute(TransactionMode mode) : Attribute { public TransactionMode Mode = mode; }
}
namespace Autodesk.Revit.UI.Events { public sealed class IdlingEventArgs : EventArgs { } }
namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Failed, Cancelled }
    public enum ExternalEventRequest { Accepted, Pending, Denied, TimedOut }
    public interface IExternalCommand { Result Execute(ExternalCommandData data, ref string message, ElementSet elements); }
    public sealed class ExternalCommandData { public UIApplication Application = new(); }
    public sealed class UIDocument(Document document) { public Document Document => document; }
    public sealed class Application { public List<Document> Documents = []; }
    public sealed class UIApplication
    {
        private UIDocument? active;
        public UIDocument? ActiveUIDocument { get { ApiContext.Require(); return active; } set { active = value; } }
        public Application Application = new();
        public IntPtr MainWindowHandle;
        private EventHandler<Events.IdlingEventArgs>? idling;
        public int IdlingSubscribers;
        public event EventHandler<Events.IdlingEventArgs> Idling { add { ApiContext.Require(); idling += value; IdlingSubscribers++; } remove { ApiContext.Require(); idling -= value; IdlingSubscribers--; } }
        public void Idle() => idling?.Invoke(this, new());
    }
    public interface IExternalEventHandler { void Execute(UIApplication app); string GetName(); }
    public sealed class ExternalEvent(IExternalEventHandler handler) : IDisposable
    {
        public static ExternalEvent? Last;
        public static ExternalEventRequest NextRaise = ExternalEventRequest.Accepted;
        public static bool ThrowRaise;
        public bool Disposed;
        public static ExternalEvent Create(IExternalEventHandler handler) { ApiContext.Require(); return Last = new(handler); }
        public ExternalEventRequest Raise() => ThrowRaise ? throw new InvalidOperationException("Raise failed") : NextRaise;
        public void Fire(UIApplication app) { if (Disposed) throw new Exception("Disposed external event fired"); handler.Execute(app); }
        public void Dispose() => Disposed = true;
    }
    [Flags] public enum TaskDialogCommonButtons { Yes = 1, No = 2, Ok = 4 }
    public enum TaskDialogResult { Yes, No, Ok }
    public sealed class TaskDialog(string title)
    {
        public string MainInstruction = "", MainContent = "";
        public TaskDialogCommonButtons CommonButtons;
        public TaskDialogResult DefaultButton;
        public static TaskDialogResult Confirmation = TaskDialogResult.Yes;
        public static Action<TaskDialog>? OnShow;
        public static List<string> Shown = [];
        public string Title => title;
        public TaskDialogResult Show() { Shown.Add(title); OnShow?.Invoke(this); return CommonButtons.HasFlag(TaskDialogCommonButtons.Yes) ? Confirmation : TaskDialogResult.Ok; }
        public static TaskDialogResult Show(string title, string message) { Shown.Add(title); return TaskDialogResult.No; }
        public static void Reset() { Shown.Clear(); Confirmation = TaskDialogResult.Yes; OnShow = null; }
    }
    public enum ItemSelectionDialogResult { Confirmed, Cancelled }
    public sealed class FileSaveDialog(string filter) : IDisposable
    {
        public string Title = "", DefaultFilterEntry = "", InitialFileName = "";
        public string Filter = filter;
        public ItemSelectionDialogResult Show() => ItemSelectionDialogResult.Cancelled;
        public object? GetSelectedModelPath() => null;
        public void Dispose() { }
    }
}