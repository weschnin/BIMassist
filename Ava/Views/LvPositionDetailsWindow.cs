using System.Windows;
using BIMassist.Ava.Core;
using W = System.Windows.Controls;

namespace BIMassist.Ava.Views;

/// <summary>Separate read-only details; opening it neither accepts nor closes its owner.</summary>
internal sealed class LvPositionDetailsWindow : Window
{
    internal LvPositionDetailsWindow(Catalog<LvEntry> catalog, LvEntry entry)
    {
        Title = "LV-Langtext – " + LvPositionDisplay.Heading(entry);
        Width = 780; Height = 600; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new W.DockPanel { Margin = new Thickness(14) };
        var close = EditorUi.Button("Schließen", Close);
        W.DockPanel.SetDock(close, W.Dock.Bottom); root.Children.Add(close);
        root.Children.Add(new W.TextBox
        {
            Text = LvPositionDisplay.Details(catalog, entry), IsReadOnly = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = W.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = W.ScrollBarVisibility.Auto
        });
        Content = root;
    }
}