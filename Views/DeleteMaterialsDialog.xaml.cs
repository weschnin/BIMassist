using Autodesk.Revit.DB;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.ComponentModel;

namespace BIMassist.Fenster
{
    /// <summary>
    /// Interaktionslogik für DeleteMaterialsDialog.xaml
    /// </summary>
    public partial class DeleteMaterialsDialog : Window
    {
        private bool _internalCheckAllUpdate = false;

        public ObservableCollection<MaterialCheck> Materials { get; set; }
        public List<Material> SelectedMaterials
            => Materials.Where(m => m.IsChecked).Select(m => m.Material).ToList();

        public DeleteMaterialsDialog(List<Material> unusedMaterials)
        {
            InitializeComponent();
            Materials = new ObservableCollection<MaterialCheck>(
                unusedMaterials.Select(m => new MaterialCheck { Material = m, Name = m.Name, IsChecked = true }));
            MaterialListBox.ItemsSource = Materials;
            foreach (var item in Materials)
            {
                item.PropertyChanged += MaterialCheck_PropertyChanged;
            }
            UpdateSelectAllCheckBox();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedMaterials.Count == 0)
            {
                MessageBox.Show("Bitte mindestens ein Material auswählen.", "Hinweis", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_internalCheckAllUpdate) return;
            _internalCheckAllUpdate = true;
            foreach (var item in Materials)
                item.IsChecked = true;
            MaterialListBox.Items.Refresh();
            _internalCheckAllUpdate = false;
        }
        
        private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_internalCheckAllUpdate) return;
            _internalCheckAllUpdate = true;
            foreach (var item in Materials)
                item.IsChecked = false;
            MaterialListBox.Items.Refresh();
            _internalCheckAllUpdate = false;
        }

        private void MaterialCheck_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MaterialCheck.IsChecked))
            {
                UpdateSelectAllCheckBox();
            }
        }

        private void UpdateSelectAllCheckBox()
        {
            _internalCheckAllUpdate = true;
            if (Materials.All(m => m.IsChecked))
                SelectAllCheckBox.IsChecked = true;
            else if (Materials.All(m => !m.IsChecked))
                SelectAllCheckBox.IsChecked = false;
            else
                SelectAllCheckBox.IsChecked = null; // indeterminate Zustand
            _internalCheckAllUpdate = false;
        }

        public class MaterialCheck : System.ComponentModel.INotifyPropertyChanged
        {
            public Autodesk.Revit.DB.Material Material { get; set; }
            public string Name { get; set; }
            private bool _isChecked = true;
            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked != value)
                    {
                        _isChecked = value;
                        OnPropertyChanged(nameof(IsChecked));
                    }
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string propertyName)
                => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }

    }

    
}
