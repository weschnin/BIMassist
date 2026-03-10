using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace BIMassist.Views
{
    public partial class SnapshotPickerWindow : Window
    {
        public SectionBoxSnapshot Selected { get; private set; }

        public SnapshotPickerWindow(IEnumerable<SectionBoxSnapshot> items)
        {
            InitializeComponent();
            List.ItemsSource = items;
        }

        private void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is SectionBoxSnapshot s)
            {
                Selected = s;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show(this, "Bitte zuerst einen Eintrag auswählen.", "Hinweis",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
