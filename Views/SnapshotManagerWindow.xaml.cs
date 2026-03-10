using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Core;
using BIMassist.Views;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace BIMassist.Views
{
    public partial class SnapshotManagerWindow : Window
    {
        private readonly UIApplication _uiApp;
        private readonly Document _doc;
        private readonly Schema _schema;

        private readonly ExternalEvent _exEvent;
        private readonly SectionBoxExternalEventHandler _handler;

        public ObservableCollection<SectionBoxSnapshot> Items { get; private set; }

        public SnapshotManagerWindow(UIApplication uiApp, Document doc, Schema schema)
        {
            InitializeComponent();
            _uiApp = uiApp;
            _doc = doc;
            _schema = schema;

            _handler = new SectionBoxExternalEventHandler
            {
                UiApp = _uiApp,
                Doc = _doc,
                SetStatus = SetStatusAndReload
            };
            _exEvent = ExternalEvent.Create(_handler);

            Items = new ObservableCollection<SectionBoxSnapshot>();
            List.ItemsSource = Items;

            LoadItems();
        }

        private void SetStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message ?? string.Empty;
            });
        }

        private void SetStatusAndReload(string message)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = message ?? string.Empty;
                LoadItems();
            });
        }

        private void LoadItems()
        {
            Items.Clear();

            var storages = new FilteredElementCollector(_doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .ToList();

            foreach (var ds in storages)
            {
                try
                {
                    var e = ds.GetEntity(_schema);
                    if (e != null && e.IsValid())
                    {
                        string name = SafeGet(e, "Name");
                        if (!string.IsNullOrEmpty(name))
                        {
                            Items.Add(new SectionBoxSnapshot { Storage = ds, Name = name });
                        }
                    }
                }
                catch
                {
                    // ignorieren – nur gültige Entities listen
                }
            }

            if (Items.Count == 0)
                SetStatus("Keine Snapshots gefunden.");
            else
                SetStatus(string.Empty);
        }

        private static string SafeGet(Entity e, string field)
        {
            try { return e.Get<string>(field); }
            catch { return string.Empty; }
        }

        // === Aktionen ===

        private void OnShowClick(object sender, RoutedEventArgs e)
        {
            var selected = List.SelectedItem as SectionBoxSnapshot;
            if (selected == null)
            {
                SetStatus("Bitte zuerst einen Eintrag auswählen.");
                return;
            }

            var view3D = _doc.ActiveView as View3D;
            if (view3D == null || view3D.IsTemplate)
            {
                SetStatus("Bitte zuerst eine passende 3D-Ansicht aktivieren.");
                return;
            }

            try
            {
                var entity = selected.Storage.GetEntity(_schema);
                if (entity == null || !entity.IsValid())
                {
                    SetStatus("Der ausgewählte Eintrag ist ungültig.");
                    return;
                }

                // Box und Ansichtorientierung im Window berechnen (ohne Transaction)
                var box = BuildBoxFromEntity(entity);

                // Orientierung aus Entity lesen
                var eyePos = DataStorageManagement.StringToXYZ(SafeGet(entity, "EyePosition"));
                var forward = DataStorageManagement.StringToXYZ(SafeGet(entity, "ForwardDirection"));
                var up = DataStorageManagement.StringToXYZ(SafeGet(entity, "UpDirection"));

                _handler.Pending = new SectionBoxAction
                {
                    Type = SectionBoxActionType.Apply,
                    StorageId = selected.Storage.Id,
                    Schema = _schema,
                    Box = box,
                    EyePosition = eyePos,
                    ForwardDirection = forward,
                    UpDirection = up
                };

                _exEvent.Raise();
            }
            catch (Exception ex)
            {
                SetStatus("Fehler: " + ex.Message);
            }
        }

        private void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var selected = List.SelectedItem as SectionBoxSnapshot;
            if (selected == null)
            {
                SetStatus("Bitte zuerst einen Eintrag auswählen.");
                return;
            }

            var prompt = new NamePromptWindow();
            prompt.Owner = this;
            // aktuellen Namen als Vorschlag
            var nameBox = prompt.FindName("NameBox") as System.Windows.Controls.TextBox;
            if (nameBox != null) nameBox.Text = selected.Name;

            if (prompt.ShowDialog() != true) return;

            var newName = prompt.EnteredName;
            if (string.IsNullOrEmpty(newName))
            {
                SetStatus("Ungültige Bezeichnung.");
                return;
            }

            // Duplikate vermeiden
            bool exists = Items.Any(i => string.Equals(i.Name, newName, StringComparison.OrdinalIgnoreCase) && i.Storage.Id != selected.Storage.Id);
            if (exists)
            {
                SetStatus("Ein Snapshot mit dieser Bezeichnung existiert bereits.");
                return;
            }

            try
            {
                _handler.Pending = new SectionBoxAction
                {
                    Type = SectionBoxActionType.Rename,
                    StorageId = selected.Storage.Id,
                    Schema = _schema,
                    NewName = newName
                };

                _exEvent.Raise();
            }
            catch (Exception ex)
            {
                SetStatus("Fehler: " + ex.Message);
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var selected = List.SelectedItem as SectionBoxSnapshot;
            if (selected == null)
            {
                SetStatus("Bitte zuerst einen Eintrag auswählen.");
                return;
            }

            var result = MessageBox.Show(this,
                "Diesen Snapshot löschen?\n\n" + selected.Name,
                "Löschen bestätigen",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                _handler.Pending = new SectionBoxAction
                {
                    Type = SectionBoxActionType.Delete,
                    StorageId = selected.Storage.Id,
                    Schema = _schema
                };

                _exEvent.Raise();
            }
            catch (Exception ex)
            {
                SetStatus("Fehler: " + ex.Message);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            // Prevent the ExternalEvent handler from invoking UI callbacks after the window is closed
            try
            {
                _handler.SetStatus = null;
                _handler.Pending = null;
            }
            catch
            {
                // ignore
            }

            Close();
        }

        private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Doppelklick = Zeigen (falls 3D-Ansicht aktiv)
            OnShowClick(sender, e);
        }

        // === SectionBox anwenden ===
        private BoundingBoxXYZ BuildBoxFromEntity(Entity entity)
        {
            XYZ localMin = DataStorageManagement.StringToXYZ(entity.Get<string>("localMin"));
            XYZ localMax = DataStorageManagement.StringToXYZ(entity.Get<string>("localMax"));
            XYZ origin = DataStorageManagement.StringToXYZ(entity.Get<string>("Origin"));
            XYZ basisX = DataStorageManagement.StringToXYZ(entity.Get<string>("BasisX"));
            XYZ basisY = DataStorageManagement.StringToXYZ(entity.Get<string>("BasisY"));
            XYZ basisZ = DataStorageManagement.StringToXYZ(entity.Get<string>("BasisZ"));

            Transform t = Transform.Identity;
            t.Origin = origin;
            t.BasisX = basisX;
            t.BasisY = basisY;
            t.BasisZ = basisZ;

            return new BoundingBoxXYZ
            {
                Transform = t,
                Min = localMin,
                Max = localMax
            };
        }
    }
}