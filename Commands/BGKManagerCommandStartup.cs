using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;

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
                {
                    // If pane has unsaved changes, ask user to save before hiding
                    try
                    {
                        var pane = BGKManagerPaneProvider.BGKManagerCtrlInstance;
                        if (pane != null && pane.DataContext is MainViewModel mv && mv.changed)
                        {
                        var td = new TaskDialog("Änderungen speichern")
                        {
                            MainInstruction = "Die BGK-Daten wurden verändert. Sollen diese gespeichert werden?",
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                        };
                        var res = td.Show();
                        if (res == TaskDialogResult.Yes)
                            {
                                var settingsPath = Properties.Settings.Default.PfadBGKDatei;
                                if (!string.IsNullOrWhiteSpace(settingsPath))
                                {
                                    try
                                    {
                                        mv.EigeneDaten = settingsPath;
                                        var dir = Path.GetDirectoryName(settingsPath);
                                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                                        if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, string.Empty);
                                        mv.SaveData();
                                    }
                                    catch { mv.SaveData("true"); }
                                }
                                else
                                {
                                    mv.SaveData("true");
                                }
                            }
                        }
                    }
                    catch { }

                    dp.Hide();
                }
                else
                {
                    // Ensure DataContext is initialized so XML auto-load can run
                    try
                    {
                        var pane = BGKManagerPaneProvider.BGKManagerCtrlInstance;
                        if (pane != null)
                        {
                            try { pane.InitDataContext(commandData.Application, null); } catch { }
                        }
                    }
                    catch { }

                    dp.Show();
                }
            }
            catch (Exception msg)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Warnung", msg.Message);
            }
            return Result.Succeeded;
        }
    }
}
