using Autodesk.Revit.UI;
using BIMassist.Core;
using BIMassist.Models;
using BIMassist.Services;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AwesRelay = BIMassist.Core.RelayCommands<object>;

namespace BIMassist.ViewModels
{
    public class BoreholeManagerViewModel : ViewModelBase
    {
        private double _gridStepMeters = 20.0;
        public double GridStepMeters { get => _gridStepMeters; set => Set(ref _gridStepMeters, value); }

        private double _maxInterpDistanceMeters = 0.0;
        public double MaxInterpDistanceMeters { get => _maxInterpDistanceMeters; set => Set(ref _maxInterpDistanceMeters, value); }

        private double _minThicknessMeters = 0.01;
        public double MinThicknessMeters { get => _minThicknessMeters; set => Set(ref _minThicknessMeters, value); }

        public ObservableCollection<Borehole> Boreholes { get; } = new();
        public ObservableCollection<GeologyLayer> Layers { get; } = new();

        private Borehole _selectedBorehole;
        public Borehole SelectedBorehole
        {
            get => _selectedBorehole;
            set
            {
                if (Set(ref _selectedBorehole, value))
                {
                    OnPropertyChanged(nameof(LayersForSelection));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private IList<Borehole> _selectedBoreholes = new List<Borehole>();
        public IList<Borehole> SelectedBoreholes
        {
            get => _selectedBoreholes;
            set
            {
                _selectedBoreholes = value ?? new List<Borehole>();
                OnPropertyChanged(nameof(SelectedBoreholes));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private double _boreholeDiameterCm = 30.0;
        public double BoreholeDiameterCm { get => _boreholeDiameterCm; set => Set(ref _boreholeDiameterCm, value); }

        public IList<string> MaterialNameColumns { get; } = new List<string> { "Description", "Classification", "GeologyCode" };

        private string _selectedMaterialNameColumn = "Description";
        public string SelectedMaterialNameColumn { get => _selectedMaterialNameColumn; set => Set(ref _selectedMaterialNameColumn, value); }

        private GeologyLayer _selectedLayer;
        public GeologyLayer SelectedLayer
        {
            get => _selectedLayer;
            set
            {
                if (Set(ref _selectedLayer, value))
                    CommandManager.InvalidateRequerySuggested();
            }
        }

        private IList<GeologyLayer> _selectedLayers = new List<GeologyLayer>();
        public IList<GeologyLayer> SelectedLayers
        {
            get => _selectedLayers;
            set
            {
                _selectedLayers = value ?? new List<GeologyLayer>();
                OnPropertyChanged(nameof(SelectedLayers));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool _assumeMeters = true;
        public bool AssumeMeters { get => _assumeMeters; set => Set(ref _assumeMeters, value); }

        private bool _depthIsBelowGround = true;
        public bool DepthIsBelowGround { get => _depthIsBelowGround; set => Set(ref _depthIsBelowGround, value); }

        private bool _useFlatBoundaryTop = false;
        public bool UseFlatBoundaryTop { get => _useFlatBoundaryTop; set => Set(ref _useFlatBoundaryTop, value); }

        public ObservableCollection<GeologyLayer> LayersForSelection
            => new(Layers.Where(l => SelectedBorehole != null && l.LocationID == SelectedBorehole.LocationID)
                           .OrderBy(l => l.DepthTop));

        private readonly ExternalEvent _evPickPoint;
        private readonly PickPointOnViewHandler _pickPointHandler;

        public ICommand ImportHolesCmd { get; private set; }
        public ICommand ImportGeologyCmd { get; private set; }
        public ICommand ImportPairCmd { get; private set; }
        public ICommand DeleteHoleCmd { get; private set; }
        public ICommand DeleteLayerCmd { get; private set; }
        public ICommand CreateExtrusionsCmd { get; private set; }
        public ICommand CreateGroundModelCmd { get; private set; }
        public ICommand ClearAllCmd { get; private set; }
        public ICommand NewBoreholeCmd { get; private set; }
        public ICommand NewLayerCmd { get; private set; }
        public ICommand SaveChangesCmd { get; private set; }
        public ICommand PickCoordinateCmd { get; private set; }

        private readonly CsvImportService _csv = new();
        private readonly BoreholeProjectStorage _storage;
        private readonly UIApplication _uiapp;

        private readonly ExternalEvent _evSave;
        private readonly SaveBoreholeDataHandler _saveHandler;
        private readonly ExternalEvent _evExtr;
        private readonly CreateExtrusionsHandler _extrHandler;
        private readonly ExternalEvent _evGround;
        private readonly CreateGroundModelHandler _groundHandler;

        public BoreholeManagerViewModel(UIApplication uiapp)
        {
            _uiapp = uiapp;
            _storage = new BoreholeProjectStorage(uiapp?.ActiveUIDocument?.Document);

            _saveHandler = new SaveBoreholeDataHandler();
            _evSave = ExternalEvent.Create(_saveHandler);

            _extrHandler = new CreateExtrusionsHandler();
            _evExtr = ExternalEvent.Create(_extrHandler);

            _groundHandler = new CreateGroundModelHandler();
            _evGround = ExternalEvent.Create(_groundHandler);

            _pickPointHandler = new PickPointOnViewHandler();
            _evPickPoint = ExternalEvent.Create(_pickPointHandler);

            Initialize();
        }

        private void Initialize()
        {
            if (_uiapp?.ActiveUIDocument?.Document != null)
            {
                var dataOpt = _storage.Load();
                if (dataOpt.HasValue)
                {
                    var data = dataOpt.Value;
                    foreach (var h in data.holes) Boreholes.Add(h);
                    foreach (var g in data.layers) Layers.Add(g);
                }
            }

            if (SelectedBorehole == null && Boreholes.Any())
                SelectedBorehole = Boreholes.First();

            ImportHolesCmd = new AwesRelay(_ => ImportHoles());
            ImportGeologyCmd = new AwesRelay(_ => ImportGeology());
            ImportPairCmd = new AwesRelay(_ => ImportPair());
            DeleteHoleCmd = new AwesRelay(_ => DeleteSelectedHoles(), _ => (SelectedBoreholes != null && SelectedBoreholes.Count > 0) || SelectedBorehole != null);
            DeleteLayerCmd = new AwesRelay(_ => DeleteSelectedLayers(), _ => (SelectedLayers != null && SelectedLayers.Count > 0) || SelectedLayer != null);
            ClearAllCmd = new AwesRelay(_ => ClearAll(), _ => Boreholes.Any() || Layers.Any());
            NewBoreholeCmd = new AwesRelay(_ => NewBorehole());
            NewLayerCmd = new AwesRelay(_ => NewLayer());
            SaveChangesCmd = new AwesRelay(_ => PersistToProject(), _ => true);
            CreateExtrusionsCmd = new AwesRelay(_ => CreateExtrusions(), _ => Boreholes.Any() && Layers.Any());
            CreateGroundModelCmd = new AwesRelay(_ => CreateGroundModel(), _ => Boreholes.Any() && Layers.Any());
            PickCoordinateCmd = new AwesRelay(async _ => await PickCoordinateForSelectedAsync(), _ => SelectedBorehole != null);
        }

        private void ClearAll()
        {
            var td = new Autodesk.Revit.UI.TaskDialog("Daten löschen")
            {
                MainInstruction = "Alle Bohrungen und Schichten löschen?",
                MainContent = "Dieser Vorgang entfernt sämtliche importierten Daten aus der Ansicht und dem Projektspeicher.",
                CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Yes | Autodesk.Revit.UI.TaskDialogCommonButtons.No,
                DefaultButton = Autodesk.Revit.UI.TaskDialogResult.No
            };
            if (td.Show() != Autodesk.Revit.UI.TaskDialogResult.Yes) return;

            Boreholes.Clear();
            Layers.Clear();
            SelectedBorehole = null;
            SelectedLayer = null;
            PersistToProject();
            OnPropertyChanged(nameof(LayersForSelection));
        }

        private void NewBorehole()
        {
            var bh = new Borehole
            {
                LocationID = "BH-" + (Boreholes.Count + 1),
                LocationType = string.Empty,
                Easting = 0,
                Northing = 0,
                GroundLevel = 0,
                FinalDepth = 0,
                Orientation = string.Empty,
                Inclination = string.Empty
            };

            Boreholes.Add(bh);
            SelectedBorehole = bh;
            PersistToProject();
            OnPropertyChanged(nameof(LayersForSelection));
        }

        private Task<(double EastingM, double NorthingM)?> PickPointAsync(string prompt)
        {
            var tcs = new TaskCompletionSource<(double EastingM, double NorthingM)?>();
            _pickPointHandler.Prompt = prompt;
            _pickPointHandler.Completion = tcs;
            _evPickPoint.Raise();
            return tcs.Task;
        }

        private async Task PickCoordinateForSelectedAsync()
        {
            var res = await PickPointAsync("Bohrpunkt wählen");
            if (!res.HasValue || SelectedBorehole == null)
                return;

            SelectedBorehole.Easting = res.Value.EastingM;
            SelectedBorehole.Northing = res.Value.NorthingM;

            PersistToProject();

            var idx = Boreholes.IndexOf(SelectedBorehole);
            if (idx >= 0)
            {
                var bh = SelectedBorehole;
                Boreholes.RemoveAt(idx);
                Boreholes.Insert(idx, bh);
                SelectedBorehole = bh;
            }

            OnPropertyChanged(nameof(LayersForSelection));
        }

        private void NewLayer()
        {
            var locId = SelectedBorehole?.LocationID ?? string.Empty;
            var gl = new GeologyLayer
            {
                LocationID = locId,
                DepthTop = 0,
                DepthBase = 0,
                Description = string.Empty,
                Colour = string.Empty,
                Consistency = string.Empty,
                Classification = string.Empty,
                GeologyCode = string.Empty
            };
            Layers.Add(gl);
            SelectedLayer = gl;
            PersistToProject();
            OnPropertyChanged(nameof(LayersForSelection));
        }

        public void PersistAfterEdit()
        {
            PersistToProject();
            OnPropertyChanged(nameof(LayersForSelection));
        }

        public void PersistToProject()
        {
            _saveHandler.Boreholes = Boreholes.ToList();
            _saveHandler.Layers = Layers.ToList();
            _evSave.Raise();
        }

        private void ImportHoles()
        {
            var dlg = new Win32OpenFileDialog { Filter = "CSV (*.csv)|*.csv" };
            if (dlg.ShowDialog() == true)
            {
                Merge(_csv.ReadHoles(dlg.FileName), null);
            }
        }

        private void ImportGeology()
        {
            var dlg = new Win32OpenFileDialog { Filter = "CSV (*.csv)|*.csv" };
            if (dlg.ShowDialog() == true)
            {
                Merge(null, _csv.ReadGeology(dlg.FileName));
            }
        }

        private void ImportPair()
        {
            var dlgH = new Win32OpenFileDialog { Title = "hole.csv wählen", Filter = "CSV (*.csv)|*.csv", Multiselect = true };
            if (dlgH.ShowDialog() == true)
            {
                var dlgG = new Win32OpenFileDialog { Title = "geol.csv wählen", Filter = "CSV (*.csv)|*.csv", Multiselect = true };
                if (dlgG.ShowDialog() == true)
                {
                    foreach (var f in dlgH.FileNames) Merge(_csv.ReadHoles(f), null);
                    foreach (var f in dlgG.FileNames) Merge(null, _csv.ReadGeology(f));
                }
            }
        }

        private void Merge(IEnumerable<Borehole> holes, IEnumerable<GeologyLayer> layers)
        {
            if (holes != null)
            {
                foreach (var h in holes)
                {
                    var existing = Boreholes.FirstOrDefault(b => b.LocationID == h.LocationID);
                    if (existing != null) existing.CopyFrom(h);
                    else Boreholes.Add(h);
                }
            }

            if (layers != null)
            {
                foreach (var layer in layers)
                {
                    var dup = Layers.FirstOrDefault(l =>
                        l.LocationID == layer.LocationID
                        && Math.Abs(l.DepthTop - layer.DepthTop) < 1e-9
                        && Math.Abs(l.DepthBase - layer.DepthBase) < 1e-9
                        && string.Equals(l.Description ?? "", layer.Description ?? "", StringComparison.OrdinalIgnoreCase));

                    if (dup != null) dup.CopyFrom(layer);
                    else Layers.Add(layer);
                }
            }

            PersistToProject();
            OnPropertyChanged(nameof(LayersForSelection));

            if (SelectedBorehole == null && Boreholes.Any())
                SelectedBorehole = Boreholes.First();
        }

        public void DeleteSelectedHoles()
        {
            var holesToDelete = new List<Borehole>();
            if (SelectedBoreholes != null && SelectedBoreholes.Count > 0)
                holesToDelete.AddRange(SelectedBoreholes);
            else if (SelectedBorehole != null)
                holesToDelete.Add(SelectedBorehole);

            if (holesToDelete.Count == 0) return;

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bh in holesToDelete)
                if (bh != null && !string.IsNullOrEmpty(bh.LocationID))
                    ids.Add(bh.LocationID);

            for (int i = Layers.Count - 1; i >= 0; i--)
            {
                var gl = Layers[i];
                if (gl != null && !string.IsNullOrEmpty(gl.LocationID) && ids.Contains(gl.LocationID))
                    Layers.RemoveAt(i);
            }

            foreach (var bh in holesToDelete.ToList())
                if (bh != null) Boreholes.Remove(bh);

            SelectedBorehole = null;
            SelectedBoreholes = new List<Borehole>();
            SelectedLayer = null;
            SelectedLayers = new List<GeologyLayer>();

            PersistToProject();
        }

        public void DeleteSelectedLayers()
        {
            var layersToDelete = new List<GeologyLayer>();
            if (SelectedLayers != null && SelectedLayers.Count > 0)
                layersToDelete.AddRange(SelectedLayers);
            else if (SelectedLayer != null)
                layersToDelete.Add(SelectedLayer);

            if (layersToDelete.Count == 0) return;

            foreach (var gl in layersToDelete.ToList())
                if (gl != null) Layers.Remove(gl);

            SelectedLayer = null;
            SelectedLayers = new List<GeologyLayer>();

            PersistToProject();
        }

        private void CreateExtrusions()
        {
            _extrHandler.AssumeMeters = AssumeMeters;
            _extrHandler.DepthIsBelowGround = DepthIsBelowGround;
            _extrHandler.Boreholes = Boreholes.ToList();
            _extrHandler.Layers = Layers.ToList();
            _extrHandler.BoreholeDiameterMeters = BoreholeDiameterCm / 100.0;
            _extrHandler.MaterialNameColumn = SelectedMaterialNameColumn;
            _extrHandler.UseCodeAsLayerKey = string.Equals(SelectedMaterialNameColumn, "GeologyCode", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(SelectedMaterialNameColumn, "Code", StringComparison.OrdinalIgnoreCase);
            _evExtr.Raise();
        }

        private void CreateGroundModel()
        {
            _groundHandler.AssumeMeters = AssumeMeters;
            _groundHandler.DepthIsBelowGround = DepthIsBelowGround;
            _groundHandler.Boreholes = Boreholes.ToList();
            _groundHandler.Layers = Layers.ToList();
            _groundHandler.GridStepMeters = GridStepMeters;
            _groundHandler.MaxInterpDistanceMeters = MaxInterpDistanceMeters;
            _groundHandler.MinThicknessMeters = MinThicknessMeters;
            _groundHandler.MaterialNameColumn = SelectedMaterialNameColumn;
            _groundHandler.UseCodeAsLayerKey = string.Equals(SelectedMaterialNameColumn, "GeologyCode", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(SelectedMaterialNameColumn, "Code", StringComparison.OrdinalIgnoreCase);
            _groundHandler.BoundaryTopMode = UseFlatBoundaryTop ? CreateGroundModelHandler.EdgeTopMode.FlatAtMean : CreateGroundModelHandler.EdgeTopMode.NearestBorehole;
            _evGround.Raise();
        }
    }
}
