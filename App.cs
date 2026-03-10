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
        public static BGKManagerUserControl BGKManagerCtrl;
        bool MetadataCtrlPane = false;
        bool BGKManagerCtrlPane = false;

        internal static App _app = null;
        public static App Instance
        {
            get { return _app; }
        }
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

                MetadataCtrl = new MetadataUserControl();
                a.RegisterDockablePane(new DockablePaneId(GuidCollection.GetMetadataDockablePaneID()),
                    "Metadaten",
                    new MetadataPaneProvider());

                BGKManagerCtrl = new BGKManagerUserControl();
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
                TaskDialog.Show("Meldung", ex.Message);
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            return Result.Succeeded;
        }

        public static ImageSource GetImageSource(string name)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"BIMassist.Resources.{name}";

            using Stream stream = assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
                throw new Exception("Ressource nicht gefunden: " + resourceName);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
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
                
                if (MetadataPaneProvider.MetadataCtrlInstance != null && uiapp?.ActiveUIDocument != null)
                {
                    MetadataPaneProvider.MetadataCtrlInstance.DataContext = new MetadataViewModel(uiapp);
                }

                if (BGKManagerPaneProvider.BGKManagerCtrlInstance != null && uiapp?.ActiveUIDocument != null)
                {
                    BGKManagerPaneProvider.BGKManagerCtrlInstance.DataContext = new BGKManagerViewModel(uiapp);
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
                TaskDialog.Show("Error", ex.Message);
            }
        }

    }

}
