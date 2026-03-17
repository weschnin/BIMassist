using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMassist.Commands;
using BIMassist.Core;
using BIMassist.Helpers;
using BIMassist.ViewModels;
using BIMassist.Views;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BIMassist
{
    internal class App : IExternalApplication
    {

        public static MetadataUserControl MetadataCtrl;
        public static Hauptfenster BGKManagerCtrl;
        bool MetadataCtrlPane = false;
        bool BGKManagerCtrlPane = false;

        internal static App _app = null;
        public static App Instance
        {
            get { return _app; }
        }
        // keep track of last active document path to detect project/document switches
        private string _lastActiveDocumentPath = null;
        public Result OnStartup(UIControlledApplication a)
        {
            _app = this;

            string tabName = "BIMassist";
            PushButtonData btnData;

            a.CreateRibbonTab(tabName);
            var panel = a.CreateRibbonPanel(tabName, "BIMassist");

            try
            {
                a.ViewActivated += new EventHandler<ViewActivatedEventArgs>(viewActivated);

                // Register dockable panes. Do NOT instantiate the pane UI here (InitializeComponent can throw
                // because WPF visual tree isn't available at startup). The PaneProvider will create the UI when Revit
                // requests it during registration.
                a.RegisterDockablePane(new DockablePaneId(GuidCollection.GetMetadataDockablePaneID()),
                    "Metadaten", new MetadataPaneProvider());

                a.RegisterDockablePane(new DockablePaneId(GuidCollection.GetBGKManagerDockablePaneID()),
                    "BGK-Manager", new BGKManagerPaneProvider());
            }
            catch
            {
                
            }


            try
            {

                // BGK Manager

                btnData = new PushButtonData("BGK-Manager", "BGK-Manager", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.BGKManagerCommandStartup")
                {
                    ToolTip = "BGK-Manager öffnen",
                    LongDescription = "Öffnet das BGK-Manager Fenster",
                    Image = GetImageSource("label_16px.png"),
                    LargeImage = GetImageSource("label_24px.png")
                };
                panel.AddItem(btnData);

                //Matadata-Funktionen

                btnData = new PushButtonData("Metadaten", "Metadaten", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.MetadataCommandStartup")
                {
                    ToolTip = "Metadaten lesen/bearbeiten",
                    LongDescription = "Metadaten der Familien lesen/bearbeiten",
                    Image = GetImageSource("metadata_16px.png"),
                    LargeImage = GetImageSource("metadata_32px.png")
                };
                panel.AddItem(btnData);

                // Geometrie-Funktionen

                PulldownButtonData PanelgroupData = new PulldownButtonData("Geometrien", "Geometrien")
                {
                    Image = GetImageSource("geometrie_16px.png"),
                    LargeImage = GetImageSource("geometrie_32px.png"),
                };
                PulldownButton pulldownGroup = panel.AddItem(PanelgroupData) as PulldownButton;

                btnData = new PushButtonData("Neue Familie aus gewählten Geometrien erstellen", "Neue Familie aus gewählten Geometrien erstellen", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.SolidsToNewFamilyCommand")
                {
                    ToolTip = "Neue Familie aus gewählten Geometrien erstellen",
                    LongDescription = "Neue Familie aus gewählten Geometrien erstellen",
                };
                pulldownGroup.AddPushButton(btnData);

                btnData = new PushButtonData("Gewählte Geometrien zu einer Familie hinzufügen", "Gewählte Geometrien zu einer Familie hinzufügen", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.SolidsToFamilyCommand")
                {
                    ToolTip = "Gewählte Geometrien zu einer Familie hinzufügen",
                    LongDescription = "Gewählte Geometrien zu einer Familie hinzufügen",
                };
                pulldownGroup.AddPushButton(btnData);

                btnData = new PushButtonData("Eine Extrusion an selektierten Fläche erstellen", "Eine Extrusion an selektierten Fläche erstellen", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.CreateExtrusionCommand")
                {
                    ToolTip = "Eine Extrusion an selektierten Fläche erstellen",
                    LongDescription = "Eine Extrusion an selektierten Fläche erstellen",
                };
                pulldownGroup.AddPushButton(btnData);

                // Materialien-Funktionen

                PanelgroupData = new PulldownButtonData("Materialien", "Materialien")
                {
                    Image = GetImageSource("material_16px.png"),
                    LargeImage = GetImageSource("material_32px.png"),
                    ToolTip = "Materialien Werkzeuge",
                    LongDescription = "Zusätzliche Materialien Werkzeuge",
                };
                pulldownGroup = panel.AddItem(PanelgroupData) as PulldownButton;

                btnData = new PushButtonData("Standard - Material bereinigen", "Standard - Material bereinigen", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.MaterialBereinigenCommand")
                {
                    ToolTip = "Standard - Material bereinigen",
                    LongDescription = "Materialbibliothek vom Element \"Standard\" bereinigen",
                };
                pulldownGroup.AddPushButton(btnData);

                btnData = new PushButtonData("Nicht verwendete Materialen entfernen", "Nicht verwendete Materialen entfernen", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.DeleteUnusedMaterialsWpfCommand")
                {
                    ToolTip = "Nicht verwendete Materialen entfernen",
                    LongDescription = "Nicht verwendete Materialen entfernen",
                };
                pulldownGroup.AddPushButton(btnData);

                // Schnittboxfunktionen

                PanelgroupData = new PulldownButtonData("3D Schnittbereich", "3D Schnittbereich")
                {
                    ToolTip = "3D Schnittberecih funktionen",
                    LongDescription = "3D Schnittberecih funktionen",
                    Image = GetImageSource("3D Box.png"), //GetImageSource(_3D_Box),
                    LargeImage = GetImageSource("3D Box.png"),  //GetImageSource(_3D_Box),
                };
                pulldownGroup = panel.AddItem(PanelgroupData) as PulldownButton;

                btnData = new PushButtonData("Schnittbereich ausrichten", "Schnittbereich ausrichten", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.Ausrichtung3DCommand");
                pulldownGroup.AddPushButton(btnData);

                btnData = new PushButtonData("Schnittbereich speichern", "Schnittbereich speichern", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.SaveSectionBoxCommand");
                pulldownGroup.AddPushButton(btnData);

                btnData = new PushButtonData("Schnittbereich verwalten/auswählen", "Schnittbereich verwalten/auswählen", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.RestoreSectionBoxCommand");
                pulldownGroup.AddPushButton(btnData);

                panel.AddSeparator();

            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Meldung", ex.Message);
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            try
            {
                // When Revit is shutting down, there may be no active document/UI anymore.
                // Avoid prompting in that case.
                // We don't have a valid UIApplication here (only ControlledApplication).
                // Be conservative: if there is no known data file or no data loaded, don't prompt.
                // This avoids prompts after all documents are already closed.
                if (BGKManagerPaneProvider.BGKManagerCtrlInstance?.DataContext is MainViewModel mv2)
                {
                    if (mv2.Baugruppen == null || mv2.Baugruppen.Count == 0)
                        return Result.Succeeded;
                }

                // If BGK manager has unsaved changes, ask to save on shutdown
                if (BGKManagerPaneProvider.BGKManagerCtrlInstance != null && BGKManagerPaneProvider.BGKManagerCtrlInstance.DataContext is MainViewModel mv)
                {
                    if (mv.changed)
                    {
                        var td = new Autodesk.Revit.UI.TaskDialog("Änderungen speichern") { MainInstruction = "Die BGK-Daten wurden verändert. Sollen diese vor dem Schließen gespeichert werden?" };
                        td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                        var tdRes = td.Show();
                        if (tdRes == TaskDialogResult.Yes)
                        {
                            var settingsPath = Properties.Settings.Default.PfadBGKDatei;
                            if (!string.IsNullOrWhiteSpace(settingsPath))
                            {
                                try
                                {
                                    mv.EigeneDaten = settingsPath;
                                    // create file if not exists so SaveData Truncate works
                                    var dir = Path.GetDirectoryName(settingsPath);
                                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                                    if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, string.Empty);
                                    mv.SaveData();
                                }
                                catch { }
                            }
                            else
                            {
                                // fallback: show save dialog
                                mv.SaveData("true");
                            }
                        }
                        // Prevent repeated prompts during shutdown
                        mv.changed = false;
                    }
                }
            }
            catch { }
            return Result.Succeeded;
        }

        public static ImageSource GetImageSource(string name)
        {
            // Load images as WPF resources via pack URI from the AddIn assembly
            var uri = new Uri($"pack://application:,,,/BIMassist;component/Resources/{name}", UriKind.Absolute);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze(); // für Revit wichtig

            return bitmap;
        }

        private void viewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                UIApplication uiapp = sender as UIApplication;

                // If there's no document (e.g. all documents closed), don't run project-switch logic
                // and don't trigger save prompts.
                if (uiapp?.ActiveUIDocument?.Document == null)
                    return;
                
                if (MetadataPaneProvider.MetadataCtrlInstance != null && uiapp?.ActiveUIDocument != null)
                {
                    MetadataPaneProvider.MetadataCtrlInstance.DataContext = new MetadataViewModel(uiapp);
                }

                 if (BGKManagerPaneProvider.BGKManagerCtrlInstance != null)
                {
                    // Detect project/document switch: prompt to save BGK changes if any
                    try
                    {
                        var doc = uiapp.ActiveUIDocument.Document;
                        var currentPath = !string.IsNullOrWhiteSpace(doc?.PathName) ? doc.PathName : doc?.Title ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(_lastActiveDocumentPath)
                            && !_lastActiveDocumentPath.Equals(currentPath, System.StringComparison.OrdinalIgnoreCase))
                        {
                            var pane = BGKManagerPaneProvider.BGKManagerCtrlInstance;
                            if (pane != null && pane.DataContext is MainViewModel mv && mv.changed)
                            {
                                // Only prompt if data were actually loaded previously
                                if (!string.IsNullOrWhiteSpace(mv.EigeneDaten) && File.Exists(mv.EigeneDaten))
                                {
                                    var td2 = new Autodesk.Revit.UI.TaskDialog("Änderungen speichern") { MainInstruction = "Die BGK-Daten wurden verändert. Sollen diese vor dem Projektwechsel gespeichert werden?" };
                                    td2.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                                    var td2Res = td2.Show();
                                    if (td2Res == TaskDialogResult.Yes)
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
                                            catch
                                            {
                                                mv.SaveData("true");
                                            }
                                        }
                                        else
                                        {
                                            mv.SaveData("true");
                                        }
                                    }
                                    else
                                    {
                                        // User chose not to save: discard changes by re-loading configured data file
                                        var settingsPath = Properties.Settings.Default.PfadBGKDatei;
                                        try
                                        {
                                            if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
                                            {
                                                mv.LoadDataFromPath(settingsPath);
                                            }
                                        }
                                        catch { }
                                    }
                                    // Either saved or user chose not to save; don't ask again for this switch.
                                    mv.changed = false;
                                }
                            }
                        }
                        _lastActiveDocumentPath = currentPath;
                    }
                    catch { }

                    // Initialize Hauptfenster with MainViewModel via IInitData if not already initialized
                    try
                    {
                        BGKManagerPaneProvider.BGKManagerCtrlInstance.InitDataContext(uiapp, null);
                    }
                    catch
                    {
                        // If InitDataContext isn't available or fails, set DataContext as fallback
                        if (BGKManagerPaneProvider.BGKManagerCtrlInstance.DataContext == null)
                            BGKManagerPaneProvider.BGKManagerCtrlInstance.DataContext = new MainViewModel(uiapp);
                    }

                    // Ensure BGK data file (XML) from settings is loaded after project/document switch.
                    try
                    {
                        // If the pane uses MainViewModel, prefer existing setting PfadBGKDatei if MainViewModel has no EigeneDaten
                        if (BGKManagerPaneProvider.BGKManagerCtrlInstance.DataContext is MainViewModel mv)
                        {
                            var settingsPath = Properties.Settings.Default.PfadBGKDatei;
                            // If MainViewModel has no file set but PfadBGKDatei exists, apply it and load
                            if ((string.IsNullOrWhiteSpace(mv.EigeneDaten) || !File.Exists(mv.EigeneDaten))
                                && !string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
                            {
                                mv.LoadDataFromPath(settingsPath);
                            }

                            // Also if MainViewModel already has EigeneDaten but file missing and PfadBGKDatei points to a valid file, try that
                            if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath)
                                && (string.IsNullOrWhiteSpace(mv.EigeneDaten) || !File.Exists(mv.EigeneDaten)))
                            {
                                mv.LoadDataFromPath(settingsPath);
                            }
                        }
                    }
                    catch
                    {
                        // ignore loading errors here
                    }
                }

                DockablePane dp = uiapp.ActiveUIDocument.Application.GetDockablePane(new DockablePaneId(GuidCollection.GetBGKManagerDockablePaneID()));
                if (!BGKManagerCtrlPane && dp.IsShown())
                {
                    dp.Hide();
                    BGKManagerCtrlPane = true;
                }

                dp = uiapp.ActiveUIDocument.Application.GetDockablePane(new DockablePaneId(GuidCollection.GetMetadataDockablePaneID()));
                if (!MetadataCtrlPane && dp.IsShown())
                {
                    dp.Hide();
                    MetadataCtrlPane = true;
                }
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", ex.Message);
            }
        }

    }

}
