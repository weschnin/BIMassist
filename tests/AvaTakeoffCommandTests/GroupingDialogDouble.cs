using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

internal static class AvaGroupingDialog
{
    public static readonly Queue<QuantityGroupingSelection?> Choices = new();
    public static int Calls;
    public static QuantityGroupingSelection? Show(UIApplication application, Document document)
    {
        Calls++;
        return Choices.Count == 0 ? new(QuantityGroupingMode.Position) : Choices.Dequeue();
    }
    public static void Reset() { Choices.Clear(); Calls = 0; }
}
