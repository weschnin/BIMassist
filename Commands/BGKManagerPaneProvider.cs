using Autodesk.Revit.UI;
using BIMassist.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BIMassist.Commands
{
    public class BGKManagerPaneProvider : IDockablePaneProvider
    {
        public static BIMassist.Hauptfenster BGKManagerCtrlInstance;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            BGKManagerCtrlInstance = new BIMassist.Hauptfenster();
            data.FrameworkElement = BGKManagerCtrlInstance;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Tabbed,
                TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
            };
        }
    }
}
