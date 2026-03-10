using System;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace BIMassist.Views
{
    public static class WpfOwner
    {
        public static void ShowModeless(Window window, UIApplication uiapp)
        {
            IntPtr revitHandle = uiapp.MainWindowHandle;
            new WindowInteropHelper(window).Owner = revitHandle;
            window.Show();
        }

        public static bool? ShowDialog(Window window, UIApplication uiapp)
        {
            IntPtr revitHandle = uiapp.MainWindowHandle;
            new WindowInteropHelper(window).Owner = revitHandle;
            return window.ShowDialog();
        }
    }
}