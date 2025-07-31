using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Handlers;
using BIMassist.Helpers;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;

namespace BIMassist.ViewModels
{
    public class BGKManagerViewModel : INotifyPropertyChanged, IDisposable
    {
        public UIApplication UIApp { get; }
        public UIDocument UIDocument => UIApp?.ActiveUIDocument;
        public Document Document => UIDocument?.Document;

        private string _eintragungstext;
        public string Eintragungstext
        {
            get => _eintragungstext;
            set
            {
                _eintragungstext = value;
                OnPropertyChanged(nameof(Eintragungstext));
            }
        }

        private string _statusanzeige;
        public string Statusanzeige
        {
            get => _statusanzeige;
            set
            {
                _statusanzeige = value;
                OnPropertyChanged(nameof(Statusanzeige));
            }
        }

        public ICommand UebertragenCommand { get; }
        public ExternalEvent UebertragenEvent { get; }
        private BGKUebertragenHandler _uebertragenHandler;

        public BGKManagerViewModel(UIApplication uiapp)
        {
            UIApp = uiapp ?? throw new ArgumentNullException(nameof(uiapp));

            _uebertragenHandler = new BGKUebertragenHandler { ViewModel = this };
            UebertragenEvent = ExternalEvent.Create(_uebertragenHandler);
            UebertragenCommand = new RelayCommand(() => UebertragenEvent.Raise());
        }

        /// <summary>
        /// Logik für das Übertragen des Textes in die Typparameter "Kommentare"
        /// </summary>
        public void ExecuteUebertragen(UIApplication app)
        {
            var udoc = app.ActiveUIDocument;
            var doc = udoc?.Document;
            var selIds = udoc?.Selection.GetElementIds();

            if (selIds == null || selIds.Count == 0)
            {
                Statusanzeige = "Bitte mindestens ein Element selektieren und den Befehl erneut betätigen!";
                return;
            }

            int geändert = 0;

            using (Transaction t = new Transaction(doc, "BGK Eintragung in Kommentare"))
            {
                t.Start();
                foreach (var id in selIds)
                {
                    var el = doc.GetElement(id);
                    if (el == null) continue;

                    // Typparameter "Kommentare" suchen
                    Parameter commentParam = null;
                    if (el is ElementType)
                        commentParam = el.LookupParameter("Kommentare");
                    else
                        commentParam = el.GetTypeId() != ElementId.InvalidElementId
                            ? doc.GetElement(el.GetTypeId())?.LookupParameter("Typenkommentare")
                            : null;

                    if (commentParam != null && !commentParam.IsReadOnly)
                    {
                        commentParam.Set(Eintragungstext ?? "");
                        geändert++;
                    }
                }
                t.Commit();
            }

            if (geändert == 0)
                Statusanzeige = "Keine 'Kommentare'-Typparameter gefunden oder bearbeitbar.";
            else
                Statusanzeige = $"Text in {geändert} Element-Typ(en) eingetragen.";
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose() { /* falls benötigt */ }
    }
}
