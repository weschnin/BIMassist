using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Views;
using BIMassist.Views;
using System;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AvaTemplateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // File-based template work only: no Document is held and no model values are written.
                WpfOwner.ShowDialog(new AvaTemplateWindow(), commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = "Der AVA-Vorlageneditor konnte nicht geöffnet werden.\n\n" + ex.Message;
                return Result.Failed;
            }
        }
    }
}
