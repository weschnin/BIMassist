// Only the unavailable Revit/desktop boundary is doubled. Core, adapter, command and writer are linked production source.
namespace Autodesk.Revit.Attributes
{
    public enum TransactionMode { ReadOnly }
    [AttributeUsage(AttributeTargets.Class)] public sealed class TransactionAttribute(TransactionMode mode) : Attribute
    { public TransactionMode Mode { get; } = mode; }
}
namespace Autodesk.Revit.DB { public sealed class ElementSet { } }
namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Cancelled, Failed }
    [Flags] public enum TaskDialogCommonButtons { Yes = 1, No = 2 }
    public enum TaskDialogResult { Yes, No, Cancel, Ok }
    public sealed class UIDocument(Autodesk.Revit.DB.Document document) { public Autodesk.Revit.DB.Document Document { get; } = document; }
    public sealed class UIApplication { public UIDocument? ActiveUIDocument { get; set; } public IntPtr MainWindowHandle { get; set; } = new(1234); }
    public sealed class ExternalCommandData { public UIApplication Application { get; set; } = new(); }
    public interface IExternalCommand { Result Execute(ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements); }
    public sealed class TaskDialog(string title)
    {
        public string Title { get; } = title;
        public string MainInstruction { get; set; } = "";
        public string MainContent { get; set; } = "";
        public string ExpandedContent { get; set; } = "";
        public TaskDialogCommonButtons CommonButtons { get; set; }
        public TaskDialogResult DefaultButton { get; set; }
        public static readonly List<TaskDialog> Confirmations = [];
        public static readonly List<string> Notices = [];
        public static readonly Queue<TaskDialogResult> Answers = new();
        public static TaskDialogResult Show(string title, string content) { Notices.Add(content); return TaskDialogResult.Ok; }
        public TaskDialogResult Show() { Confirmations.Add(this); return Answers.Count > 0 ? Answers.Dequeue() : TaskDialogResult.No; }
        public static void Reset() { Confirmations.Clear(); Notices.Clear(); Answers.Clear(); }
    }
}
namespace System.Windows
{
    public sealed class Window
    {
        public IntPtr NativeOwner { get; set; }
        public static int Created, Closed;
        public Window() { Created++; }
        public void Close() { Closed++; }
    }
}
namespace System.Windows.Interop
{
    public sealed class WindowInteropHelper(System.Windows.Window window)
    { public IntPtr Owner { get => window.NativeOwner; set => window.NativeOwner = value; } }
}
namespace Microsoft.Win32
{
    public abstract class CommonDialog
    {
        public string Title { get; set; } = "";
        public string Filter { get; set; } = "";
        public string DefaultExt { get; set; } = "";
        public int FilterIndex { get; set; }
        public string FileName { get; set; } = "";
        public static readonly Queue<string?> OpenChoices = new(), SaveChoices = new();
        public static readonly List<(CommonDialog Dialog, IntPtr Owner)> Shown = [];
        public bool? ShowDialog() => Choose(IntPtr.Zero);
        public bool? ShowDialog(System.Windows.Window owner) => Choose(owner.NativeOwner);
        private bool? Choose(IntPtr owner)
        {
            Shown.Add((this, owner));
            var queue = this is OpenFileDialog ? OpenChoices : SaveChoices;
            if (queue.Count == 0) return false;
            var path = queue.Dequeue();
            if (path == null) return false;
            FileName = path;
            return true;
        }
        public static void Reset() { OpenChoices.Clear(); SaveChoices.Clear(); Shown.Clear(); }
    }
    public sealed class OpenFileDialog : CommonDialog { public bool CheckFileExists { get; set; } public bool Multiselect { get; set; } }
    public sealed class SaveFileDialog : CommonDialog { public bool AddExtension { get; set; } public bool OverwritePrompt { get; set; } }
}
