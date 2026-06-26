using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Views;
using System;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    internal class AssignMaterialCommand : IExternalCommand
    {
        private static readonly object SyncRoot = new object();
        private static MaterialFavoritesWindow _window;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiapp = commandData.Application;

                lock (SyncRoot)
                {
                    if (_window == null || !_window.IsLoaded)
                    {
                        _window = new MaterialFavoritesWindow(uiapp)
                        {
                            ShowInTaskbar = false,
                            Topmost = false,
                            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
                        };

                        _window.Closed += (_, __) =>
                        {
                            lock (SyncRoot)
                            {
                                _window = null;
                            }
                        };

                        WpfOwner.ShowModeless(_window, uiapp);
                    }
                    else
                    {
                        _window.RestoreAndActivate();
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMassist", "Fehler beim Öffnen von 'Material zuweisen':\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
