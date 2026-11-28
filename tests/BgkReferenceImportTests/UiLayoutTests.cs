using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;

internal static class UiLayoutTests
{
    internal static void Run(XElement buttonMarkup)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var import = (Button)XamlReader.Parse(buttonMarkup.ToString());
                var panel = new WrapPanel();
                panel.Children.Add(new Button { Content = "3DText generieren", Height = 23 });
                panel.Children.Add(import);
                panel.Measure(new Size(600, 1000));
                double wideHeight = panel.DesiredSize.Height;
                panel.Measure(new Size(180, 1000));
                panel.Arrange(new Rect(0, 0, 180, panel.DesiredSize.Height));
                panel.UpdateLayout();
                if (panel.DesiredSize.Height <= wideHeight || import.DesiredSize.Width > 180 || import.DesiredSize.Height <= 30)
                    throw new Exception("The actual WPF button must wrap its full label and move below 3DText in a narrow panel");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("WPF import-button layout failed", failure);
        Console.WriteLine("PASS actual STA WPF reference-import label and toolbar layout at narrow width");
    }
}