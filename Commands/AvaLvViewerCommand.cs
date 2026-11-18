using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Views;

namespace BIMassist.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class AvaLvViewerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            using var dialog = new FileOpenDialog("GAEB-LV (*.x82)|*.x82|Alle Dateien (*.*)|*.*")
            {
                Title = "GAEB-LV im Viewer öffnen",
                DefaultFilterEntry = "*.x82"
            };
            if (dialog.Show() != ItemSelectionDialogResult.Confirmed) return Result.Cancelled;
            var selectedPath = dialog.GetSelectedModelPath();
            if (selectedPath == null) throw new InvalidDataException("Der Revit-Öffnendialog hat keinen X82-Dateipfad zurückgegeben.");
            var path = Path.GetFullPath(ModelPathUtils.ConvertModelPathToUserVisiblePath(selectedPath));
            LvX82ViewerWindow.Show(path, commandData.Application.MainWindowHandle);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = "Der GAEB-LV-Viewer konnte nicht geöffnet werden.\n\n" + ex.Message;
            TaskDialog.Show("AVA – LV-Viewer", message);
            return Result.Failed;
        }
    }
}
