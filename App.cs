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

                btnData = new PushButtonData("BGK-Manager", "BGK-Manager", Assembly.GetExecutingAssembly().Location, "BIMassist.Commands.TestCommand")
                {
                    ToolTip = "Organisation der Baugruppenkennzeichen",
                    LongDescription = "Organisation der Baugruppenkennzeichen",
                    Image = GetImageSource(Resource.add_32px),
                    //LargeImage = GetImageSource(Resources.Label_24px)
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


        private ImageSource GetImageSource(System.Drawing.Image img)
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                img.GetHbitmap(),
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        //private ImageSource GetImageSource(byte[] img)
        //{
        //    if (img == null || img.Length == 0)
        //        throw new ArgumentException("Byte array is null or empty");

        //    using (MemoryStream memoryStream = new MemoryStream(img))
        //    {
        //        using (Bitmap bitmap = new Bitmap(memoryStream))
        //        {
        //            IntPtr hBitmap = bitmap.GetHbitmap();
        //            try
        //            {
        //                return Imaging.CreateBitmapSourceFromHBitmap(
        //                    hBitmap,
        //                    IntPtr.Zero,
        //                    Int32Rect.Empty,
        //                    BitmapSizeOptions.FromEmptyOptions());
        //            }
        //            finally
        //            {
        //                DeleteObject(hBitmap); // Ressourcen freigeben
        //            }
        //        }
        //    }
        //}

        //[DllImport("gdi32.dll")]
        //[return: MarshalAs(UnmanagedType.Bool)]
        //private static extern bool DeleteObject(IntPtr hObject);
    }

}
