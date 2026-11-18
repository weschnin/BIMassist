using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

internal static class AvaGroupingDialog
{
    public static readonly Queue<QuantityGroupingSelection?> Choices = new();
    public static int Calls;
    public static long LastViewId;
    public static QuantityGroupingSelection? Show(UIApplication application, Document document, View activeView)
    {
        Calls++;
        LastViewId = activeView.Id.Value;
        return Choices.Count == 0 ? new(QuantityGroupingMode.Position) : Choices.Dequeue();
    }
    public static void Reset() { Choices.Clear(); Calls = 0; LastViewId = 0; }
}
