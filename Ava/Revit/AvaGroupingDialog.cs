using System.Windows;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Revit;

/// <summary>Collects output-only grouping choices; it never edits the Revit model.</summary>
public static class AvaGroupingDialog
{
    public static QuantityGroupingSelection? Show(UIApplication application, Autodesk.Revit.DB.Document document)
    {
        var options = RevitQuantityReader.GetGroupingParameterOptions(document);
        var window = new Window
        {
            Title = "AVA – Gruppierung der Mengenermittlung",
            Width = 540,
            Height = 360,
            MinWidth = 500,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13
        };
        if (application.MainWindowHandle != IntPtr.Zero)
            new System.Windows.Interop.WindowInteropHelper(window).Owner = application.MainWindowHandle;

        var panel = new W.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new W.TextBlock
        {
            Text = "Wie sollen die Mengen im X31-Ausgabe-LV gegliedert werden?",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var byPosition = new W.RadioButton { Content = "Nach Leistungspositionen (ohne Gewerk-/Bereichsgruppen)", IsChecked = true, Margin = new Thickness(0, 4, 0, 8) };
        var byTrade = new W.RadioButton { Content = "Nach Gewerk", IsEnabled = options.Count > 0, Margin = new Thickness(0, 4, 0, 8) };
        var byArea = new W.RadioButton { Content = "Nach Baubereich", IsEnabled = options.Count > 0, Margin = new Thickness(0, 4, 0, 8) };
        var parameterLabel = new W.TextBlock { Text = "Textparameter mit Gewerk-/Bereichsbezeichnung:", Margin = new Thickness(20, 2, 0, 4), IsEnabled = false };
        var parameters = new W.ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = nameof(GroupingParameterOption.DisplayLabel),
            SelectedIndex = options.Count > 0 ? 0 : -1,
            MinHeight = 30,
            Margin = new Thickness(20, 0, 0, 8),
            IsEnabled = false
        };
        var unavailable = new W.TextBlock
        {
            Text = options.Count == 0 ? "Im aktuellen Modell wurden keine verwendbaren Textparameter gefunden." : "",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DarkRed,
            Margin = new Thickness(0, 4, 0, 8)
        };
        void RefreshParameterState()
        {
            var enabled = byTrade.IsChecked == true || byArea.IsChecked == true;
            parameterLabel.IsEnabled = enabled;
            parameters.IsEnabled = enabled && options.Count > 0;
        }
        byPosition.Checked += (_, _) => RefreshParameterState();
        byTrade.Checked += (_, _) => RefreshParameterState();
        byArea.Checked += (_, _) => RefreshParameterState();
        panel.Children.Add(byPosition); panel.Children.Add(byTrade); panel.Children.Add(byArea);
        panel.Children.Add(parameterLabel); panel.Children.Add(parameters); panel.Children.Add(unavailable);

        var buttons = new W.WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var accept = new W.Button { Content = "Weiter", IsDefault = true, MinWidth = 95, MinHeight = 30, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new W.Button { Content = "Abbrechen", IsCancel = true, MinWidth = 95, MinHeight = 30 };
        QuantityGroupingSelection? selection = null;
        accept.Click += (_, _) =>
        {
            var mode = byTrade.IsChecked == true ? QuantityGroupingMode.Trade
                : byArea.IsChecked == true ? QuantityGroupingMode.BuildingArea : QuantityGroupingMode.Position;
            if (mode != QuantityGroupingMode.Position && parameters.SelectedItem is not GroupingParameterOption)
            {
                MessageBox.Show(window, "Für die Gruppierung einen Textparameter auswählen.", "AVA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            selection = mode == QuantityGroupingMode.Position
                ? new(mode)
                : new(mode, (GroupingParameterOption)parameters.SelectedItem);
            window.DialogResult = true;
        };
        buttons.Children.Add(accept); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        window.Content = panel;
        return window.ShowDialog() == true ? selection : null;
    }
}
