using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Core;
using BIMassist.Handlers;
using BIMassist.Helpers;
using BIMassist.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace BIMassist.ViewModels
{
    // =========================
    // ViewModel
    // =========================
    public class MetadataViewModel : INotifyPropertyChanged, IDisposable
    {
        public UIApplication UIApp { get; }
        public UIDocument UIDocument => UIApp?.ActiveUIDocument;
        public Document Document => UIDocument?.Document;

        private DispatcherTimer _selectionTimer;
        private ElementId _lastSelectionId;

        public string DeveloperFirstName { get; set; }
        public string DeveloperLastName { get; set; }
        public string Owner { get; set; }
        public string Email { get; set; }
        public DateTime? EditDate { get; set; }
        public string Description { get; set; }
        public string PasswordInput { get; set; }
        public bool IsReadOnly { get; set; } = false;
        public string StatusMessage { get; set; }
        public string FamilyName { get; set; }
        public ICommand UnlockCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CopyCommand { get; }

        // ExternalEvent Handlerd

        public ExternalEvent SaveEvent { get; }
        public ExternalEvent CopyEvent { get; }
        public ExternalEvent UnlockEvent { get; }

        private SaveMetadataHandler _saveHandler;
        private CopyMetadataHandler _copyHandler;
        private UnlockMetadataHandler _unlockHandler;


        public MetadataViewModel(UIApplication uiapp)
        {
            UIApp = uiapp ?? throw new ArgumentNullException(nameof(uiapp));

            // Commands initialisieren
            _saveHandler = new SaveMetadataHandler { ViewModel = this };
            _copyHandler = new CopyMetadataHandler { ViewModel = this };
            _unlockHandler = new UnlockMetadataHandler { ViewModel = this };

            SaveEvent = ExternalEvent.Create(_saveHandler);
            CopyEvent = ExternalEvent.Create(_copyHandler);
            UnlockEvent = ExternalEvent.Create(_unlockHandler);

            SaveCommand = new RelayCommand(() => SaveEvent.Raise());
            CopyCommand = new RelayCommand(() => CopyEvent.Raise());
            UnlockCommand = new RelayCommand(() => UnlockEvent.Raise());

            // Timer für Family-Auswahl
            _selectionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _selectionTimer.Tick += (s, e) => RefreshSelectedFamily();
            _selectionTimer.Start();
        }

        public void ExecuteSave(UIApplication uiapp)
        {
            var family = GetSelectedFamily();
            if (family == null) return;

            // Prüfe Passwortschutz wie bisher
            var entity = MetadataStorage.LoadMetadata(Document, family);
            var hasPw = false;
            string oldHash = "";
            if (entity != null)
            {
                oldHash = entity.Get<string>("PasswordHash");
                hasPw = !string.IsNullOrEmpty(oldHash);
            }

            if (hasPw)
            {
                string enteredHash = MetadataStorage.ComputeMD5(PasswordInput ?? "");
                if (enteredHash != oldHash)
                {
                    StatusMessage = "Ungültiges Passwort. Änderungen wurden nicht gespeichert!";
                    OnPropertyChanged(nameof(StatusMessage));
                    return;
                }
            }

            Dictionary<string, string> values = new Dictionary<string, string>
            {
                { "DeveloperFirstName", DeveloperFirstName },
                { "DeveloperLastName", DeveloperLastName },
                { "Owner", Owner },
                { "Email", Email },
                { "EditDate", EditDate?.ToString("dd.MM.yyyy") ?? "" },
                { "Description", Description },
                { "PasswordHash", MetadataStorage.ComputeMD5(PasswordInput) },
            };

            // Neue Speicher-Logik:
            bool ok = FamilyFileHelper.OpenFamilyAndSaveMetadata(uiapp, family, values);

            if (ok)
            {
                IsReadOnly = true;
                PasswordInput = string.Empty;
                StatusMessage = "Gespeichert (in Familie-Datei)!";
            }
            else
            {
                StatusMessage = "Speichern abgebrochen oder fehlgeschlagen!";
            }

            OnPropertyChanged("IsReadOnly");
            OnPropertyChanged("PasswordInput");
            OnPropertyChanged(nameof(StatusMessage));
        }

        public void ExecuteCopy(UIApplication uiapp)
        {
            var selectedIds = UIDocument.Selection.GetElementIds().ToList();
            if (selectedIds.Count < 2)
            {
                StatusMessage = "Bitte wählen Sie zuerst die Quellfamilie, dann Zielfamilien!";
                OnPropertyChanged(nameof(StatusMessage));
                return;
            }

            // 1. Quelle: Immer das erste Element
            var firstInst = Document.GetElement(selectedIds[0]) as FamilyInstance;
            var sourceFamily = firstInst?.Symbol?.Family;
            if (sourceFamily == null || !sourceFamily.IsEditable)
            {
                StatusMessage = "Die Quellfamilie ist keine ladbare Familie.";
                OnPropertyChanged(nameof(StatusMessage));
                return;
            }

            // 2. Metadaten aus rfa der Quellfamilie laden!
            Dictionary<string, string> sourceValues = FamilyFileHelper.LoadMetadataFromFamilyFile(uiapp, sourceFamily);
            if (sourceValues == null)
            {
                StatusMessage = "In der Quellfamilie wurden keine Metadaten gefunden.";
                OnPropertyChanged(nameof(StatusMessage));
                return;
            }

            // 3. Prüfe Passwortschutz
            var sourcePwHash = sourceValues.GetValueOrDefault("PasswordHash", "");
            if (!string.IsNullOrEmpty(sourcePwHash))
            {
                string enteredHash = MetadataStorage.ComputeMD5(PasswordInput ?? "");
                if (enteredHash != sourcePwHash)
                {
                    StatusMessage = "Ungültiges Passwort – Kopieren abgebrochen!";
                    OnPropertyChanged(nameof(StatusMessage));
                    return;
                }
            }

            // 4. Kopieren auf alle weiteren Ziel-Familien
            int copied = 0;
            for (int i = 1; i < selectedIds.Count; i++)
            {
                var inst = Document.GetElement(selectedIds[i]) as FamilyInstance;
                var targetFamily = inst?.Symbol?.Family;
                if (targetFamily != null && targetFamily.IsEditable)
                {
                    bool ok = FamilyFileHelper.OpenFamilyAndSaveMetadata(uiapp, targetFamily, sourceValues);
                    if (ok) copied++;
                }
            }

            if (copied == 0)
                StatusMessage = "Keine gültigen Zielfamilien gefunden!";
            else if (copied == 1)
                StatusMessage = "Metadaten auf eine Familie kopiert.";
            else
                StatusMessage = $"Metadaten auf {copied} Familien kopiert.";

            OnPropertyChanged(nameof(StatusMessage));
        }

        public void Unlock()
        {
            var family = GetSelectedFamily();
            var entity = MetadataStorage.LoadMetadata(Document, family);
            if (entity != null)
            {
                string storedHash = entity.Get<string>("PasswordHash");
                string enteredHash = MetadataStorage.ComputeMD5(PasswordInput);
                if (storedHash == enteredHash)
                {
                    IsReadOnly = false;
                    PasswordInput = string.Empty;
                    StatusMessage = "Bearbeitung freigegeben.";
                    OnPropertyChanged("PasswordInput");
                    OnPropertyChanged("IsReadOnly");
                    OnPropertyChanged(nameof(StatusMessage));
                }
                else
                {
                    StatusMessage = "Ungültiges Passwort.";
                    OnPropertyChanged(nameof(StatusMessage));
                }
            }
            else
            {
                StatusMessage = "Keine Metadaten gefunden.";
                OnPropertyChanged(nameof(StatusMessage));
            }
        }

        private void RefreshSelectedFamily()
        {
            var uiapp = UIDocument.Application;
            var paneId = new DockablePaneId(GuidCollection.GetMetadataDockablePaneID());
            var pane = uiapp.GetDockablePane(paneId);

            if (pane == null || !pane.IsShown())
                return;

            if (UIDocument == null || UIDocument.Selection == null)
            {
                StatusMessage = "Kein aktives Revit-Dokument!";
                OnPropertyChanged(nameof(StatusMessage));
                return;
            }

            var selIds = UIDocument.Selection.GetElementIds();
            if (selIds == null || selIds.Count == 0)
                return;

            var selId = selIds.FirstOrDefault();
            if (selId == null || selId == _lastSelectionId)
                return;

            _lastSelectionId = selId;

            var famInst = Document.GetElement(selId) as FamilyInstance;
            var family = famInst?.Symbol?.Family;

            if (family == null || !family.IsEditable)
            {
                StatusMessage = "Bitte wählen Sie eine ladbare Familie (keine Systemfamilie).";
                OnPropertyChanged(nameof(StatusMessage));
                return;
            }

            FamilyName = family.Name;
            OnPropertyChanged(nameof(FamilyName));

            // Metadaten aus der rfa holen:
            Dictionary<string, string> meta = FamilyFileHelper.LoadMetadataFromFamilyFile(uiapp, family);
            if (meta == null)
            {
                // Wenn keine Metadaten in rfa gefunden
                DeveloperFirstName = "";
                DeveloperLastName = "";
                Owner = "";
                Email = "";
                EditDate = null;
                Description = "";
                PasswordInput = "";
                IsReadOnly = false;
                StatusMessage = "Keine Metadaten in der Familie gefunden.";
            }
            else
            {
                DeveloperFirstName = meta.GetValueOrDefault("DeveloperFirstName", "");
                DeveloperLastName = meta.GetValueOrDefault("DeveloperLastName", "");
                Owner = meta.GetValueOrDefault("Owner", "");
                Email = meta.GetValueOrDefault("Email", "");
                if (DateTime.TryParse(meta.GetValueOrDefault("EditDate", ""), out var date))
                    EditDate = date;
                else
                    EditDate = null;
                Description = meta.GetValueOrDefault("Description", "");
                var pwHash = meta.GetValueOrDefault("PasswordHash", "");
                bool hasPassword = !string.IsNullOrEmpty(pwHash);

                PasswordInput = "";  // Niemals anzeigen!
                IsReadOnly = hasPassword;

                StatusMessage = hasPassword
                    ? "Metadaten sind passwortgeschützt."
                    : "Metadaten geladen (ohne Passwortschutz).";
            }

            OnPropertyChanged(nameof(DeveloperFirstName));
            OnPropertyChanged(nameof(DeveloperLastName));
            OnPropertyChanged(nameof(Owner));
            OnPropertyChanged(nameof(Email));
            OnPropertyChanged(nameof(EditDate));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(PasswordInput));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(StatusMessage));
        }

        public Family GetSelectedFamily()
        {
            var selIds = UIDocument.Selection.GetElementIds();
            if (selIds == null || selIds.Count == 0)
            {
                StatusMessage = "Bitte wählen Sie eine ladbare Familie!";
                OnPropertyChanged(nameof(StatusMessage));
                return null;
            }

            var selId = selIds.FirstOrDefault();
            if (selId == null)
            {
                StatusMessage = "Bitte wählen Sie eine ladbare Familie!";
                OnPropertyChanged(nameof(StatusMessage));
                return null;
            }

            var famInst = Document.GetElement(selId) as FamilyInstance;
            var family = famInst?.Symbol?.Family;

            if (family != null && family.IsEditable)
                return family;

            StatusMessage = "Bitte wählen Sie eine ladbare Familie!";
            OnPropertyChanged(nameof(StatusMessage));
            return null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            if (_selectionTimer != null)
            {
                _selectionTimer?.Stop();
                _selectionTimer.Tick -= (s, e) => RefreshSelectedFamily();
            }
        }
    }
}
