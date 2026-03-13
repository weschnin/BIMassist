using System.Windows;

namespace BIMassist.Views
{
    public partial class NamePromptWindow : Window
    {
        public string EnteredName { get; private set; }

        public NamePromptWindow()
        {
            InitializeComponent();
            Loaded += delegate { NameBox.Focus(); };
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text != null ? NameBox.Text.Trim() : string.Empty;
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show(this, "Bitte eine Bezeichnung eingeben.", "Hinweis",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            EnteredName = name;
            DialogResult = true;
            Close();
        }
    }
}
