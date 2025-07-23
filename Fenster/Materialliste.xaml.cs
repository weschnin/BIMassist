using Autodesk.Revit.DB;
using System.Windows;
using System.Windows.Controls;

namespace BIMassist.Fenster
{
    /// <summary>
    /// Interaktionslogik für Materialliste.xaml
    /// </summary>
    public partial class Materialliste : Window
    {
        public ElementId SelectedMaterialId { get; private set; }

        public Materialliste(Document doc)
        {
            InitializeComponent();
            LoadMaterials(doc);
        }

        private void LoadMaterials(Document doc)
        {
            // Materialien sammeln
            List<Material> materials = new FilteredElementCollector(doc)
                                        .OfClass(typeof(Material))
                                        .Cast<Material>()
                                        .ToList();

            // Materialien in der ListBox anzeigen
            foreach (Material material in materials)
            {
                // ListBoxItem erstellen
                ListBoxItem listBoxItem = new ListBoxItem
                {
                    Content = material.Name,
                    Tag = material.Id
                };

                MaterialListBox.Items.Add(listBoxItem);
            }

            // Ereignis für Auswahländerung
            MaterialListBox.SelectionChanged += MaterialListBox_SelectionChanged;
        }

        private void MaterialListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OkButton.IsEnabled = MaterialListBox.SelectedItem != null;
            if (MaterialListBox.SelectedItem is ListBoxItem selectedItem)
            {
                // Setze die MaterialId
                SelectedMaterialId = (ElementId)selectedItem.Tag;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
