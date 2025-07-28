using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.ViewModels;
using BIMassist.Views;

namespace BIMassist.Commands
{
    // =========================
    // Dockable Pane Provider
    // =========================
    public class MetadataPaneProvider : IDockablePaneProvider
    {
        // Instanz speichern!
        public static MetadataUserControl MetadataCtrlInstance;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            MetadataCtrlInstance = new MetadataUserControl();
            data.FrameworkElement = MetadataCtrlInstance;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Tabbed,
                TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
            };
        }
    }

}
