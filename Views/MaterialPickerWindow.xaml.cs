using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace BIMassist.Views
{
    public partial class MaterialPickerWindow : Window
    {
        private readonly ObservableCollection<string> _materialNames;
        private readonly ICollectionView _materialView;

        public string SelectedMaterialName => MaterialListBox.SelectedItem as string;

        public MaterialPickerWindow(IEnumerable<string> materialNames)
        {
            InitializeComponent();

            _materialNames = new ObservableCollection<string>(
                (materialNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList());

            _materialView = CollectionViewSource.GetDefaultView(_materialNames);
            _materialView.Filter = FilterMaterialName;
            MaterialListBox.ItemsSource = _materialView;

            Loaded += (_, __) =>
            {
                SearchTextBox.Focus();
                if (_materialNames.Count > 0)
                    MaterialListBox.SelectedIndex = 0;
            };
        }

        private bool FilterMaterialName(object item)
        {
            if (item is not string name)
                return false;

            string filter = SearchTextBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            return name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _materialView.Refresh();

            if (MaterialListBox.SelectedItem == null && MaterialListBox.Items.Count > 0)
                MaterialListBox.SelectedIndex = 0;
        }

        private void MaterialListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ConfirmSelection();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmSelection();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ConfirmSelection()
        {
            if (string.IsNullOrWhiteSpace(SelectedMaterialName))
            {
                System.Windows.MessageBox.Show(this,
                    "Bitte zuerst ein Material auswählen.",
                    "Material auswählen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}
