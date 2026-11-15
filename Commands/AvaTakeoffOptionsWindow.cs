using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIMassist.Ava.Views;

internal sealed class AvaTakeoffOptionsWindow : Window
{
    private readonly CheckBox visibleOnly;
    public bool VisibleOnly => visibleOnly.IsChecked == true;

    private AvaTakeoffOptionsWindow(string viewName)
    {
        Title = "AVA – Umfang der Mengenermittlung";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "Wählen Sie den Umfang der Mengenermittlung.",
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 12)
        });
        visibleOnly = new CheckBox
        {
            Content = "Nur sichtbare Elemente in der aktiven Ansicht berücksichtigen",
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 8),
            ToolTip = "Revit-Ansichtssichtbarkeit; dauerhaft und temporär ausgeblendete Elemente werden ausgeschlossen. Zoom/Pan wird ignoriert. Geometrische Überdeckung wird nicht geprüft. Standardmäßig werden alle Modellelement-Instanzen im Hauptmodell berücksichtigt."
        };
        root.Children.Add(visibleOnly);
        root.Children.Add(new TextBlock
        {
            Text = "Aktive Ansicht: " + (string.IsNullOrWhiteSpace(viewName) ? "(unbekannt)" : viewName),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 0, 0, 14)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Abbrechen", MinWidth = 90, Margin = new Thickness(4), IsCancel = true });
        var accept = new Button { Content = "Weiter", MinWidth = 90, Margin = new Thickness(4), IsDefault = true };
        accept.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(accept);
        root.Children.Add(buttons);
        Content = root;
    }

    public static bool? Show(UIApplication application, View activeView, out bool selected)
    {
        var dialog = new AvaTakeoffOptionsWindow(activeView?.Name ?? "");
        var handle = application.MainWindowHandle;
        if (handle != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(dialog).Owner = handle;
        var result = dialog.ShowDialog();
        selected = dialog.VisibleOnly;
        return result;
    }
}
