using Autodesk.Revit.DB;
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

                    IList<Reference> pickedReferences = uidoc.Selection.PickObjects(
                        ObjectType.Element,
                        isFamilyDocument
                            ? new FamilyMaterialSelectionFilter()
                            : new ProjectMaterialSelectionFilter(),
                        isFamilyDocument
                            ? $"Familiengeometrie auswählen, die das Material \"{material.Name}\" erhalten soll"
                            : $"Elemente auswählen, die das Material \"{material.Name}\" erhalten sollen");

                    if (pickedReferences == null || pickedReferences.Count == 0)
                    {
                        _window.SetStatus(isFamilyDocument
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
                        MainContent = isFamilyDocument
                            ? $"Ausgewählte Familienelemente: {uniqueReferences.Count}\nDie Zuweisung erfolgt direkt im geöffneten Familiendokument."
                            : $"Ausgewählte Elemente: {uniqueReferences.Count}\nBei ladbaren Familien wird bei Bedarf die Familiengeometrie aktualisiert.",
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

                            if (TryAssignInProjectDocument(doc, element, material.Id, out string successDetail, out string failureReason))
                            {
                                successCount++;
                            }
                            else if (element is FamilyInstance inPlaceInstance
                                && inPlaceInstance.Symbol?.Family != null
                                && inPlaceInstance.Symbol.Family.IsInPlace
                                && TryAssignInInPlaceFamily(doc, inPlaceInstance, material.Id, out successDetail, out failureReason))
                            {
                                successCount++;
                            }
                            else if (element is FamilyInstance familyInstance && TryAssignViaEditableFamily(doc, familyInstance, material.Name, out successDetail, out failureReason))
                            {
                                successCount++;
                            }
                            else
                            {
                                failures.Add($"{DescribeElement(element)} – {failureReason}");
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
                    if (wasVisible)
                        _window.RestoreAndActivate();
                }
            }

            public string GetName() => nameof(AssignFavoriteMaterialHandler);

            private static bool TryAssignInProjectDocument(Document doc, Element element, ElementId materialId, out string successDetail, out string failureReason)
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

                    if (TryPaintElement(doc, element, materialId, out int paintedFaces))
                    {
                        tx.Commit();
                        successDetail = $"{paintedFaces} Fläche(n) wurden bemalt.";
                        return true;
                    }

                    tx.RollBack();
                }

                failureReason = "Kein schreibbarer Materialparameter und keine bemalbaren Flächen gefunden";
                return false;
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

                    Material familyMaterial = GetOrCreateMaterialByName(familyDoc, materialName);
                    if (familyMaterial == null)
                    {
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

                    int assignedCount = 0;
                    using (Transaction tx = new Transaction(familyDoc, "Material in ladbarer Familie zuweisen"))
                    {
                        tx.Start();

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

                    foreach (Element target in targets)
                    {
                        if (target == null || target.Id == familyInstance.Id)
                            continue;

                        if (TrySetMaterialParameter(projectDoc, target, materialId, out _))
                        {
                            assignedCount++;
                            continue;
                        }

                        if (TryPaintElement(projectDoc, target, materialId, out int paintedFaces) && paintedFaces > 0)
                            assignedCount++;
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
                        ? "In der In-Place-Familie wurde kein beschreibbarer Materialparameter und keine bemalbare Untergeometrie gefunden"
                        : "In der In-Place-Familie wurde kein beschreibbarer Materialparameter und keine bemalbare Untergeometrie gefunden. Kandidaten: " + sampleTargets;
                    return false;
                }

                successDetail = $"In-Place-Familie \"{familyInstance.Symbol.Family.Name}\" wurde über {assignedCount} Geometrieelement(e) aktualisiert.";
                return true;
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

                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId)
                        continue;

                    if (!LooksLikeMaterialParameter(doc, parameter))
                        continue;

                    try
                    {
                        parameter.Set(materialId);
                        parameterName = parameter.Definition?.Name ?? "Material";
                        return true;
                    }
                    catch
                    {
                        // Weiter mit dem nächsten Kandidaten.
                    }
                }

                return false;
            }

            private static bool LooksLikeMaterialParameter(Document doc, Parameter parameter)
            {
                string definitionName = parameter.Definition?.Name ?? string.Empty;
                if (definitionName.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                try
                {
                    ElementId currentId = parameter.AsElementId();
                    if (currentId != null && currentId != ElementId.InvalidElementId)
                        return doc.GetElement(currentId) is Material;
                }
                catch
                {
                    return false;
                }

                return false;
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
