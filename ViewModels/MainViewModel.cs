using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;
using BIMassist.Models;
using BIMassist.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Collections.Specialized;
using System.Windows.Input;
using BIMassist.Helpers;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace BIMassist
{
    public class MainViewModel : ViewModelBase
    {
        private bool _suspendChangeTracking = false;

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set => Set(ref _statusMessage, value);
        }

        private CancellationTokenSource _statusCts;
        private void ShowStatus(string message, int milliseconds = 10000)
        {
            try
            {
                _statusCts?.Cancel();
                _statusCts = new CancellationTokenSource();
                var token = _statusCts.Token;

                void SetMessage(string m)
                {
                    try
                    {
                        var disp = System.Windows.Application.Current?.Dispatcher;
                        if (disp != null && !disp.CheckAccess())
                            disp.Invoke(() => StatusMessage = m ?? string.Empty);
                        else
                            StatusMessage = m ?? string.Empty;
                    }
                    catch
                    {
                        StatusMessage = m ?? string.Empty;
                    }
                }

                SetMessage(message);
                if (string.IsNullOrWhiteSpace(message)) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(milliseconds, token);
                        if (!token.IsCancellationRequested)
                            SetMessage(string.Empty);
                    }
                    catch { }
                }, token);
            }
            catch { }
        }
        #region Eigenschaften- und Variablen-Definition

        private ExternalEvent _assignEvent;
        private BgkAssignExternalEventHandler _assignHandler;

        private ObservableCollection<Baugruppe> _labels;
        public ObservableCollection<Baugruppe> Baugruppen
        {
            get => _labels;
            set { Set(ref _labels, value); }
        }

        private string _dateiname;
        public string Dateiname
        {
            get => _dateiname;
            set => Set(ref _dateiname, value);
        }

        private string _eigeneDaten;
        public string EigeneDaten
        {
            get => _eigeneDaten;
            set
            {
                if (_eigeneDaten != value)
                {
                    _eigeneDaten = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _windowwidth;
        public int WindowWidth
        {
            get => _windowwidth;
            set => Set(ref _windowwidth, value);
        }

        private int _windowheight;
        public int WindowHeight
        {
            get => _windowheight;
            set => Set(ref _windowheight, value);
        }

        private double _windowtop;
        public double WindowTop
        {
            get => _windowtop;
            set => Set(ref _windowtop, value);
        }

        private double _windowleft;
        public double WindowLeft
        {
            get => _windowleft;
            set => Set(ref _windowleft, value);
        }

        private bool _autoLoad;
        public bool AutoLoad
        {
            get => _autoLoad;
            set => Set(ref _autoLoad, value);
        }

        private bool _autoSave;
        public bool AutoSave
        {
            get => _autoSave;
            set => Set(ref _autoSave, value);
        }

        private bool _editMode;
        public bool EditMode
        {
            get => _editMode;
            set => Set(ref _editMode, value);
        }

        private bool _trvExpand;
        public bool TrvExpand
        {
            get => _trvExpand;
            set => Set(ref _trvExpand, value);
        }

        private bool _autoAktualisierunb;
        public bool AutoAktualisierung
        {
            get => _autoAktualisierunb;
            set => Set(ref _autoAktualisierunb, value);
        }

        private bool _hauptfensterTopMost;
        public bool HauptfensterTopMost
        {
            get => _hauptfensterTopMost;
            set => Set(ref _hauptfensterTopMost, value);
        }

        private string _parameterNameBGK;
        public string ParameterNameBGK
        {
            get => _parameterNameBGK;
            set
            {
                if (_parameterNameBGK != value)
                {
                    SharedParameterElement param = SharedParameterElements?.OfType<SharedParameterElement>().FirstOrDefault(s => s.Name == value);
                    Definition def = param?.GetDefinition();
                    ParameterGroupBGK = GetParameterGroupString(def);

                    Set(ref _parameterNameBGK, value);
                }
            }
        }

        private string _parameterGroupBGK;
        public string ParameterGroupBGK
        {
            get => _parameterGroupBGK;
            set => Set(ref _parameterGroupBGK, value);
        }

        private string _parameterNameTymarkierung;
        public string ParameterNameTypmarkierung
        {
            get => _parameterNameTymarkierung;
            set
            {
                if (_parameterNameTymarkierung != value)
                {
                    SharedParameterElement param = SharedParameterElements?.OfType<SharedParameterElement>().FirstOrDefault(s => s.Name == value);
                    Definition def = param?.GetDefinition();
                    ParameterGroupTypmarkierung = GetParameterGroupString(def);
                    Set(ref _parameterNameTymarkierung, value);
                }
            }
        }

        private string _parameterGroupTypmarkierung;
        public string ParameterGroupTypmarkierung
        {
            get => _parameterGroupTypmarkierung;
            set => Set(ref _parameterGroupTypmarkierung, value);
        }

        private string _parameterNameTypbeschreibung;
        public string ParameterNameTypbeschreibung
        {
            get => _parameterNameTypbeschreibung;
            set
            {
                if (_parameterNameTypbeschreibung != value)
                {
                    SharedParameterElement param = SharedParameterElements?.OfType<SharedParameterElement>().FirstOrDefault(s => s.Name == value);
                    Definition def = param?.GetDefinition();
                    ParameterGroupTypbeschreibung = GetParameterGroupString(def);
                    Set(ref _parameterNameTypbeschreibung, value);
                }
            }
        }

        private string _parameterGroupTypbeschreibung;
        public string ParameterGroupTypbeschreibung
        {
            get => _parameterGroupTypbeschreibung;
            set => Set(ref _parameterGroupTypbeschreibung, value);
        }

        private bool _warnmeldungBeiFehlendemParameter;
        public bool WarnmeldungBeiFehlendemParameter
        {
            get => _warnmeldungBeiFehlendemParameter;
            set => Set(ref _warnmeldungBeiFehlendemParameter, value);
        }

        private string _sharedParmFileName;
        public string SharedParamFileName
        {
            get => _sharedParmFileName;
            set => Set(ref _sharedParmFileName, value);
        }

        private bool _familyAutoSave;
        public bool FamilyAutoSave
        {
            get => _familyAutoSave;
            set => Set(ref _familyAutoSave, value);
        }
        public bool changed;

        private bool _bestaetigungBGKZUweisung;
        public bool BestaetigungBGKZUweisung
        {
            get => _bestaetigungBGKZUweisung;
            set => Set(ref _bestaetigungBGKZUweisung, value);
        }

        Document doc;
        UIDocument uidoc;
        UIApplication uiapp;

        private List<SharedParameterElement> SharedParameterElements;
        private readonly Dictionary<Baugruppe, PropertyChangedEventHandler> _nodeChangeHandlers = new();
        private readonly Dictionary<Baugruppe, PropertyChangedEventHandler> _nodeSelectionHandlers = new();
        private readonly Dictionary<Baugruppe, NotifyCollectionChangedEventHandler> _nodeTypCollectionHandlers = new();
        private readonly Dictionary<Baugruppe, NotifyCollectionChangedEventHandler> _nodeChildCollectionHandlers = new();
        private readonly Dictionary<Typmarkierung, PropertyChangedEventHandler> _typChangeHandlers = new();

        private Baugruppe _selectedNode;
        public Baugruppe SelectedNode
        {
            get => _selectedNode;
            set => Set(ref _selectedNode, value);
        }
        public ObservableCollection<string> ParamListe
        {
            get
            {
                return new ObservableCollection<string>((SharedParameterElements ?? new List<SharedParameterElement>())
                    .OrderBy(u => u.Name)
                    .Select(user => user.Name));
            }
        }

        public ObservableCollection<string> ParameterGruppen
        {
            get
            {
                DefinitionFile defFile = uiapp.Application.OpenSharedParameterFile();
                DefinitionGroups groups = defFile?.Groups;
                return new ObservableCollection<string>(groups?.Select(p => p.Name)?.OrderByDescending(x=> x));
            } 
        }

        public ICommand NewOwnDataCommand { get; }
        public ICommand OpenOwnDataCommand { get; }
        public ICommand SaveOwnDataCommand { get; }
        public ICommand Einstellungen { get; }
        public ICommand Infos { get; }

        public ICommand AddLabel { get; }
        public ICommand ItemUp { get; }
        public ICommand ItemDown { get; }
        public ICommand ItemCopy { get; }
        public ICommand ItemDelete { get; }
        public ICommand ItemToParent { get; }
        public ICommand ItemToSub { get; }
        public ICommand ExpandCloseNodesCmd { get; }

        public ICommand AddTypenmarkierung { get; }
        public ICommand CopyTypenmarkierung { get; }
        public ICommand MoveUpTypenmarkierung { get; }
        public ICommand MoveDownTypenmarkierung { get; }
        public ICommand DelTypenmarkierung { get; }

        public ICommand AllSubNudesProtectionCmd { get; }

        public ICommand SendValues { get; }

        public ICommand LoadOwnDataFromConfiguredPathCommand { get; }

        #endregion

        public MainViewModel(UIApplication ap)
        {
            uiapp = ap ?? throw new ArgumentNullException(nameof(ap));
            uidoc = uiapp.ActiveUIDocument;
            doc = uidoc?.Document;
            SharedParameterElements = new List<SharedParameterElement>();

            LoadSettings();
            ParameterListeFüllen();
            changed = false;

            // Mark ViewModel property changes as edits.
            // Important: ignore status/UI-only properties, otherwise actions like "Übertragen" or status updates
            // can incorrectly trigger the "BGK-Daten wurden verändert" prompt.
            this.PropertyChanged += (s, e) =>
            {
                try
                {
                    if (_suspendChangeTracking) return;
                    if (e?.PropertyName == null) return;
                    if (e.PropertyName == nameof(changed)) return;
                    if (e.PropertyName == nameof(StatusMessage)) return;
                    changed = true;
                }
                catch { }
            };

            // ExternalEvent + Handler initialisieren
            _assignHandler = new BgkAssignExternalEventHandler();
            _assignEvent = ExternalEvent.Create(_assignHandler);

            Baugruppen = new ObservableCollection<Baugruppe>();
            // Observe collection changes so we can track edits in nodes/typen
            Baugruppen.CollectionChanged += Baugruppen_CollectionChanged;

            // Setze EigeneDaten explizit auf null, um Auto-Load zu ermöglichen
            EigeneDaten = null;

            // Menü-Commands (Hauptfenster.xaml)
            NewOwnDataCommand = new RelayCommand(() => NewData());
            OpenOwnDataCommand = new RelayCommand(() => OpenData("true"));
            SaveOwnDataCommand = new RelayCommand(() => SaveData("false"));
            Einstellungen = new RelayCommand(() => EinstellungenÖffnen());
            Infos = new RelayCommand(() => InfosÖffnen());

            LoadOwnDataFromConfiguredPathCommand = new RelayCommand(() =>
            {
                var settingsPath = Properties.Settings.Default.PfadBGKDatei;
                var path = !string.IsNullOrWhiteSpace(settingsPath) ? settingsPath : EigeneDaten;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    ShowStatus("Datei nicht gefunden: " + (path ?? string.Empty));
                    return;
                }

                LoadDataFromPath(path);
                ShowStatus("Daten geladen.");
            });

            AddLabel = new RelayCommands<Baugruppe>(AddLabelExecute);
            ItemUp = new RelayCommands<Baugruppe>(ItemUpExecute);
            ItemDown = new RelayCommands<Baugruppe>(ItemDownExecute);
            ItemCopy = new RelayCommands<Baugruppe>(ItemCopyExecute);
            ItemDelete = new RelayCommands<Baugruppe>(ItemDeleteExecute);
            ItemToParent = new RelayCommands<Baugruppe>(ItemToParentExecute);
            ItemToSub = new RelayCommands<Baugruppe>(ItemToSubExecute);
            ExpandCloseNodesCmd = new RelayCommands<Baugruppe>(ExpandCloseNodesExecute);

            AddTypenmarkierung = new RelayCommands<Baugruppe>(AddTypenmarkierungExecute);
            CopyTypenmarkierung = new RelayCommands<Typmarkierung>(CopyTypenmarkierungExecute);
            MoveUpTypenmarkierung = new RelayCommands<Typmarkierung>(MoveUpTypenmarkierungExecute);
            MoveDownTypenmarkierung = new RelayCommands<Typmarkierung>(MoveDownTypenmarkierungExecute);
            DelTypenmarkierung = new RelayCommands<Typmarkierung>(DelTypenmarkierungExecute);

            AllSubNudesProtectionCmd = new RelayCommands<Baugruppe>(AllSubNodesProtectionExecute);

            SendValues = new RelayCommands<Typmarkierung>(SendValuesExecute);
        }

        public void RefreshDocumentContext(UIApplication currentApp)
        {
            if (currentApp == null)
                return;

            uiapp = currentApp;
            uidoc = uiapp.ActiveUIDocument;
            doc = uidoc?.Document;
            ParameterListeFüllen();
        }

        private void SendValuesExecute(Typmarkierung selectedTyp)
        {
            try
            {
                if (selectedTyp == null) return;
                var node = FindNodeContainingTyp(selectedTyp);
                if (node == null) return;

                // Document/UIDocument can change in Revit (project <-> family editor). Don't rely on cached fields.
                var currentUiDoc = uiapp?.ActiveUIDocument;
                var currentDoc = currentUiDoc?.Document;
                if (currentDoc == null)
                {
                    ShowStatus("Kein aktives Dokument.");
                    return;
                }

                // If we are editing a family, write into the currently active family type.
                if (currentDoc.IsFamilyDocument)
                {
                    _assignHandler.SetRequest(new BgkAssignRequest
                    {
                        Mode = AssignMode.ActiveFamilyDoc,
                        ElementIds = null,
                        BgkKey = node.Key,
                        BgkDescription = node.Description,
                        TypNummer = selectedTyp.Typnummer.ToString(),
                        Typbeschreibung = selectedTyp.Typbezeichnung,
                        BestaetigungBGKZUweisung = BestaetigungBGKZUweisung,
                        FamilyAutoSave = FamilyAutoSave
                    });

                    _assignEvent.Raise();
                    // Übertragen ändert keine BGK-Daten
                    changed = false;
                    ShowStatus("Daten in aktive Familie übertragen.");
                    return;
                }

                var ids = currentUiDoc?.Selection?.GetElementIds()?.ToList() ?? new List<ElementId>();
                if (ids.Count == 0)
                {
                    try
                    {
                        ShowStatus("Bitte Elemente auswählen und mit ENTER bestätigen...");
                        var picked = currentUiDoc.Selection.PickObjects(Autodesk.Revit.UI.Selection.ObjectType.Element,
                            "Bitte Elemente auswählen und mit ENTER bestätigen");
                        ids = picked?.Select(r => r.ElementId).Distinct().ToList() ?? new List<ElementId>();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        ShowStatus("Auswahl abgebrochen – Werte nicht übertragen.");
                        return;
                    }

                    if (ids.Count == 0)
                    {
                        ShowStatus("Keine Elemente ausgewählt – Werte nicht übertragen.");
                        return;
                    }
                }

                _assignHandler.SetRequest(new BgkAssignRequest
                {
                    Mode = AssignMode.ProjectSelection,
                    ElementIds = ids,
                    BgkKey = node.Key,
                    BgkDescription = node.Description,
                    TypNummer = selectedTyp.Typnummer.ToString(),
                    Typbeschreibung = selectedTyp.Typbezeichnung,
                    BestaetigungBGKZUweisung = BestaetigungBGKZUweisung,
                    FamilyAutoSave = FamilyAutoSave
                });

                _assignEvent.Raise();
                // Übertragen ändert keine BGK-Daten
                changed = false;
                ShowStatus("Daten übertragen.");
            }
            catch (Exception ex)
            {
                ShowStatus("Fehler beim Übertragen: " + ex.Message);
            }
        }

        private Baugruppe GetRoot(Baugruppe node)
        {
            if (node == null) return null;
            while (node.ParentNode != null) node = node.ParentNode;
            return node;
        }

        private ObservableCollection<Baugruppe> GetSiblings(Baugruppe node)
        {
            if (node == null) return null;
            if (node.ParentNode != null)
                return node.ParentNode.Baugruppen;
            return Baugruppen;
        }

        private void AddLabelExecute(Baugruppe selected)
        {
            if (selected == null) return;
            if (selected.NodeProtection) return;

            selected.Baugruppen ??= new ObservableCollection<Baugruppe>();

            var newNode = new Baugruppe
            {
                Key = "---",
                Description = "---",
                Level = (selected.Level + 1),
                RootNode = false,
                ParentNode = selected,
                Typmarkierungen = new ObservableCollection<Typmarkierung>(),
                Baugruppen = new ObservableCollection<Baugruppe>()
            };

            selected.Baugruppen.Add(newNode);
            selected.ExpandNode = true;
        }

        private void ItemDeleteExecute(Baugruppe selected)
        {
            if (selected == null) return;
            if (selected.NodeProtection) return;
            if (selected.ParentNode == null) return; // don't delete root

            var siblings = GetSiblings(selected);
            siblings?.Remove(selected);
        }

        private void ItemUpExecute(Baugruppe selected)
        {
            if (selected == null) return;
            var siblings = GetSiblings(selected);
            if (siblings == null) return;
            int idx = siblings.IndexOf(selected);
            if (idx <= 0) return;
            siblings.Move(idx, idx - 1);
            ShowStatus("Datensatz verschoben.");
        }

        private void ItemDownExecute(Baugruppe selected)
        {
            if (selected == null) return;
            var siblings = GetSiblings(selected);
            if (siblings == null) return;
            int idx = siblings.IndexOf(selected);
            if (idx < 0 || idx >= siblings.Count - 1) return;
            siblings.Move(idx, idx + 1);
            ShowStatus("Datensatz verschoben.");
        }

        private static Baugruppe CloneNodeShallow(Baugruppe src)
        {
            return new Baugruppe
            {
                Key = src?.Key,
                Description = src?.Description,
                NodeComment = src?.NodeComment,
                NodeProtection = src?.NodeProtection ?? false,
                Level = src?.Level ?? 0,
                RootNode = false,
                Typmarkierungen = src?.Typmarkierungen != null
                    ? new ObservableCollection<Typmarkierung>(src.Typmarkierungen.Select(t => new Typmarkierung { Typnummer = t.Typnummer, Typbezeichnung = t.Typbezeichnung }))
                    : new ObservableCollection<Typmarkierung>(),
                Baugruppen = new ObservableCollection<Baugruppe>()
            };
        }

        private void ItemCopyExecute(Baugruppe selected)
        {
            if (selected == null) return;
            var siblings = GetSiblings(selected);
            if (siblings == null) return;

            var copy = CloneNodeShallow(selected);
            copy.ParentNode = selected.ParentNode;
            copy.Level = selected.Level;
            siblings.Insert(siblings.IndexOf(selected) + 1, copy);
            ShowStatus("Datensatz kopiert.");
        }

        private void ItemToParentExecute(Baugruppe selected)
        {
            if (selected == null) return;
            if (selected.ParentNode == null) return;
            var parent = selected.ParentNode;
            if (parent.ParentNode == null) return;

            var grandParent = parent.ParentNode;
            parent.Baugruppen.Remove(selected);
            selected.ParentNode = grandParent;
            selected.Level = Math.Max(0, selected.Level - 1);
            grandParent.Baugruppen ??= new ObservableCollection<Baugruppe>();
            grandParent.Baugruppen.Add(selected);
            ShowStatus("Datensatz umgehängt.");
        }

        private void ItemToSubExecute(Baugruppe selected)
        {
            if (selected == null) return;
            var siblings = GetSiblings(selected);
            if (siblings == null) return;
            int idx = siblings.IndexOf(selected);
            if (idx <= 0) return;

            var newParent = siblings[idx - 1];
            if (newParent == null) return;
            siblings.RemoveAt(idx);
            newParent.Baugruppen ??= new ObservableCollection<Baugruppe>();
            selected.ParentNode = newParent;
            selected.Level = newParent.Level + 1;
            newParent.Baugruppen.Add(selected);
            newParent.ExpandNode = true;
            ShowStatus("Datensatz umgehängt.");
        }

        private void ExpandCloseNodesExecute(Baugruppe selected)
        {
            if (selected == null) return;
            bool newState = !selected.ExpandNode;
            void Apply(Baugruppe n)
            {
                if (n == null) return;
                n.ExpandNode = newState;
                if (n.Baugruppen == null) return;
                foreach (var c in n.Baugruppen) Apply(c);
            }
            Apply(selected);
            ShowStatus(newState ? "Unterelemente geöffnet." : "Unterelemente geschlossen.");
        }

        private void AddTypenmarkierungExecute(Baugruppe selected)
        {
            if (selected == null) return;
            if (selected.NodeProtection) return;
            selected.Typmarkierungen ??= new ObservableCollection<Typmarkierung>();
            selected.Typmarkierungen.Add(new Typmarkierung { Typnummer = 0, Typbezeichnung = string.Empty });
        }

        private Baugruppe FindNodeContainingTyp(Typmarkierung typ)
        {
            if (typ == null) return null;
            Baugruppe found = null;
            void Walk(Baugruppe n)
            {
                if (found != null || n == null) return;
                if (n.Typmarkierungen != null && n.Typmarkierungen.Contains(typ)) { found = n; return; }
                if (n.Baugruppen == null) return;
                foreach (var c in n.Baugruppen) Walk(c);
            }

            foreach (var root in Baugruppen ?? Enumerable.Empty<Baugruppe>())
                Walk(root);
            return found;
        }

        private void CopyTypenmarkierungExecute(Typmarkierung selected)
        {
            if (selected == null) return;
            var node = FindNodeContainingTyp(selected);
            if (node == null || node.NodeProtection) return;
            var list = node.Typmarkierungen;
            int idx = list.IndexOf(selected);
            list.Insert(idx + 1, new Typmarkierung { Typnummer = selected.Typnummer, Typbezeichnung = selected.Typbezeichnung });
            ShowStatus("Typ kopiert.");
        }

        private void MoveUpTypenmarkierungExecute(Typmarkierung selected)
        {
            if (selected == null) return;
            var node = FindNodeContainingTyp(selected);
            var list = node?.Typmarkierungen;
            if (list == null) return;
            int idx = list.IndexOf(selected);
            if (idx <= 0) return;
            list.Move(idx, idx - 1);
            ShowStatus("Typ verschoben.");
        }

        private void MoveDownTypenmarkierungExecute(Typmarkierung selected)
        {
            if (selected == null) return;
            var node = FindNodeContainingTyp(selected);
            var list = node?.Typmarkierungen;
            if (list == null) return;
            int idx = list.IndexOf(selected);
            if (idx < 0 || idx >= list.Count - 1) return;
            list.Move(idx, idx + 1);
            ShowStatus("Typ verschoben.");
        }

        private void DelTypenmarkierungExecute(Typmarkierung selected)
        {
            if (selected == null) return;
            var node = FindNodeContainingTyp(selected);
            if (node == null || node.NodeProtection) return;
            node.Typmarkierungen?.Remove(selected);
        }

        private void AllSubNodesProtectionExecute(Baugruppe selected)
        {
            if (selected == null) return;
            bool newState = !selected.NodeProtection;

            void Apply(Baugruppe n)
            {
                if (n == null) return;
                n.NodeProtection = newState;
                if (n.Baugruppen == null) return;
                foreach (var c in n.Baugruppen) Apply(c);
            }

            Apply(selected);
            ShowStatus(newState ? "Schutz aktiviert." : "Schutz deaktiviert.");
        }

        private void Baugruppen_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (Baugruppe item in e.NewItems)
                    RegisterNodeHandlers(item);
            }

            if (e.OldItems != null)
            {
                foreach (Baugruppe item in e.OldItems)
                    UnregisterNodeHandlers(item);
            }
        }

        private void RegisterNodeHandlers(Baugruppe node)
        {
            if (node == null || _nodeChangeHandlers.ContainsKey(node)) return;

            PropertyChangedEventHandler nodeChangeHandler = (s, e) =>
            {
                if (_suspendChangeTracking) return;
                if (e?.PropertyName == nameof(Baugruppe.IsSelectedNode)
                    || e.PropertyName == nameof(Baugruppe.ExpandNode)
                    || e.PropertyName == nameof(Baugruppe.RootNode)
                    || e.PropertyName == nameof(Baugruppe.Level))
                {
                    return;
                }

                changed = true;
            };

            PropertyChangedEventHandler nodeSelectionHandler = (s, e) =>
            {
                if (e?.PropertyName == nameof(Baugruppe.IsSelectedNode) && node.IsSelectedNode)
                    SelectedNode = node;
            };

            _nodeChangeHandlers[node] = nodeChangeHandler;
            _nodeSelectionHandlers[node] = nodeSelectionHandler;
            node.PropertyChanged += nodeChangeHandler;
            node.PropertyChanged += nodeSelectionHandler;

            if (node.Typmarkierungen == null) node.Typmarkierungen = new ObservableCollection<Typmarkierung>();
            NotifyCollectionChangedEventHandler typCollectionHandler = (s, e) =>
            {
                if (!_suspendChangeTracking) changed = true;
                if (e.NewItems != null)
                {
                    foreach (Typmarkierung t in e.NewItems)
                        AttachTypHandler(t);
                }

                if (e.OldItems != null)
                {
                    foreach (Typmarkierung t in e.OldItems)
                        DetachTypHandler(t);
                }
            };

            _nodeTypCollectionHandlers[node] = typCollectionHandler;
            node.Typmarkierungen.CollectionChanged += typCollectionHandler;
            foreach (var t in node.Typmarkierungen)
                AttachTypHandler(t);

            if (node.Baugruppen == null) node.Baugruppen = new ObservableCollection<Baugruppe>();
            NotifyCollectionChangedEventHandler childCollectionHandler = (s, e) =>
            {
                if (!_suspendChangeTracking) changed = true;
                if (e.NewItems != null)
                {
                    foreach (Baugruppe n in e.NewItems)
                        RegisterNodeHandlers(n);
                }

                if (e.OldItems != null)
                {
                    foreach (Baugruppe n in e.OldItems)
                        UnregisterNodeHandlers(n);
                }
            };

            _nodeChildCollectionHandlers[node] = childCollectionHandler;
            node.Baugruppen.CollectionChanged += childCollectionHandler;

            foreach (var child in node.Baugruppen)
                RegisterNodeHandlers(child);
        }

        private void UnregisterNodeHandlers(Baugruppe node)
        {
            if (node == null) return;

            if (node.Baugruppen != null)
            {
                foreach (var child in node.Baugruppen.ToList())
                    UnregisterNodeHandlers(child);
            }

            if (node.Typmarkierungen != null)
            {
                foreach (var typ in node.Typmarkierungen.ToList())
                    DetachTypHandler(typ);
            }

            if (_nodeTypCollectionHandlers.TryGetValue(node, out var typCollectionHandler))
            {
                node.Typmarkierungen.CollectionChanged -= typCollectionHandler;
                _nodeTypCollectionHandlers.Remove(node);
            }

            if (_nodeChildCollectionHandlers.TryGetValue(node, out var childCollectionHandler))
            {
                node.Baugruppen.CollectionChanged -= childCollectionHandler;
                _nodeChildCollectionHandlers.Remove(node);
            }

            if (_nodeChangeHandlers.TryGetValue(node, out var nodeChangeHandler))
            {
                node.PropertyChanged -= nodeChangeHandler;
                _nodeChangeHandlers.Remove(node);
            }

            if (_nodeSelectionHandlers.TryGetValue(node, out var nodeSelectionHandler))
            {
                node.PropertyChanged -= nodeSelectionHandler;
                _nodeSelectionHandlers.Remove(node);
            }
        }

        private void AttachTypHandler(Typmarkierung typ)
        {
            if (typ == null || _typChangeHandlers.ContainsKey(typ)) return;

            PropertyChangedEventHandler handler = (ss, ee) =>
            {
                if (!_suspendChangeTracking) changed = true;
            };

            _typChangeHandlers[typ] = handler;
            typ.PropertyChanged += handler;
        }

        private void DetachTypHandler(Typmarkierung typ)
        {
            if (typ == null) return;

            if (_typChangeHandlers.TryGetValue(typ, out var handler))
            {
                typ.PropertyChanged -= handler;
                _typChangeHandlers.Remove(typ);
            }
        }

        public void LoadSettings()
        {
            var settings = Properties.Settings.Default;
            Dateiname = GetSetting<string>(settings, "Dateiname", string.Empty);
            EigeneDaten = GetSetting<string>(settings, "EigeneDaten", string.Empty);
            WindowWidth = GetSetting<int>(settings, "WindowWidth", 0);
            WindowHeight = GetSetting<int>(settings, "WindowHeight", 0);
            WindowTop = GetSetting<double>(settings, "WindowTop", 0);
            AutoLoad = GetSetting<bool>(settings, "AutoLoad", false);
            AutoSave = GetSetting<bool>(settings, "AutoSave", false);
            EditMode = GetSetting<bool>(settings, "EditierModus", false);
            WindowLeft = GetSetting<double>(settings, "WindowLeft", 0);
            TrvExpand = GetSetting<bool>(settings, "TrvExpand", false);
            AutoAktualisierung = GetSetting<bool>(settings, "AutoAktualisierung", false);
            HauptfensterTopMost = GetSetting<bool>(settings, "HauptfensterTopMost", false);
            // optional settings - may not exist in current Settings
            ParameterNameBGK = GetSetting<string>(settings, "ParameternameBGK", string.Empty);
            ParameterNameTypmarkierung = GetSetting<string>(settings, "ParameternameTypmarkierung", string.Empty);
            ParameterNameTypbeschreibung = GetSetting<string>(settings, "ParameternameTypbeschreibung", string.Empty);
            WarnmeldungBeiFehlendemParameter = GetSetting<bool>(settings, "WarnmeldungParameter", false);
            ParameterGroupBGK = GetSetting<string>(settings, "ParameterGroupBGK", string.Empty);
            ParameterGroupTypmarkierung = GetSetting<string>(settings, "ParameterGroupTypmarkierung", string.Empty);
            ParameterGroupTypbeschreibung = GetSetting<string>(settings, "ParameterGroupTypbeschreibung", string.Empty);
            SharedParamFileName = GetSetting<string>(settings, "SharedParamFileName", string.Empty);
            BestaetigungBGKZUweisung = GetSetting<bool>(settings, "BGKZuweisungenBestätigung", false);
            FamilyAutoSave = GetSetting<bool>(settings, "FamilyAutoSave", false);
        }

        public void SaveSettings()
        {
            var settings = Properties.Settings.Default;
            SetSetting(settings, "Dateiname", Dateiname);
            SetSetting(settings, "EigeneDaten", EigeneDaten);
            SetSetting(settings, "WindowWidth", WindowWidth);
            SetSetting(settings, "WindowHeight", WindowHeight);
            SetSetting(settings, "WindowTop", WindowTop);
            SetSetting(settings, "WindowLeft", WindowLeft);
            SetSetting(settings, "TrvExpand", TrvExpand);
            SetSetting(settings, "AutoLoad", AutoLoad);
            SetSetting(settings, "AutoSave", AutoSave);
            SetSetting(settings, "EditierModus", EditMode);
            SetSetting(settings, "AutoAktualisierung", AutoAktualisierung);
            SetSetting(settings, "HauptfensterTopMost", HauptfensterTopMost);
            SetSetting(settings, "ParameternameBGK", ParameterNameBGK);
            SetSetting(settings, "ParameternameTypmarkierung", ParameterNameTypmarkierung);
            SetSetting(settings, "ParameternameTypbeschreibung", ParameterNameTypbeschreibung);
            SetSetting(settings, "WarnmeldungParameter", WarnmeldungBeiFehlendemParameter);
            SetSetting(settings, "ParameterGroupBGK", ParameterGroupBGK);
            SetSetting(settings, "ParameterGroupTypmarkierung", ParameterGroupTypmarkierung);
            SetSetting(settings, "ParameterGroupTypbeschreibung", ParameterGroupTypbeschreibung);
            SetSetting(settings, "SharedParamFileName", SharedParamFileName);
            SetSetting(settings, "BGKZuweisungenBestätigung", BestaetigungBGKZUweisung);
            SetSetting(settings, "FamilyAutoSave", FamilyAutoSave);

            try { settings.Save(); } catch { }
        }

        public void SaveData(string dlg = "false")
        {
            try
            {
                bool dialog = Convert.ToBoolean(dlg);
                if (!dialog)
                {
                    var res = System.Windows.MessageBox.Show(
                        "Daten in der voreingestellten Datei speichern?",
                        "Speichern bestätigen",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (res != MessageBoxResult.Yes)
                        return;
                }
                if (dialog)
                {
                    SaveFileDialog saveFileDialog = new SaveFileDialog
                    {
                        Filter = "xml files (*.xml)|*.xml",
                        FilterIndex = 2,
                        RestoreDirectory = true
                    };

                    if (saveFileDialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(saveFileDialog.FileName))
                        EigeneDaten = saveFileDialog.FileName;
                }

                if (string.IsNullOrWhiteSpace(EigeneDaten))
                    return;

                if (!TrySaveDataSilently(EigeneDaten, out var errorMessage))
                    throw new InvalidOperationException(errorMessage);

                ShowStatus("Daten gespeichert.");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler", "Daten konnten nicht gespeichert werden: " + ex.Message);
            }
        }

        public bool TrySaveDataSilently(string path)
        {
            return TrySaveDataSilently(path, out _);
        }

        public bool TrySaveDataSilently(string path, out string errorMessage)
        {
            errorMessage = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    errorMessage = "Kein Speicherpfad konfiguriert.";
                    return false;
                }

                var rootNode = GetRootNodeForSerialization();
                if (rootNode == null)
                {
                    errorMessage = "Es sind keine BGK-Daten zum Speichern vorhanden.";
                    return false;
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var xmlserializer = new XmlSerializer(typeof(Baugruppe));
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    xmlserializer.Serialize(fs, rootNode);
                }

                EigeneDaten = path;
                changed = false;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private Baugruppe GetRootNodeForSerialization()
        {
            if (Baugruppen == null || Baugruppen.Count == 0)
                return null;

            return Baugruppen.FirstOrDefault() ?? Baugruppen[0];
        }

        public void LoadDataFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                _suspendChangeTracking = true;
                EigeneDaten = path;
                OpenData("false");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler", "BGK-Datei konnte nicht geladen werden: " + ex.Message);
            }
            finally
            {
                _suspendChangeTracking = false;
            }
        }

        public void OpenData(string dlg)
        {
            try
            {
                bool dialog = Convert.ToBoolean(dlg);
                // When called with dlg == "false" we interpret it as "load from configured path".
                // This is used by the bottom toolbar open button (no dialog).
                if (!dialog)
                {
                    var settingsPath = Properties.Settings.Default.PfadBGKDatei;
                    var path = !string.IsNullOrWhiteSpace(settingsPath) ? settingsPath : EigeneDaten;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        EigeneDaten = path;
                }
                else
                {
                    OpenFileDialog openFileDialog = new OpenFileDialog
                    {
                        Filter = "xml files (*.xml)|*.xml",
                        FilterIndex = 2,
                        Multiselect = false
                    };

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                        EigeneDaten = openFileDialog.FileName;
                }

                if (!string.IsNullOrWhiteSpace(EigeneDaten))
                {
                    var xmlserializer = new XmlSerializer(typeof(Baugruppe));
                    using (StreamReader sr = new StreamReader(EigeneDaten))
                    {
                        var newNode = (Baugruppe)xmlserializer.Deserialize(sr);
                        newNode = FillParent(newNode);
                        Baugruppen.Clear();
                        Baugruppen.Add(newNode);
                        changed = false;
                    }
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler", "Daten konnten nicht geladen werden: " + ex.Message);
            }
        }

        private static T GetSetting<T>(dynamic settings, string name, T defaultValue)
        {
            try
            {
                var val = settings[name];
                if (val == null) return defaultValue;
                return (T)Convert.ChangeType(val, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }

        private static void SetSetting(dynamic settings, string name, object value)
        {
            try
            {
                if (settings.Properties[name] != null)
                    settings[name] = value;
            }
            catch
            {
            }
        }

        private void ParameterListeFüllen()
        {
            try
            {
                if (doc == null)
                {
                    SharedParameterElements = new List<SharedParameterElement>();
                    OnPropertyChanged(nameof(ParamListe));
                    return;
                }

                SharedParameterElements = new FilteredElementCollector(doc)
                    .OfClass(typeof(SharedParameterElement))
                    .Cast<SharedParameterElement>()
                    .OrderBy(p => p.Name)
                    .ToList();
            }
            catch
            {
                SharedParameterElements = new List<SharedParameterElement>();
            }

            OnPropertyChanged(nameof(ParamListe));
        }

        public void NewData()
        {
            try
            {
                var tempNode = new Baugruppe { Key = "---", Description = Dateiname, Level = 0, RootNode = true };
                tempNode.Baugruppen ??= new ObservableCollection<Baugruppe>();
                tempNode.Baugruppen.Add(new Baugruppe { Key = "---", Description = "---", Level = 1, RootNode = false, ParentNode = tempNode, Typmarkierungen = new ObservableCollection<Typmarkierung>() });

                Baugruppen ??= new ObservableCollection<Baugruppe>();
                Baugruppen.Clear();
                Baugruppen.Add(tempNode);
                SelectedNode = tempNode;
                changed = false;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler", ex.Message);
            }
        }

        private void EinstellungenÖffnen()
        {
            try
            {
                var wnd = new Einstellungen(this);
                wnd.Show();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler", ex.Message);
            }
        }

        private void InfosÖffnen()
        {
            try
            {
                Info wnd = new Info();
                wnd.Show();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler", ex.Message);
            }
        }

        private static string GetParameterGroupString(Definition def)
        {
            try
            {
                if (def == null) return string.Empty;
                var prop = def.GetType().GetProperty("ParameterGroup");
                if (prop != null)
                {
                    var val = prop.GetValue(def);
                    return val?.ToString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }

        private Baugruppe FillParent(Baugruppe node)
        {
            if (node == null) return null;

            node.Baugruppen ??= new ObservableCollection<Baugruppe>();
            node.Typmarkierungen ??= new ObservableCollection<Typmarkierung>();

            foreach (var child in node.Baugruppen)
            {
                child.ParentNode = node;
                FillParent(child);
            }

            return node;
        }
    }
}
