using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BIMassist.Core;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace BIMassist.Views
{
    public partial class SectionBoxSaveWindow : Window
    {
        private readonly Document _doc;
        private readonly Schema _schema;

        public ObservableCollection<SectionBoxSnapshot> Items { get; }
        public string SelectedSnapshotName { get; private set; } = string.Empty;
        public bool OverwriteExisting { get; private set; }

        public SectionBoxSaveWindow(Document doc, Schema schema)
        {
            InitializeComponent();

            _doc = doc;
            _schema = schema;

            Items = new ObservableCollection<SectionBoxSnapshot>();
            List.ItemsSource = Items;

            LoadItems();
        }

        private void LoadItems()
        {
            Items.Clear();

            var storages = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .ToList();

            foreach (DataStorage ds in storages)
            {
                try
                {
                    Entity entity = ds.GetEntity(_schema);
                    if (entity == null || !entity.IsValid())
                        continue;

                    string name = DataStorageManagement.TryGetString(entity, "Name") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    Items.Add(new SectionBoxSnapshot
                    {
                        Storage = ds,
                        Name = name.Trim()
                    });
                }
                catch
                {
                    // Nur gültige Snapshot-Entities anzeigen.
                }
            }

            if (Items.Count == 0)
                StatusText.Text = "Keine gespeicherten Schnittboxen vorhanden. Mit 'Neu' eine neue Schnittbox speichern.";
            else
                StatusText.Text = "Vorhandene Schnittbox auswählen und überschreiben oder mit 'Neu' separat speichern.";
        }

        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            NamePromptWindow prompt = new NamePromptWindow
            {
                Owner = this
            };

            if (prompt.ShowDialog() != true)
                return;

            string newName = prompt.EnteredName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName))
            {
                StatusText.Text = "Bitte einen Namen eingeben.";
                return;
            }

            bool alreadyExists = Items.Any(item => string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase));
            if (alreadyExists)
            {
                StatusText.Text = "Dieser Name existiert bereits. Bitte vorhandenen Eintrag markieren und 'Überschreiben' verwenden.";
                return;
            }

            SelectedSnapshotName = newName;
            OverwriteExisting = false;
            DialogResult = true;
            Close();
        }

        private void OnOverwriteClick(object sender, RoutedEventArgs e)
        {
            SectionBoxSnapshot selected = List.SelectedItem as SectionBoxSnapshot;
            if (selected == null || string.IsNullOrWhiteSpace(selected.Name))
            {
                StatusText.Text = "Bitte zuerst eine gespeicherte Schnittbox auswählen.";
                return;
            }

            MessageBoxResult result = System.Windows.MessageBox.Show(
                this,
                "Die gespeicherte Schnittbox wird durch die aktuelle Schnittbox überschrieben:\n\n" + selected.Name,
                "Schnittbox überschreiben",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            SelectedSnapshotName = selected.Name.Trim();
            OverwriteExisting = true;
            DialogResult = true;
            Close();
        }

        private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OnOverwriteClick(sender, e);
        }
    }
}
