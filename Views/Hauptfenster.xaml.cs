using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;
using System.Windows.Controls;
using System.IO;
using System.Windows;

namespace BIMassist
{
    public partial class Hauptfenster : Page, IDockablePaneProvider, BIMassist.Core.IInitData
    {
        public Hauptfenster()
        {
            InitializeComponent();
        }

        public void SaveData()
        {
            //(DataContext as MainViewModel)?.OnWindowClosing();
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.VisibleByDefault = false;
            data.EditorInteraction = new EditorInteraction(EditorInteractionType.KeepAlive);
            data.InitialState = new DockablePaneState() { DockPosition = DockPosition.Tabbed, TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser };
        }

        public void InitDataContext(UIApplication uiapp, string str)
        {
            // Initialize only once to preserve current state between document/view changes.
            if (DataContext == null)
            {
                DataContext = new MainViewModel(uiapp);
                // Try to auto-load last used BGK XML from settings (PfadBGKDatei) if available
                // Will also be called from Loaded event as backup
                TryAutoLoadSavedPath();
            }
            else
            {
                try
                {
                    if (DataContext is MainViewModel mv)
                    {
                        mv.RefreshDocumentContext(uiapp);
                    }

                    var settingsPath = Properties.Settings.Default.PfadBGKDatei;
                    if (DataContext is MainViewModel existingVm
                        && (existingVm.Baugruppen == null || existingVm.Baugruppen.Count == 0)
                        && !string.IsNullOrWhiteSpace(settingsPath)
                        && File.Exists(settingsPath))
                    {
                        TryAutoLoadSavedPath();
                    }
                }
                catch
                {
                }
            }
        }

        private void TryAutoLoadSavedPath()
        {
            try
            {
                var mv = DataContext as MainViewModel;
                var settingsPath = Properties.Settings.Default.PfadBGKDatei;

                if (mv == null) return;
                if (string.IsNullOrWhiteSpace(settingsPath)) return;
                if (!File.Exists(settingsPath)) return;

                // Skip auto-load only if data are already loaded and the current file is valid.
                // EigeneDaten may be set from settings even though nothing was loaded yet.
                if (mv.Baugruppen != null && mv.Baugruppen.Count > 0
                    && !string.IsNullOrWhiteSpace(mv.EigeneDaten)
                    && File.Exists(mv.EigeneDaten))
                {
                    return;
                }

                mv.LoadDataFromPath(settingsPath);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", ex.Message);
            }
        }
    }
}
