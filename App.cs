using System.Reflection;
using Autodesk.Revit.UI;
using System;
using System.IO;
using System.Windows;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace BIMassist
{
    internal class App : IExternalApplication
    {
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

                btnData = new PushButtonData("Test", "Test", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.Test_Command")
                {
                    ToolTip = "Test",
                    LongDescription = "Test",
                    Image = GetImageSource("arrow_16px.png"), //GetImageSource("Resources.add_32px),
                    LargeImage = GetImageSource("arrow_24px.png")
                };
                panel.AddItem(btnData);

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
    }

}
