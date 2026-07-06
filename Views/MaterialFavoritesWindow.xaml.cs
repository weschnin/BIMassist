using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Core;
using BimassistSettings = BIMassist.Properties.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BIMassist.Views
{
    public partial class MaterialFavoritesWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly ExternalEvent _addFavoriteEvent;
        private readonly AddFavoriteMaterialHandler _addFavoriteHandler;
        private readonly ExternalEvent _assignMaterialEvent;
        private readonly AssignFavoriteMaterialHandler _assignMaterialHandler;

        public ObservableCollection<string> FavoriteMaterials { get; } = new ObservableCollection<string>();

        public MaterialFavoritesWindow(UIApplication uiapp)
        {
            _uiapp = uiapp ?? throw new ArgumentNullException(nameof(uiapp));
            _addFavoriteHandler = new AddFavoriteMaterialHandler(this);
            _addFavoriteEvent = ExternalEvent.Create(_addFavoriteHandler);
            _assignMaterialHandler = new AssignFavoriteMaterialHandler(this);
            _assignMaterialEvent = ExternalEvent.Create(_assignMaterialHandler);

            InitializeComponent();
            FavoritesListBox.ItemsSource = FavoriteMaterials;
            LoadFavorites();
            UpdateStatusForCurrentSelection();
        }

        public string SelectedFavoriteMaterialName => FavoritesListBox.SelectedItem as string;

        internal void AddFavoriteMaterial(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
                return;

            string existing = FavoriteMaterials.FirstOrDefault(name =>
                string.Equals(name, materialName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                FavoritesListBox.SelectedItem = existing;
                SetStatus($"Material \"{existing}\" ist bereits als Favorit vorhanden.");
                return;
            }

            FavoriteMaterials.Add(materialName);
            SortFavorites();
            FavoritesListBox.SelectedItem = FavoriteMaterials.FirstOrDefault(name =>
                string.Equals(name, materialName, StringComparison.OrdinalIgnoreCase));
            SaveFavorites();
            SetStatus($"Material \"{materialName}\" wurde zu den Favoriten hinzugefügt.");
        }

        internal void SetStatus(string message, bool isError = false)
        {
            StatusTextBlock.Text = message ?? string.Empty;
            StatusTextBlock.Foreground = isError ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DimGray;
        }

        internal void RestoreAndActivate()
        {
            if (!IsVisible)
                Show();

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void AddFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetStatus("Materialauswahl wird geöffnet …");
                _addFavoriteEvent.Raise();
            }
            catch (Exception ex)
            {
                SetStatus("Materialauswahl konnte nicht geöffnet werden: " + ex.Message, true);
            }
        }

        private void AssignMaterialButton_Click(object sender, RoutedEventArgs e)
        {
            BeginAssignSelectedMaterial();
        }

        private void FavoritesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            BeginAssignSelectedMaterial();
        }

        private void FavoritesListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            string selected = SelectedFavoriteMaterialName;
            if (string.IsNullOrWhiteSpace(selected))
                return;

            FavoriteMaterials.Remove(selected);
            SaveFavorites();
            SetStatus($"Favorit \"{selected}\" wurde entfernt.");
            UpdateStatusForCurrentSelection();
            e.Handled = true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BeginAssignSelectedMaterial()
        {
            string selected = SelectedFavoriteMaterialName;
            if (string.IsNullOrWhiteSpace(selected))
            {
                SetStatus("Bitte zuerst ein Favoritenmaterial auswählen.", true);
                return;
            }

            try
            {
                _assignMaterialHandler.SelectedMaterialName = selected;
                SetStatus($"Elementauswahl für Material \"{selected}\" wird gestartet …");
                _assignMaterialEvent.Raise();
            }
            catch (Exception ex)
            {
                SetStatus("Materialzuweisung konnte nicht gestartet werden: " + ex.Message, true);
            }
        }

        private void LoadFavorites()
        {
            FavoriteMaterials.Clear();

            try
            {
                string json = BimassistSettings.Default.FavoritenMaterialienJson;
                if (string.IsNullOrWhiteSpace(json))
                    return;

                List<string> names = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                foreach (string name in names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name))
                    FavoriteMaterials.Add(name);
            }
            catch
            {
                // Bei fehlerhaften Benutzereinstellungen starten wir leer, statt das Fenster zu blockieren.
            }
        }

        private void SaveFavorites()
        {
            try
            {
                List<string> favorites = FavoriteMaterials
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name)
                    .ToList();

                BimassistSettings.Default.FavoritenMaterialienJson = JsonSerializer.Serialize(favorites);
                BimassistSettings.Default.Save();
            }
            catch (Exception ex)
            {
                SetStatus("Favoriten konnten nicht dauerhaft gespeichert werden: " + ex.Message, true);
            }
        }

        private void SortFavorites()
        {
            List<string> sorted = FavoriteMaterials.OrderBy(name => name).ToList();
            FavoriteMaterials.Clear();
            foreach (string item in sorted)
                FavoriteMaterials.Add(item);
        }

        private void UpdateStatusForCurrentSelection()
        {
            if (FavoriteMaterials.Count == 0)
                SetStatus("Noch keine Favoriten gespeichert. Mit \"Favorit hinzufügen\" kann ein Material hinterlegt werden.");
            else if (string.IsNullOrWhiteSpace(SelectedFavoriteMaterialName))
                SetStatus("Favoritenmaterial auswählen und danach Materialzuweisung starten.");
        }

        private sealed class AddFavoriteMaterialHandler : IExternalEventHandler
        {
            private readonly MaterialFavoritesWindow _window;

            public AddFavoriteMaterialHandler(MaterialFavoritesWindow window)
            {
                _window = window;
            }

            public void Execute(UIApplication app)
            {
                try
                {
                    Document doc = app.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        _window.SetStatus("Es ist kein aktives Revit-Dokument geöffnet.", true);
                        return;
                    }

                    List<string> materialNames = new FilteredElementCollector(doc)
                        .OfClass(typeof(Material))
                        .Cast<Material>()
                        .Select(material => material.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name)
                        .ToList();

                    if (materialNames.Count == 0)
                    {
                        _window.SetStatus("Im aktuellen Dokument wurden keine Materialien gefunden.", true);
                        return;
                    }

                    MaterialPickerWindow picker = new MaterialPickerWindow(materialNames);
                    bool? result = WpfOwner.ShowDialog(picker, app);
                    if (result == true && !string.IsNullOrWhiteSpace(picker.SelectedMaterialName))
                        _window.AddFavoriteMaterial(picker.SelectedMaterialName);
                    else
                        _window.SetStatus("Materialauswahl wurde abgebrochen.");
                }
                catch (Exception ex)
                {
                    _window.SetStatus("Fehler beim Hinzufügen eines Favoritenmaterials: " + ex.Message, true);
                }
            }

            public string GetName() => nameof(AddFavoriteMaterialHandler);
        }

        private sealed class AssignFavoriteMaterialHandler : IExternalEventHandler
        {
            private readonly MaterialFavoritesWindow _window;

            public AssignFavoriteMaterialHandler(MaterialFavoritesWindow window)
            {
                _window = window;
            }

            public string SelectedMaterialName { get; set; }

            public void Execute(UIApplication app)
            {
                bool wasVisible = _window.IsVisible;
                bool suppressRestoreAndActivate = false;

                try
                {
                    UIDocument uidoc = app.ActiveUIDocument;
                    Document doc = uidoc?.Document;
                    if (uidoc == null || doc == null)
                    {
                        _window.SetStatus("Es ist kein aktives Revit-Dokument geöffnet.", true);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(SelectedMaterialName))
                    {
                        _window.SetStatus("Es wurde kein Favoritenmaterial ausgewählt.", true);
                        return;
                    }

                    Material material = new FilteredElementCollector(doc)
                        .OfClass(typeof(Material))
                        .Cast<Material>()
                        .FirstOrDefault(m => string.Equals(m.Name, SelectedMaterialName, StringComparison.OrdinalIgnoreCase));

                    if (material == null)
                    {
                        _window.SetStatus($"Das Material \"{SelectedMaterialName}\" wurde im aktuellen Dokument nicht gefunden.", true);
                        return;
                    }

                    if (wasVisible)
                        _window.Hide();

                    bool isFamilyDocument = doc.IsFamilyDocument;
                    bool isInPlaceFamilyEditMode = IsInPlaceFamilyEditMode(doc);

                    IList<Reference> pickedReferences = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        isFamilyDocument || isInPlaceFamilyEditMode
                            ? new FamilyMaterialSelectionFilter()
                            : new ProjectMaterialSelectionFilter(),
                        isFamilyDocument
                            ? $"Familienelemente auswählen, die das Material \"{material.Name}\" erhalten sollen"
                            : isInPlaceFamilyEditMode
                                ? $"Formkörper/Extrusionen der geöffneten Projektfamilie auswählen, die das Material \"{material.Name}\" erhalten sollen"
                            : $"Ganze Elemente auswählen, die das Material \"{material.Name}\" erhalten sollen. Wände/Decken werden über den Typ geändert; Projektfamilien werden als ganze Elemente verarbeitet.");

                    if (pickedReferences == null || pickedReferences.Count == 0)
                    {
                        _window.SetStatus(isFamilyDocument || isInPlaceFamilyEditMode
                            ? "Es wurde keine Familiengeometrie ausgewählt."
                            : "Es wurden keine Elemente ausgewählt.");
                        return;
                    }

                    List<Reference> uniqueReferences = pickedReferences
                        .Where(reference => reference != null && reference.ElementId != null && reference.ElementId != ElementId.InvalidElementId)
                        .GroupBy(reference => reference.ElementId.Value)
                        .Select(group => group.First())
                        .ToList();

                    if (uniqueReferences.Count == 0)
                    {
                        _window.SetStatus("Die Auswahl enthielt keine gültigen Ziele.", true);
                        return;
                    }

                    TaskDialog confirmation = new TaskDialog("Material zuweisen")
                    {
                        MainInstruction = $"Material \"{material.Name}\" zuweisen?",
                        MainContent = isFamilyDocument || isInPlaceFamilyEditMode
                            ? $"Ausgewählte Familienelemente: {uniqueReferences.Count}\nDie Zuweisung erfolgt direkt im geöffneten Familiendokument."
                            : $"Ausgewählte Elemente: {uniqueReferences.Count}\nWände und Geschossdecken werden über die Typ-Schichten aktualisiert. Projektfamilien werden als ganze Elemente verarbeitet.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.Yes
                    };

                    if (confirmation.Show() != TaskDialogResult.Yes)
                    {
                        _window.SetStatus("Materialzuweisung wurde nicht bestätigt.");
                        return;
                    }

                    int successCount = 0;
                    List<string> failures = new List<string>();

                    if (isFamilyDocument)
                    {
                        using (Transaction tx = new Transaction(doc, "Material in Familie zuweisen"))
                        {
                            tx.Start();

                            foreach (Reference reference in uniqueReferences)
                            {
                                Element element = doc.GetElement(reference);
                                if (element == null)
                                {
                                    failures.Add("<unbekanntes Familienelement>");
                                    continue;
                                }

                                if (TryAssignInFamilyDocument(doc, element, material.Id, out string successDetail, out string failureReason))
                                {
                                    successCount++;
                                }
                                else
                                {
                                    failures.Add($"{DescribeElement(element)} – {failureReason}");
                                }
                            }

                            if (successCount > 0)
                                tx.Commit();
                            else
                                tx.RollBack();
                        }
                    }
                    else
                    {
                        foreach (Reference reference in uniqueReferences)
                        {
                            Element element = doc.GetElement(reference);
                            if (element == null)
                            {
                                failures.Add("<unbekanntes Element>");
                                continue;
                            }

                            if (IsPipeOrPipeSystemElement(element))
                            {
                                if (TryAssignPipeSystemMaterial(doc, element, material.Id, out string pipeSuccessDetail, out string pipeFailureReason))
                                {
                                    successCount++;
                                }
                                else
                                {
                                    failures.Add($"{DescribeElement(element)} – {pipeFailureReason}");
                                }

                                continue;
                            }

                            if (element is FamilyInstance familyInstance)
                            {
                                bool isInPlaceFamily = familyInstance.Symbol?.Family != null && familyInstance.Symbol.Family.IsInPlace;

                                if (isInPlaceFamily)
                                {
                                    // Revit lässt In-Place-/Projektfamilien nicht wie ladbare Familien per EditFamily bearbeiten.
                                    // Zusätzlich deaktiviert Revit BIMassist-Befehle im In-Place-Familienbearbeitungsmodus, sodass der
                                    // Formkörper dort nicht zuverlässig über dieses Add-in ausgewählt/materialisiert werden kann.
                                    // Kategorie- oder Paint-Fallbacks werden bewusst nicht als Erfolg gewertet, weil sie im Eigenschaftenfenster
                                    // weiterhin <Nach Kategorie> zeigen und nicht den echten Form-Materialparameter ändern.
                                    failures.Add($"{DescribeElement(element)} – Projektfamilien/In-Place-Familien werden von BIMassist nicht unterstützt. Revit blockiert die direkte API-Bearbeitung dieser Familien und deaktiviert BIMassist im Projektfamilien-Bearbeitungsmodus. Bitte das Material manuell im Revit-Bearbeitungsmodus der Projektfamilie setzen.");
                                    continue;
                                }

                                if (TryAssignViaEditableFamily(doc, familyInstance, material.Name, out string successDetail, out string failureReason))
                                {
                                    successCount++;
                                }
                                else
                                {
                                    failures.Add($"{DescribeElement(element)} – {failureReason}");
                                }

                                continue;
                            }

                            if (IsPipeElement(element))
                            {
                                if (TryAssignPipeSegmentMaterial(doc, element, material.Id, out string pipeSuccessDetail, out string pipeFailureReason))
                                {
                                    successCount++;
                                }
                                else
                                {
                                    failures.Add($"{DescribeElement(element)} – {pipeFailureReason}");
                                }

                                continue;
                            }

                            if (IsHostObjectBasedElement(doc, element))
                            {
                                if (TryAssignInHostObjectType(doc, element, material.Id, out string hostSuccessDetail, out string hostFailureReason))
                                {
                                    successCount++;
                                }
                                else
                                {
                                    failures.Add($"{DescribeElement(element)} – {hostFailureReason}");
                                }

                                continue;
                            }

                            if (TryAssignProjectParameterOnly(doc, element, material.Id, out string nonFamilySuccessDetail, out string nonFamilyFailureReason))
                            {
                                successCount++;
                            }
                            else if (element is GenericForm || element is CombinableElement)
                            {
                                failures.Add($"{DescribeElement(element)} – Der echte Materialparameter konnte nicht gesetzt werden ({nonFamilyFailureReason}). Bemalen/Kategorie-Material wird bei Projektfamilien-Formen nicht mehr als Erfolg gewertet, weil dann im Eigenschaftenfenster weiterhin <Nach Kategorie> steht.");
                            }
                            else if (TryPaintInProjectDocument(doc, element, material.Id, out nonFamilySuccessDetail, out string paintFailureReason))
                            {
                                successCount++;
                            }
                            else
                            {
                                string combinedFailure = string.Join(" | ", new[] { nonFamilyFailureReason, paintFailureReason }
                                    .Where(message => !string.IsNullOrWhiteSpace(message)));
                                failures.Add($"{DescribeElement(element)} – {combinedFailure}");
                            }
                        }
                    }

                    if (successCount > 0)
                    {
                        _window.SetStatus($"Material \"{material.Name}\" wurde {successCount} Element(en) zugewiesen.");
                    }
                    else
                    {
                        _window.SetStatus($"Material \"{material.Name}\" konnte keinem der ausgewählten Elemente zugewiesen werden.", true);
                    }

                    if (failures.Count > 0)
                    {
                        string details = string.Join("\n", failures.Take(12));
                        if (failures.Count > 12)
                            details += $"\n… und {failures.Count - 12} weitere.";

                        TaskDialog.Show("Material zuweisen",
                            $"Erfolgreich: {successCount}\nNicht zugewiesen: {failures.Count}\n\n{details}");
                    }
                    else if (successCount > 0)
                    {
                        TaskDialog.Show("Material zuweisen",
                            $"Material \"{material.Name}\" wurde {successCount} Element(en) zugewiesen.");
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    _window.SetStatus("Materialzuweisung wurde abgebrochen.");
                }
                catch (Exception ex)
                {
                    _window.SetStatus("Fehler bei der Materialzuweisung: " + ex.Message, true);
                }
                finally
                {
                    if (wasVisible && !suppressRestoreAndActivate)
                        _window.RestoreAndActivate();
                }
            }

            public string GetName() => nameof(AssignFavoriteMaterialHandler);

            private static bool IsInPlaceFamilyEditMode(Document doc)
            {
                if (doc == null)
                    return false;

                try
                {
                    return doc.IsInEditMode() && doc.GetActiveEditMode() == EditModeType.InPlaceFamily;
                }
                catch
                {
                    return false;
                }
            }

            private static bool TryPostEditInPlaceFamily(UIApplication app, UIDocument uidoc, FamilyInstance familyInstance, out string failureReason)
            {
                failureReason = string.Empty;

                try
                {
                    if (app == null || uidoc == null || familyInstance == null)
                    {
                        failureReason = "Ungültiger Revit-Kontext";
                        return false;
                    }

                    uidoc.Selection.SetElementIds(new List<ElementId> { familyInstance.Id });

                    RevitCommandId editInPlaceFamilyCommand = RevitCommandId.LookupCommandId("ID_EDIT_INPLACE_FAMILY");
                    if (editInPlaceFamilyCommand == null)
                    {
                        failureReason = "Revit-Befehl ID_EDIT_INPLACE_FAMILY wurde nicht gefunden";
                        return false;
                    }

                    if (!app.CanPostCommand(editInPlaceFamilyCommand))
                    {
                        failureReason = "Revit erlaubt den Befehl momentan nicht";
                        return false;
                    }

                    app.PostCommand(editInPlaceFamilyCommand);
                    return true;
                }
                catch (Exception ex)
                {
                    failureReason = ex.Message;
                    return false;
                }
            }

            private static string GetReferenceKey(Document doc, Reference reference)
            {
                if (reference == null)
                    return string.Empty;

                try
                {
                    string stableRepresentation = reference.ConvertToStableRepresentation(doc);
                    if (!string.IsNullOrWhiteSpace(stableRepresentation))
                        return stableRepresentation;
                }
                catch
                {
                    // Some references cannot be converted in all edit contexts; fall back to ids.
                }

                return $"{reference.ElementId?.Value ?? -1}:{reference.LinkedElementId?.Value ?? -1}:{reference.ElementReferenceType}";
            }

            private static bool TryAssignInProjectDocument(Document doc, Element element, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                if (TryAssignProjectParameterOnly(doc, element, materialId, out successDetail, out failureReason))
                    return true;

                if (TryPaintInProjectDocument(doc, element, materialId, out successDetail, out failureReason))
                    return true;

                failureReason = "Kein schreibbarer Materialparameter und keine bemalbaren Flächen gefunden";
                return false;
            }

            private static bool TryAssignProjectParameterOnly(Document doc, Element element, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                using (Transaction tx = new Transaction(doc, "Material zuweisen"))
                {
                    tx.Start();

                    if (TrySetMaterialParameter(doc, element, materialId, out string parameterName))
                    {
                        tx.Commit();
                        successDetail = $"Parameter \"{parameterName}\" wurde gesetzt.";
                        return true;
                    }

                    tx.RollBack();
                }

                failureReason = "Kein schreibbarer Materialparameter gefunden";
                return false;
            }

            private static bool TryPaintInProjectDocument(Document doc, Element element, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                using (Transaction tx = new Transaction(doc, "Material durch Bemalung zuweisen"))
                {
                    tx.Start();

                    if (TryPaintElement(doc, element, materialId, out int paintedFaces))
                    {
                        tx.Commit();
                        successDetail = $"{paintedFaces} Fläche(n) wurden bemalt.";
                        return true;
                    }

                    tx.RollBack();
                }

                failureReason = "Keine bemalbaren Flächen gefunden";
                return false;
            }

            private static bool TryPaintPickedFaceInProjectDocument(Document doc, Reference reference, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                if (doc == null || reference == null || reference.ElementId == null || reference.ElementId == ElementId.InvalidElementId)
                {
                    failureReason = "Ungültige Flächenreferenz";
                    return false;
                }

                Element element = doc.GetElement(reference.ElementId);
                if (element == null)
                {
                    failureReason = "Zur ausgewählten Fläche wurde kein Element gefunden";
                    return false;
                }

                Face face = null;
                try
                {
                    face = element.GetGeometryObjectFromReference(reference) as Face;
                }
                catch (Exception ex)
                {
                    failureReason = "Fläche konnte aus der Auswahl nicht gelesen werden: " + ex.Message;
                    return false;
                }

                if (face == null)
                {
                    failureReason = "Die Auswahl enthält keine echte Fläche";
                    return false;
                }

                using (Transaction tx = new Transaction(doc, "Projektfamilienfläche bemalen"))
                {
                    tx.Start();

                    try
                    {
                        doc.Paint(element.Id, face, materialId);
                        ElementId paintedMaterialId = doc.GetPaintedMaterial(element.Id, face);
                        if (paintedMaterialId == materialId)
                        {
                            tx.Commit();
                            successDetail = "Ausgewählte Projektfamilienfläche wurde bemalt.";
                            return true;
                        }

                        tx.RollBack();
                        failureReason = "Revit hat Paint akzeptiert, aber das Material danach nicht auf der Fläche zurückgemeldet";
                        return false;
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        failureReason = ex.Message;
                        return false;
                    }
                }
            }

            private static bool TryAssignInFamilyDocument(Document familyDoc, Element element, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                if (familyDoc == null || element == null)
                {
                    failureReason = "Ungültiges Familienziel";
                    return false;
                }

                Parameter builtInMaterial = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (builtInMaterial != null && !builtInMaterial.IsReadOnly)
                {
                    builtInMaterial.Set(materialId);
                    successDetail = "Builtin-Parameter MATERIAL_ID_PARAM wurde gesetzt.";
                    return true;
                }

                if (TrySetMaterialParameter(familyDoc, element, materialId, out string parameterName))
                {
                    successDetail = $"Parameter \"{parameterName}\" wurde gesetzt.";
                    return true;
                }

                failureReason = "Im ausgewählten Familienelement wurde kein schreibbarer Materialparameter gefunden";
                return false;
            }

            private static bool TryAssignViaEditableFamily(Document projectDoc, FamilyInstance familyInstance, string materialName, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                Document familyDoc = null;

                try
                {
                    if (familyInstance?.Symbol?.Family == null)
                    {
                        failureReason = "Die Familieninstanz besitzt keine bearbeitbare Familie";
                        return false;
                    }

                    familyDoc = projectDoc.EditFamily(familyInstance.Symbol.Family);
                    if (familyDoc == null)
                    {
                        failureReason = "Die Familie konnte nicht zum Bearbeiten geöffnet werden";
                        return false;
                    }

                    int assignedCount = 0;
                    using (Transaction tx = new Transaction(familyDoc, "Material in ladbarer Familie zuweisen"))
                    {
                        tx.Start();

                        Material familyMaterial = GetOrCreateMaterialByName(familyDoc, materialName);
                        if (familyMaterial == null)
                        {
                            tx.RollBack();
                            failureReason = $"Das Material \"{materialName}\" konnte in der Familie nicht bereitgestellt werden";
                            return false;
                        }

                        FamilyManager familyManager = familyDoc.FamilyManager;
                        if (familyManager != null)
                        {
                            FamilyType matchingType = familyManager.Types
                                .Cast<FamilyType>()
                                .FirstOrDefault(type => string.Equals(type.Name, familyInstance.Symbol.Name, StringComparison.OrdinalIgnoreCase));

                            if (matchingType != null)
                                familyManager.CurrentType = matchingType;
                            else if (familyManager.CurrentType == null)
                                familyManager.CurrentType = familyManager.Types.Cast<FamilyType>().FirstOrDefault();
                        }

                        foreach (Element familyElement in new FilteredElementCollector(familyDoc).WhereElementIsNotElementType())
                        {
                            if (!CanBeFamilyMaterialTarget(familyDoc, familyElement))
                                continue;

                            if (TryAssignInFamilyDocument(familyDoc, familyElement, familyMaterial.Id, out _, out _))
                                assignedCount++;
                        }

                        if (assignedCount > 0)
                            tx.Commit();
                        else
                            tx.RollBack();
                    }

                    if (assignedCount == 0)
                    {
                        failureReason = "In der ladbaren Familie wurde kein schreibbarer Materialparameter an der Geometrie gefunden";
                        return false;
                    }

                    familyDoc.LoadFamily(projectDoc, new JtFamilyLoadOptions());
                    successDetail = $"Familie \"{familyInstance.Symbol.Family.Name}\" wurde aktualisiert ({assignedCount} Geometrieelement(e)).";
                    return true;
                }
                catch (Exception ex)
                {
                    failureReason = "Bearbeitung der ladbaren Familie fehlgeschlagen: " + ex.Message;
                    return false;
                }
                finally
                {
                    if (familyDoc != null && familyDoc.IsModifiable)
                    {
                        try
                        {
                            familyDoc.Close(false);
                        }
                        catch
                        {
                            // Best effort cleanup.
                        }
                    }
                    else if (familyDoc != null)
                    {
                        try
                        {
                            familyDoc.Close(false);
                        }
                        catch
                        {
                            // Best effort cleanup.
                        }
                    }
                }
            }

            private static bool TryAssignInInPlaceFamily(Document projectDoc, FamilyInstance familyInstance, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                if (projectDoc == null || familyInstance == null)
                {
                    failureReason = "Ungültige In-Place-Familie";
                    return false;
                }

                List<Element> targets = CollectInPlaceFamilyTargets(familyInstance)
                    .Where(target => target != null)
                    .GroupBy(target => target.Id.Value)
                    .Select(group => group.First())
                    .ToList();

                if (targets.Count == 0)
                {
                    failureReason = "Für die In-Place-Familie wurden keine untergeordneten Geometrieelemente gefunden";
                    return false;
                }

                int assignedCount = 0;
                using (Transaction tx = new Transaction(projectDoc, "Material in In-Place-Familie zuweisen"))
                {
                    tx.Start();

                    if (TrySetMaterialParameter(projectDoc, familyInstance, materialId, out _))
                        assignedCount++;

                    ElementId typeId = familyInstance.GetTypeId();
                    Element typeElement = typeId != null && typeId != ElementId.InvalidElementId
                        ? projectDoc.GetElement(typeId)
                        : null;

                    if (typeElement != null && TrySetMaterialParameter(projectDoc, typeElement, materialId, out _))
                        assignedCount++;

                    foreach (Element target in targets)
                    {
                        if (target == null)
                            continue;

                        if (target.Id == familyInstance.Id || (typeElement != null && target.Id == typeElement.Id))
                            continue;

                        if (TrySetMaterialParameter(projectDoc, target, materialId, out _))
                        {
                            assignedCount++;
                            continue;
                        }

                        // Bei Projektfamilien zählt nur ein echter Materialparameter.
                        // Kategorie-Material oder Paint können sichtbar wirken, lassen im Eigenschaftenfenster aber weiterhin <Nach Kategorie> stehen.
                    }

                    if (assignedCount > 0)
                        tx.Commit();
                    else
                        tx.RollBack();
                }

                if (assignedCount == 0)
                {
                    string sampleTargets = string.Join(", ", targets
                        .Where(target => target != null && target.Id != familyInstance.Id)
                        .Take(5)
                        .Select(DescribeElement));

                    failureReason = string.IsNullOrWhiteSpace(sampleTargets)
                        ? "In der In-Place-Familie wurde kein beschreibbarer echter Materialparameter gefunden"
                        : "In der In-Place-Familie wurde kein beschreibbarer echter Materialparameter gefunden. Kandidaten: " + sampleTargets;
                    return false;
                }

                successDetail = $"In-Place-Familie \"{familyInstance.Symbol.Family.Name}\" wurde über {assignedCount} Geometrieelement(e) aktualisiert.";
                return true;
            }

            private static bool IsHostObjectBasedElement(Document doc, Element element)
            {
                ElementId typeId = element?.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return false;

                return doc.GetElement(typeId) is HostObjAttributes;
            }

            private static bool IsPipeOrPipeSystemElement(Element element)
            {
                if (IsPipeElement(element))
                    return true;

                if (element == null)
                    return false;

                if (element is MEPSystem)
                    return true;

                string categoryName = element.Category?.Name ?? string.Empty;
                if (categoryName.IndexOf("Rohrsystem", StringComparison.OrdinalIgnoreCase) >= 0
                    || categoryName.IndexOf("Piping System", StringComparison.OrdinalIgnoreCase) >= 0
                    || categoryName.IndexOf("Pipe System", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                return false;
            }

            private static bool IsPipeElement(Element element)
            {
                if (element == null)
                    return false;

                if (element is Pipe)
                    return true;

                BuiltInCategory? category = TryGetBuiltInCategory(element.Category?.Id);
                return category == BuiltInCategory.OST_PipeCurves
                    || category == BuiltInCategory.OST_FlexPipeCurves
                    || category == BuiltInCategory.OST_PlaceHolderPipes;
            }

            private static bool TryAssignPipeSystemMaterial(Document doc, Element pipeOrSystemElement, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                if (doc == null || pipeOrSystemElement == null)
                {
                    failureReason = "Ungültiger Rohrsystem-Kontext";
                    return false;
                }

                List<Element> targets = ResolvePipeSystemMaterialTargets(doc, pipeOrSystemElement)
                    .Where(target => target != null)
                    .GroupBy(target => target.Id.Value)
                    .Select(group => group.First())
                    .ToList();

                if (targets.Count == 0)
                {
                    failureReason = "Zum ausgewählten Rohr/Rohrsystem konnte kein Rohrsystemtyp mit Materialparameter ermittelt werden";
                    return false;
                }

                List<string> attemptedTargets = new List<string>();
                foreach (Element target in targets)
                {
                    using (Transaction tx = new Transaction(doc, "Material im Rohrsystem zuweisen"))
                    {
                        tx.Start();

                        if (TrySetMaterialParameter(doc, target, materialId, out string parameterName))
                        {
                            tx.Commit();

                            Element reloadedTarget = doc.GetElement(target.Id) ?? target;
                            if (HasMaterialParameterValue(doc, reloadedTarget, materialId, out string verifiedParameterName))
                            {
                                successDetail = $"Rohrsystem/Typ \"{reloadedTarget.Name}\" wurde über Parameter \"{verifiedParameterName ?? parameterName}\" gesetzt. Hinweis: Das betrifft alle Rohre, die dieses Rohrsystem bzw. diesen Systemtyp verwenden.";
                                return true;
                            }

                            failureReason = $"Rohrsystem/Typ \"{target.Name}\" hat das Material nach der Transaktion nicht verifiziert übernommen";
                            return false;
                        }

                        tx.RollBack();
                    }

                    attemptedTargets.Add($"{target.Name} ({target.GetType().Name})");
                }

                failureReason = "Kein beschreibbarer Materialparameter am Rohrsystem/Rohrsystemtyp gefunden";
                if (attemptedTargets.Count > 0)
                    failureReason += ": " + string.Join(", ", attemptedTargets);

                return false;
            }

            private static List<Element> ResolvePipeSystemMaterialTargets(Document doc, Element pipeOrSystemElement)
            {
                List<Element> targets = new List<Element>();

                void AddTarget(ElementId id)
                {
                    if (id == null || id == ElementId.InvalidElementId)
                        return;

                    Element target = doc.GetElement(id);
                    if (target != null)
                        targets.Add(target);
                }

                if (pipeOrSystemElement is Pipe pipe)
                {
                    try
                    {
                        AddTarget(pipe.MEPSystem?.GetTypeId());
                        AddTarget(pipe.MEPSystem?.Id);
                    }
                    catch
                    {
                        // Einige Platzhalter/defekte Systeme liefern kein MEPSystem.
                    }
                }

                try
                {
                    Parameter systemTypeParameter = pipeOrSystemElement.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
                    AddTarget(systemTypeParameter?.AsElementId());
                }
                catch
                {
                    // Parameter ist nicht auf allen MEP-Elementen verfügbar.
                }

                if (pipeOrSystemElement is MEPSystem)
                {
                    AddTarget(pipeOrSystemElement.GetTypeId());
                    AddTarget(pipeOrSystemElement.Id);
                }

                string categoryName = pipeOrSystemElement.Category?.Name ?? string.Empty;
                if (categoryName.IndexOf("Rohrsystem", StringComparison.OrdinalIgnoreCase) >= 0
                    || categoryName.IndexOf("Piping System", StringComparison.OrdinalIgnoreCase) >= 0
                    || categoryName.IndexOf("Pipe System", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    AddTarget(pipeOrSystemElement.GetTypeId());
                    AddTarget(pipeOrSystemElement.Id);
                }

                return targets;
            }

            private static bool HasMaterialParameterValue(Document doc, Element element, ElementId materialId, out string parameterName)
            {
                parameterName = string.Empty;

                if (element == null)
                    return false;

                Parameter builtInMaterial = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (builtInMaterial != null && ParameterHasMaterialValue(builtInMaterial, materialId))
                {
                    parameterName = builtInMaterial.Definition?.Name ?? "MATERIAL_ID_PARAM";
                    return true;
                }

                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null || parameter.StorageType != StorageType.ElementId)
                        continue;

                    if (!LooksLikeMaterialParameter(doc, parameter))
                        continue;

                    if (ParameterHasMaterialValue(parameter, materialId))
                    {
                        parameterName = parameter.Definition?.Name ?? "Material";
                        return true;
                    }
                }

                return false;
            }

            private static bool TryAssignPipeSegmentMaterial(Document doc, Element pipeElement, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                if (doc == null || pipeElement == null)
                {
                    failureReason = "Ungültiger Rohr-Kontext";
                    return false;
                }

                Parameter segmentParameter = pipeElement.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM);
                ElementId segmentId = segmentParameter?.AsElementId();
                if (segmentId == null || segmentId == ElementId.InvalidElementId)
                {
                    failureReason = "Rohr besitzt keinen auslesbaren Rohrsegment-Parameter";
                    return false;
                }

                if (doc.GetElement(segmentId) is not Segment segment)
                {
                    failureReason = "Das Rohrsegment konnte nicht als bearbeitbares MEP-Segment geladen werden";
                    return false;
                }

                using (Transaction tx = new Transaction(doc, "Material in Rohrsegment zuweisen"))
                {
                    tx.Start();

                    Parameter pipeMaterialParameter = segment.get_Parameter(BuiltInParameter.RBS_PIPE_MATERIAL_PARAM);
                    if (pipeMaterialParameter == null || pipeMaterialParameter.IsReadOnly || pipeMaterialParameter.StorageType != StorageType.ElementId)
                    {
                        tx.RollBack();
                        failureReason = "Rohrsegment besitzt keinen beschreibbaren Material-Parameter";
                        return false;
                    }

                    try
                    {
                        pipeMaterialParameter.Set(materialId);
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        failureReason = $"Rohrsegment-Material konnte nicht gesetzt werden: {ex.Message}";
                        return false;
                    }

                    if (!ParameterHasMaterialValue(pipeMaterialParameter, materialId))
                    {
                        tx.RollBack();
                        failureReason = "Rohrsegment hat das Material nach dem Setzen nicht übernommen";
                        return false;
                    }

                    tx.Commit();
                }

                try
                {
                    doc.Regenerate();
                }
                catch
                {
                    // Regenerate kann in manchen Revit-Kontexten unnötig/gesperrt sein; die Parameterprüfung unten bleibt maßgeblich.
                }

                Element reloadedSegment = doc.GetElement(segmentId);
                Parameter verifiedMaterialParameter = reloadedSegment?.get_Parameter(BuiltInParameter.RBS_PIPE_MATERIAL_PARAM);
                bool verifiedByParameter = verifiedMaterialParameter != null && ParameterHasMaterialValue(verifiedMaterialParameter, materialId);
                bool verifiedBySegmentProperty = reloadedSegment is Segment verifiedSegment && verifiedSegment.MaterialId == materialId;

                if (!verifiedByParameter && !verifiedBySegmentProperty)
                {
                    ElementId currentParameterValue = null;
                    try
                    {
                        currentParameterValue = verifiedMaterialParameter?.AsElementId();
                    }
                    catch
                    {
                        // Nur für Diagnosemeldung.
                    }

                    failureReason = $"Rohrsegment-Material konnte nach der Transaktion nicht verifiziert werden (Parameterwert: {FormatElementId(currentParameterValue)}, erwartetes Material: {FormatElementId(materialId)})";
                    return false;
                }

                successDetail = $"Rohrsegment \"{reloadedSegment?.Name ?? segment.Name}\" wurde auf das Favoritenmaterial gesetzt. Hinweis: Das betrifft alle Rohre, die dieses Rohrsegment verwenden.";
                return true;
            }

            private static BuiltInCategory? TryGetBuiltInCategory(ElementId categoryId)
            {
                if (categoryId == null || categoryId == ElementId.InvalidElementId)
                    return null;

                try
                {
                    return (BuiltInCategory)categoryId.Value;
                }
                catch
                {
                    return null;
                }
            }

            private static string FormatElementId(ElementId id)
            {
                if (id == null)
                    return "<null>";

                try
                {
                    return id.Value.ToString();
                }
                catch
                {
                    return id.ToString();
                }
            }

            private static bool TryAssignInHostObjectType(Document doc, Element element, ElementId materialId, out string successDetail, out string failureReason)
            {
                successDetail = string.Empty;
                failureReason = string.Empty;

                ElementId typeId = element?.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                {
                    failureReason = "Element besitzt keinen bearbeitbaren Typ für eine Materialzuweisung";
                    return false;
                }

                Element typeElement = doc.GetElement(typeId);
                if (typeElement is not HostObjAttributes hostType)
                {
                    failureReason = "Elementtyp ist kein Host-Typ mit Compound Structure";
                    return false;
                }

                using (Transaction tx = new Transaction(doc, "Material in Systemfamilientyp zuweisen"))
                {
                    tx.Start();

                    CompoundStructure compoundStructure = hostType.GetCompoundStructure();
                    if (compoundStructure == null || compoundStructure.LayerCount == 0)
                    {
                        tx.RollBack();
                        failureReason = "Host-Typ besitzt keine bearbeitbare Compound Structure";
                        return false;
                    }

                    int changedLayers = 0;
                    for (int layerIndex = 0; layerIndex < compoundStructure.LayerCount; layerIndex++)
                    {
                        compoundStructure.SetMaterialId(layerIndex, materialId);
                        changedLayers++;
                    }

                    if (changedLayers == 0)
                    {
                        tx.RollBack();
                        failureReason = "In der Compound Structure konnten keine Materiallagen aktualisiert werden";
                        return false;
                    }

                    hostType.SetCompoundStructure(compoundStructure);
                    tx.Commit();
                    successDetail = $"Alle {changedLayers} Schicht(en) des Typs \"{typeElement.Name}\" wurden auf das Favoritenmaterial gesetzt.";
                    return true;
                }
            }

            private static List<Element> CollectInPlaceFamilyTargets(FamilyInstance familyInstance)
            {
                List<Element> targets = new List<Element>();
                if (familyInstance == null)
                    return targets;

                Document doc = familyInstance.Document;
                HashSet<long> visited = new HashSet<long>();
                Queue<ElementId> queue = new Queue<ElementId>();
                queue.Enqueue(familyInstance.Id);

                while (queue.Count > 0)
                {
                    ElementId currentId = queue.Dequeue();
                    if (currentId == null || currentId == ElementId.InvalidElementId || !visited.Add(currentId.Value))
                        continue;

                    Element current = doc.GetElement(currentId);
                    if (current == null)
                        continue;

                    targets.Add(current);

                    if (current is FamilyInstance nestedFamilyInstance)
                    {
                        foreach (ElementId subComponentId in nestedFamilyInstance.GetSubComponentIds())
                        {
                            if (subComponentId != null && subComponentId != ElementId.InvalidElementId)
                                queue.Enqueue(subComponentId);
                        }
                    }

                    try
                    {
                        ICollection<ElementId> dependentIds = current.GetDependentElements(null);
                        if (dependentIds == null)
                            continue;

                        foreach (ElementId dependentId in dependentIds)
                        {
                            if (dependentId != null && dependentId != ElementId.InvalidElementId)
                                queue.Enqueue(dependentId);
                        }
                    }
                    catch
                    {
                        // Nicht jedes Element erlaubt eine stabile Abhängigkeitsabfrage.
                    }
                }

                return targets;
            }

            private static Material GetOrCreateMaterialByName(Document doc, string materialName)
            {
                Material existing = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .FirstOrDefault(material => string.Equals(material.Name, materialName, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                    return existing;

                ElementId materialId = Material.Create(doc, materialName);
                return doc.GetElement(materialId) as Material;
            }

            private static bool CanBeFamilyMaterialTarget(Document doc, Element element)
            {
                if (element == null)
                    return false;

                Parameter builtInMaterial = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (builtInMaterial != null && !builtInMaterial.IsReadOnly)
                    return true;

                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId)
                        continue;

                    if (LooksLikeMaterialParameter(doc, parameter))
                        return true;
                }

                return false;
            }

            private static bool TrySetMaterialParameter(Document doc, Element element, ElementId materialId, out string parameterName)
            {
                parameterName = string.Empty;

                Parameter builtInMaterial = element?.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (builtInMaterial != null && !builtInMaterial.IsReadOnly)
                {
                    try
                    {
                        builtInMaterial.Set(materialId);
                        if (ParameterHasMaterialValue(builtInMaterial, materialId))
                        {
                            parameterName = builtInMaterial.Definition?.Name ?? "MATERIAL_ID_PARAM";
                            return true;
                        }
                    }
                    catch
                    {
                        // Weiter mit anderen Materialparametern.
                    }
                }

                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId)
                        continue;

                    if (!LooksLikeMaterialParameter(doc, parameter))
                        continue;

                    try
                    {
                        parameter.Set(materialId);
                        if (ParameterHasMaterialValue(parameter, materialId))
                        {
                            parameterName = parameter.Definition?.Name ?? "Material";
                            return true;
                        }
                    }
                    catch
                    {
                        // Weiter mit dem nächsten Kandidaten.
                    }
                }

                return false;
            }

            private static bool ParameterHasMaterialValue(Parameter parameter, ElementId expectedMaterialId)
            {
                try
                {
                    ElementId assignedId = parameter.AsElementId();
                    return assignedId != null && assignedId == expectedMaterialId;
                }
                catch
                {
                    return false;
                }
            }

            private static bool LooksLikeMaterialParameter(Document doc, Parameter parameter)
            {
                try
                {
                    ForgeTypeId dataType = parameter.Definition?.GetDataType();
                    if (dataType != null && dataType == SpecTypeId.Reference.Material)
                        return true;
                }
                catch
                {
                    // Fallback auf ältere/unsichere Heuristik weiter unten.
                }

                string definitionName = parameter.Definition?.Name ?? string.Empty;
                if (definitionName.IndexOf("material", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                try
                {
                    ElementId currentId = parameter.AsElementId();
                    if (currentId == null || currentId == ElementId.InvalidElementId)
                        return true;

                    return doc.GetElement(currentId) is Material;
                }
                catch
                {
                    return false;
                }
            }

            private static bool TryPaintElement(Document doc, Element element, ElementId materialId, out int paintedFaces)
            {
                paintedFaces = 0;

                foreach (Face face in EnumerateFaces(element))
                {
                    try
                    {
                        doc.Paint(element.Id, face, materialId);
                        paintedFaces++;
                    }
                    catch
                    {
                        // Einzelne Flächen können unbemalbar sein; restliche weiter versuchen.
                    }
                }

                return paintedFaces > 0;
            }

            private static IEnumerable<Face> EnumerateFaces(Element element)
            {
                Options options = new Options
                {
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = true,
                    DetailLevel = ViewDetailLevel.Fine
                };

                GeometryElement geometry = element.get_Geometry(options);
                if (geometry == null)
                    yield break;

                foreach (Face face in EnumerateFaces(geometry))
                    yield return face;
            }

            private static IEnumerable<Face> EnumerateFaces(GeometryElement geometry)
            {
                foreach (GeometryObject geometryObject in geometry)
                {
                    if (geometryObject is Solid solid)
                    {
                        foreach (Face face in solid.Faces)
                            yield return face;
                    }
                    else if (geometryObject is GeometryInstance instance)
                    {
                        GeometryElement instanceGeometry = instance.GetInstanceGeometry();
                        if (instanceGeometry == null)
                            continue;

                        foreach (Face face in EnumerateFaces(instanceGeometry))
                            yield return face;
                    }
                }
            }

            private static string DescribeElement(Element element)
            {
                string typeName = element.GetType().Name;
                string name;

                try
                {
                    name = element.Name;
                }
                catch
                {
                    name = string.Empty;
                }

                return string.IsNullOrWhiteSpace(name)
                    ? $"{typeName} ({element.Id})"
                    : $"{typeName} \"{name}\" ({element.Id})";
            }
        }

        private sealed class ProjectMaterialSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element != null && element.Category != null;
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }

        private sealed class FamilyMaterialSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element != null && !(element is ElementType);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }
    }
}
