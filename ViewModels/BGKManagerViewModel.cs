using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Helpers;
using BIMassist.Models;
using BIMassist.Properties; // <- Settings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using Microsoft.Win32;

namespace BIMassist.ViewModels
{
    public class BGKManagerViewModel : ViewModelBase
    {
        private readonly UIApplication _uiapp;

        // --- optional: ExternalEvent (empfohlen für Revit-Transaktionen aus Dockable Pane)
        private readonly ExternalEvent _sendValuesEvent;
        private readonly IExternalEventHandler _sendValuesHandler;

        public BGKManagerViewModel(UIApplication uiapp)
        {
            _uiapp = uiapp;

            Baugruppen = new ObservableCollection<Baugruppe>();

            // ExternalEvent einrichten (falls du die Klasse schon hast; andernfalls diese 2 Zeilen ersetzen mit: SendValuesCommand = new RelayCommand(SendValues);)
            _sendValuesHandler = new BGKSendValuesHandler { ViewModel = this };
            _sendValuesEvent = ExternalEvent.Create(_sendValuesHandler);

            SendValuesCommand = new RelayCommand(() => _sendValuesEvent.Raise()); // immer aktiv
            ImportCommand = new RelayCommand(ImportCsv);

            // >>> Auto-Import beim Laden <<<
            TryAutoLoadFromSetting();
        }

        public BGKManagerViewModel() : this(null) { }

        // ----------------- Daten/Binding -----------------
        private ObservableCollection<Baugruppe> _baugruppen;
        public ObservableCollection<Baugruppe> Baugruppen
        {
            get => _baugruppen ??= new ObservableCollection<Baugruppe>();
            set => Set(ref _baugruppen, value);
        }

        private Baugruppe _selectedBaugruppe;
        public Baugruppe SelectedBaugruppe
        {
            get => _selectedBaugruppe;
            set => Set(ref _selectedBaugruppe, value);
        }

        private Typmarkierung _selectedTypmarkierung;
        public Typmarkierung SelectedTypmarkierung
        {
            get => _selectedTypmarkierung;
            set => Set(ref _selectedTypmarkierung, value);
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        // ----------------- Befehle -----------------
        public ICommand SendValuesCommand { get; }
        public ICommand ImportCommand { get; }

        // ----------------- Senden (läuft im Revit-API-Kontext via ExternalEvent) -----------------
        public void ExecuteSendValuesInApiContext(UIApplication app)
        {
            try
            {
                // Auswahl im Tree prüfen (Button ist immer aktiv)
                if (SelectedTypmarkierung == null)
                {
                    StatusText = SelectedBaugruppe != null
                        ? "Kategorie ausgewählt – bitte einen Typ unterhalb der Kategorie wählen."
                        : "Bitte einen Typ (kein Kategorie-Knoten) im TreeView auswählen.";
                    return;
                }

                var uiapp = app ?? _uiapp;
                if (uiapp?.ActiveUIDocument == null)
                {
                    StatusText = "Kein aktives Revit-Dokument.";
                    return;
                }

                var uidoc = uiapp.ActiveUIDocument;
                var doc = uidoc.Document;

                var ids = uidoc.Selection.GetElementIds();
                if (ids == null || ids.Count != 1) { StatusText = "Bitte genau ein Element selektieren."; return; }

                var elem = doc.GetElement(ids.First());
                if (elem == null) { StatusText = "Selektiertes Element nicht gefunden."; return; }

                var typeId = elem.GetTypeId();
                if (typeId == ElementId.InvalidElementId) { StatusText = "Das Element besitzt keinen Typ."; return; }

                var type = doc.GetElement(typeId) as ElementType;
                if (type == null) { StatusText = "Elementtyp konnte nicht ermittelt werden."; return; }

                // Werte aus Auswahl
                string kategorie = SelectedBaugruppe?.Key ?? string.Empty;
                string typnummer = SelectedTypmarkierung.Typnummer ?? string.Empty;
                string beschreibung = SelectedTypmarkierung.Beschreibung ?? string.Empty;

                var pBGK = type.get_Parameter(BuiltInParameter.ASSEMBLY_CODE);
                var pTypeComment = type.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                var pDescription = type.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);

                string errA = null, errB = null, errC = null;
                bool okA = IsWritableString(pBGK, ref errA);
                bool okB = IsWritableString(pTypeComment, ref errB);
                bool okC = IsWritableString(pDescription, ref errC);

                using (var t = new Transaction(doc, "BGK-Manager: Werte übertragen"))
                {
                    t.Start();
                    if (okA) pBGK.Set(kategorie);
                    if (okB) pTypeComment.Set(typnummer);
                    if (okC) pDescription.Set(beschreibung);
                    t.Commit();
                }

                if (okA && okB && okC)
                    StatusText = $"OK: Typ '{type.Name}' ← Kategorie='{kategorie}', Typnummer='{typnummer}', Beschreibung='{beschreibung}'.";
                else
                {
                    var parts = new List<string>();
                    if (!okA) parts.Add($"ASSEMBLY_CODE nicht schreibbar: {errA ?? "n/a"}");
                    if (!okB) parts.Add($"ALL_MODEL_TYPE_COMMENTS nicht schreibbar: {errB ?? "n/a"}");
                    if (!okC) parts.Add($"ALL_MODEL_DESCRIPTION nicht schreibbar: {errC ?? "n/a"}");
                    StatusText = string.Join(" | ", parts);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Fehler: {ex.Message}";
            }

            static bool IsWritableString(Parameter p, ref string err)
            {
                if (p == null) { err = "Parameter nicht vorhanden"; return false; }
                if (p.IsReadOnly) { err = "schreibgeschützt"; return false; }
                if (p.StorageType != StorageType.String) { err = $"Speichertyp ist {p.StorageType}"; return false; }
                return true;
            }
        }

        // ----------------- Import: Dialog + Persistenz -----------------
        private void ImportCsv()
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "CSV importieren",
                    Filter = "CSV / Text (*.csv;*.txt)|*.csv;*.txt|Alle Dateien (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (dlg.ShowDialog() != true) return;

                ImportCsvFromPath(dlg.FileName);

                // Pfad speichern (User-Setting)
                Properties.Settings.Default.PfadBGKDatei = dlg.FileName;
                Properties.Settings.Default.Save();

                StatusText = $"{StatusText} (Pfad gespeichert)";
            }
            catch (Exception ex)
            {
                StatusText = $"Fehler beim Import: {ex.Message}";
            }
        }

