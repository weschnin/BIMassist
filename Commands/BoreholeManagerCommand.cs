using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace BIMassist.Commands
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class BoreholeManagerCommand : IExternalCommand
    {
        private static Views.BoreholeManagerWindow _window;
        private static ViewModels.BoreholeManagerViewModel _vm;
        private static readonly object _lock = new();

        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            try
            {
                var uiapp = commandData.Application;

                lock (_lock)
                {
                    if (_window == null || !_window.IsLoaded)
                    {
                        _vm = new ViewModels.BoreholeManagerViewModel(uiapp);

                        _window = new Views.BoreholeManagerWindow
                        {
                            DataContext = _vm,
                            ShowInTaskbar = false,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner
                        };

                        var hwnd = uiapp.MainWindowHandle;
                        if (hwnd != IntPtr.Zero)
                            new WindowInteropHelper(_window) { Owner = hwnd };

                        _window.Closed += (s, e) =>
                        {
                            _window = null;
                            _vm = null;
                        };

                        _window.Show();
                        BringToFront(_window);
                    }
                    else
                    {
                        BringToFront(_window);
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMassist", $"Fehler: {ex.Message}");
                return Result.Failed;
            }
        }

        public static void ActivateWindow()
        {
            if (_window == null) return;

            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;

            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();

            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }

        private static void BringToFront(Window win)
        {
            if (win == null) return;

            if (!win.IsVisible) win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;

            win.Activate();
            win.Topmost = true;
            win.Topmost = false;
            win.Focus();

            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
