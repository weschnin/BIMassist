using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class BGKManagerCommandStartup : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane dp = commandData.Application.GetDockablePane(
                    new DockablePaneId(GuidCollection.GetBGKManagerDockablePaneID()));

                if (dp.IsShown())
                    dp.Hide();
                else
                    dp.Show();
            }
            catch (Exception msg)
            {
                TaskDialog.Show("Warnung", msg.Message);
            }
            return Result.Succeeded;
        }
    }
}