        // Kernlogik (ohne Dialog) – wird auch für Auto-Import benutzt
        private void ImportCsvFromPath(string path)
        {
            var enc = DetectEncoding(path);
            var lines = File.ReadAllLines(path, enc);
            if (lines.Length == 0) { StatusText = "Datei ist leer."; return; }

            // Header: Kategorie,Typnummer,Beschreibung (gemäß Beispiel) :contentReference[oaicite:0]{index=0}
            var header = SplitCsvLine(lines[0]);
            int idxKat = IndexOfHeader(header, "Kategorie");
            int idxTyp = IndexOfHeader(header, "Typnummer");
            int idxBes = IndexOfHeader(header, "Beschreibung");
            if (idxKat < 0 || idxTyp < 0 || idxBes < 0)
            {
                StatusText = "Ungültiger Header. Erwartet: Kategorie, Typnummer, Beschreibung.";
                return;
            }

            var dict = new Dictionary<string, Baugruppe>(StringComparer.CurrentCultureIgnoreCase);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cells = SplitCsvLine(lines[i]);

                int want = Math.Max(idxKat, Math.Max(idxTyp, idxBes));
                if (cells.Count <= want) continue;

                var kat = (cells[idxKat] ?? "").Trim();
                var typ = (cells[idxTyp] ?? "").Trim();
                var bes = (cells[idxBes] ?? "").Trim();

                if (!dict.TryGetValue(kat, out var bg))
                {
                    bg = new Baugruppe { Key = kat, Typmarkierungen = new ObservableCollection<Typmarkierung>() };
                    dict[kat] = bg;
                }

                bg.Typmarkierungen.Add(new Typmarkierung { Typnummer = typ, Beschreibung = bes });
            }

            var neu = dict.Values
                          .OrderBy(b => b.Key, StringComparer.CurrentCultureIgnoreCase)
                          .Select(b =>
                          {
                              b.Typmarkierungen = new ObservableCollection<Typmarkierung>(
                                  b.Typmarkierungen.OrderBy(t => t.Typnummer, StringComparer.CurrentCultureIgnoreCase));
                              return b;
                          });

            Baugruppen = new ObservableCollection<Baugruppe>(neu);
            SelectedBaugruppe = null;
            SelectedTypmarkierung = null;

            StatusText = $"Import: {Baugruppen.Count} Kategorien, {Baugruppen.Sum(b => b.Typmarkierungen.Count)} Typen.";
        }

        // ----------------- Auto-Import beim Start -----------------
        private void TryAutoLoadFromSetting()
        {
            try
            {
                var path = Properties.Settings.Default.PfadBGKDatei;
                if (string.IsNullOrWhiteSpace(path)) return; // kein Auto-Load

                if (!File.Exists(path))
                {
                    StatusText = $"Gespeicherter Pfad nicht gefunden: {path}";
                    return;
                }

                ImportCsvFromPath(path);
                StatusText = $"{StatusText} (Auto-Import aus Einstellung)";
            }
            catch (Exception ex)
            {
                StatusText = $"Fehler beim Auto-Import: {ex.Message}";
            }
        }

        // ----------------- Helfer -----------------
        private static int IndexOfHeader(IReadOnlyList<string> header, string name)
        {
            for (int i = 0; i < header.Count; i++)
                if (string.Equals(header[i]?.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
                    return i;
            return -1;
        }

        private static Encoding DetectEncoding(string path)
        {
            using var fs = File.OpenRead(path);
            using var sr = new StreamReader(fs, Encoding.UTF8, true);
            sr.Peek(); // BOM-Erkennung
            return sr.CurrentEncoding;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var list = new List<string>();
            if (line == null) return list;

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '\"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '\"') { sb.Append('\"'); i++; }
                    else { inQuotes = !inQuotes; }
                    continue;
                }

                if (c == ',' && !inQuotes)
                {
                    list.Add(sb.ToString()); sb.Clear(); continue;
                }

                sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }

        // --- kleiner interner Handler, damit oben kein extra File nötig ist ---
        private sealed class BGKSendValuesHandler : IExternalEventHandler
        {
            public BGKManagerViewModel ViewModel { get; set; }
            public void Execute(UIApplication app) => ViewModel?.ExecuteSendValuesInApiContext(app);
            public string GetName() => "BGK Manager – Werte übertragen";
        }
    }
}
