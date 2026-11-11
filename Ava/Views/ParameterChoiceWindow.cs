using System.Windows;
using System.Windows.Controls;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Views;

internal sealed class ParameterChoiceWindow : Window
{
    private readonly List<RadioButton> choices;
    private readonly CheckBox remember;
    public string? SelectedIdentity { get; private set; }
    public bool RememberSelection => remember.IsChecked == true;

    public ParameterChoiceWindow(string parameterName, IReadOnlyList<AvaParameterCandidate> candidates)
    {
        Title = "AVA – Parameter auswählen";
        Width = 620;
        Height = Math.Min(560, 190 + candidates.Count * 54);
        MinWidth = 480;
        MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = $"Der Parameter „{parameterName}“ kommt mehrfach mit unterschiedlichen Werten vor. Welche Quelle soll verwendet werden?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        choices = new List<RadioButton>();
        var list = new StackPanel();
        foreach (var candidate in candidates)
        {
            var choice = new RadioButton
            {
                Content = new TextBlock { Text = candidate.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 530 },
                Tag = candidate.Identity,
                GroupName = "ParameterSource",
                Margin = new Thickness(4, 5, 4, 5),
                IsChecked = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            choices.Add(choice);
            list.Children.Add(choice);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330 };
        root.Children.Add(scroll);

        remember = new CheckBox
        {
            Content = "Diese Auswahl für diesen Projekttyp und diese Vorlage künftig merken",
            Margin = new Thickness(0, 12, 0, 12)
        };
        root.Children.Add(remember);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Abbrechen", MinWidth = 90, Margin = new Thickness(4), IsCancel = true };
        var accept = new Button { Content = "Auswahl übernehmen", MinWidth = 145, Margin = new Thickness(4), IsDefault = true, IsEnabled = false };
        foreach (var choice in choices)
            choice.Checked += (_, _) => accept.IsEnabled = true;
        accept.Click += (_, _) =>
        {
            SelectedIdentity = choices.FirstOrDefault(choice => choice.IsChecked == true)?.Tag as string;
            if (SelectedIdentity != null) DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        root.Children.Add(buttons);
        Content = root;
    }
}
