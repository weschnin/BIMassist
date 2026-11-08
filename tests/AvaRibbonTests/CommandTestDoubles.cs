// Minimal test doubles for the command boundary only. These do not test the Revit API or WPF.
namespace Autodesk.Revit.DB
{
    public sealed class ElementSet { }
}
namespace Autodesk.Revit.Attributes
{
    public enum TransactionMode { Manual, ReadOnly }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TransactionAttribute(TransactionMode mode) : Attribute
    {
        public TransactionMode Mode { get; } = mode;
    }
}
namespace Autodesk.Revit.UI
{
    public enum Result { Succeeded, Failed, Cancelled }
    public sealed class UIApplication { }
    public sealed class ExternalCommandData
    {
        public UIApplication Application { get; set; } = new();
    }
    public interface IExternalCommand
    {
        Result Execute(ExternalCommandData data, ref string message, Autodesk.Revit.DB.ElementSet elements);
    }
}
namespace BIMassist.Ava.Views
{
    public sealed class AvaTemplateWindow { }
}
namespace BIMassist.Views
{
    public static class WpfOwner
    {
        public static bool ThrowOnShow { get; set; }
        public static BIMassist.Ava.Views.AvaTemplateWindow? LastWindow { get; private set; }
        public static Autodesk.Revit.UI.UIApplication? LastOwner { get; private set; }
        public static bool? ShowDialog(BIMassist.Ava.Views.AvaTemplateWindow window, Autodesk.Revit.UI.UIApplication owner)
        {
            if (ThrowOnShow) throw new InvalidOperationException("test window failure");
            LastWindow = window;
            LastOwner = owner;
            return null;
        }
    }
}
