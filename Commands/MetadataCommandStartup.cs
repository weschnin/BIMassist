using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;

namespace BIMassist.Commands
{
    // =========================
    // ExternalCommand Einstiegspunkt
    // =========================
    [Transaction(TransactionMode.Manual)]
    public class MetadataCommandStartup : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane dp = commandData.Application.GetDockablePane(new DockablePaneId(GuidCollection.GetMetadataDockablePaneID()));

                if (dp.IsShown())
                    dp.Hide();
                else
                    dp.Show();
            }
            catch (Exception msg)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Warnung", msg.Message);
            }

            return Result.Succeeded;
        }
    }
}
