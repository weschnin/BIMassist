using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB.Structure;
using BIMassist.Core;
using System.IO;

namespace BIMassist.Services
{
    internal sealed record Bgk3DTextBuildResult(FamilySymbol Symbol, int AddedTypes, bool NewFamily);

    /// <summary>
    /// Erstellt/ergänzt ausschließlich BGK-3DText-Familien. Jede Textgeometrie hat einen
    /// eigene verschachtelte Familie; deren Instanzsichtbarkeit wird an den äußeren Typ gebunden.
    /// </summary>
    internal static class Bgk3DTextMultiFamilyBuilder
    {
        private const string MarkerName = "BIMassist BGK-3DText";
        private const string MarkerValue = "BIMassist BGK-3DText v2";
        private const string AvaName = "AVA_Anzahl";
        // Nur zur sicheren Erkennung bereits vorhandener BGK-Familien mit altem Shared-Parameter.
        private static readonly Guid AvaGuid = new("07aa7e7a-5dca-4367-a572-641a87ef7670");
        private static readonly Guid GeometryOwnerSchemaId = new("7ff5b775-81fa-49ce-b573-e2d83f4bb95f");
        private const string GeometryOwnerField = "BGKTypeName";
        private sealed record ExistingInstanceSnapshot(ElementId Id, string TypeName, Transform Transform,
            long HostId, long LevelId, bool Pinned, IReadOnlyList<string> Values);

        internal static Bgk3DTextBuildResult Build(UIApplication app, Document project,
            Bgk3DTextBatch batch, double heightMm, double depthMm)
        {
            if (project.IsFamilyDocument || project.IsReadOnly || project.IsModifiable)
                throw new InvalidOperationException("Bitte ein bearbeitbares Projekt ohne offene Transaktion verwenden.");
            if (batch.FamilyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || batch.FamilyName.EndsWith(".", StringComparison.Ordinal))
                throw new InvalidOperationException("BGK-Nummer und -Bezeichnung ergeben keinen gültigen Familiennamen.");

            List<Family> families = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>().ToList();
            Family? existing = families.FirstOrDefault(f =>
                string.Equals(f.Name, batch.FamilyName, StringComparison.OrdinalIgnoreCase));
            if (existing != null && (existing.IsInPlace || !existing.IsEditable))
                throw new InvalidOperationException("Die vorhandene BGK-Familie ist nicht als ladbare Familie bearbeitbar.");
            // Der alte Family-Wrapper kann beim Reload ungültig werden; dessen ID
            // muss vor dem LoadFamily-Aufruf als Wert gesichert sein.
            long? existingFamilyId = existing?.Id.Value;
            HashSet<long> originalIds = families.Select(f => f.Id.Value).ToHashSet();
            List<ExistingInstanceSnapshot> priorInstances = existing == null ? new() :
                new FilteredElementCollector(project).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(i => i.Symbol?.Family.Id == existing.Id)
                    .Select(SnapshotInstance).ToList();

            Document? familyDoc = null;
            string? tempDir = null;
            try
            {
                bool isNew = existing == null;
                if (isNew)
                {
                    string template = ResolveTemplate(app);
                    familyDoc = app.Application.NewFamilyDocument(template);
                }
                else
                {
                    familyDoc = project.EditFamily(existing!);
                }
                if (familyDoc.OwnerFamily?.FamilyCategory?.Id.Value != (long)BuiltInCategory.OST_GenericModel)
                    throw new InvalidOperationException("Die BGK-Familie gehört nicht zur Kategorie 'Allgemeine Modelle'.");

                FamilyManager manager = familyDoc.FamilyManager;
                FamilyParameter? avaParameter = FindParameter(manager, AvaName);
                bool legacySharedAva = existing != null && avaParameter?.IsShared == true;
                bool avaMissing = !ValidateAvaParameter(avaParameter, legacySharedAva);
                if (existing != null && legacySharedAva) VerifyProjectAvaDefinition(project);
                List<FamilyType> originalTypes = manager.Types.Cast<FamilyType>().ToList();
                FamilyParameter? marker = FindParameter(manager, MarkerName);
                bool legacy = existing != null && marker == null;
                List<FamilyType> managedTypes = existing == null ? new() : originalTypes
                    .Where(t => marker != null ? t.AsString(marker) == MarkerValue : IsLegacyType(manager, t, batch.FamilyName))
                    .ToList();
                List<FamilyType> emptyTypes = originalTypes.Except(managedTypes).ToList();
                List<FreeFormElement> legacyForms = new();
                if (existing != null)
                {
                    if (emptyTypes.Count != 0)
                        throw new InvalidOperationException("Die bestehende Familie besitzt nicht zugeordnete Typen. Deren Parameter und Verwendungen können nicht sicher erhalten werden; Ergänzung abgebrochen.");
                    if (legacy)
                        throw new InvalidOperationException("Die alte BGK-Familie enthält direkte Freiformen. Deren Material, Unterkategorie und Formeigenschaften lassen sich derzeit nicht verlustfrei in Unterfamilien migrieren; sie wurde nicht verändert.");
                    if (managedTypes.Count == 0 || (legacy && managedTypes.Count != 1)
                        || emptyTypes.Any(t => !IsEmptyTemplateType(manager, t))
                        || (!legacy && emptyTypes.Count != 0))
                        throw new InvalidOperationException("Die gleichnamige Familie ist keine eindeutig zuordenbare BIMassist-BGK-3DText-Familie. Sie wurde nicht verändert.");
                    foreach (FamilyType empty in emptyTypes)
                    {
                        FamilySymbol? unused = new FilteredElementCollector(project).OfClass(typeof(FamilySymbol))
                            .Cast<FamilySymbol>().FirstOrDefault(s => s.Family.Id == existing.Id && s.Name == empty.Name);
                        if (unused != null && priorInstances.Any(i => i.TypeName == empty.Name))
                            throw new InvalidOperationException("Ein leerer Vorlagentyp der bestehenden Familie wird im Projekt verwendet; automatische Migration abgebrochen.");
                    }
                    List<FreeFormElement> forms = new FilteredElementCollector(familyDoc)
                        .OfClass(typeof(FreeFormElement)).Cast<FreeFormElement>().ToList();
                    RejectForeignGeometry(familyDoc, !legacy);
                    if (new FilteredElementCollector(familyDoc).OfClass(typeof(ModelText)).GetElementCount() != 0)
                        throw new InvalidOperationException("Die bestehende BGK-Familie enthält unbearbeiteten Modelltext.");
                    if (legacy)
                    {
                        if (forms.Count == 0 || new FilteredElementCollector(familyDoc)
                            .OfClass(typeof(FamilyInstance)).GetElementCount() != 0)
                            throw new InvalidOperationException("Die unmarkierte Familie enthält keine eindeutige direkte BGK-Textgeometrie.");
                        if (forms.Any(f => manager.GetAssociatedFamilyParameter(VisibleParameter(f)) != null))
                            throw new InvalidOperationException("Die bestehende Familie besitzt fremde Sichtbarkeitsbindungen; Migration abgebrochen.");
                        TaskDialog confirm = new TaskDialog("BGK-3DText-Familie ergänzen")
                        {
                            MainInstruction = "Vorhandene Familie in eine Mehrtypen-Familie umwandeln?",
                            MainContent = $"'{batch.FamilyName}' wurde ohne BIMassist-Marker erstellt. "
                                + $"{forms.Count} bestehende Freiform(en) werden dem bisherigen Typ zugeordnet; "
                                + $"{emptyTypes.Count} ungenutzte(r) leere(r) Vorlagentyp(en) werden entfernt. "
                                + "Vorhandene platzierte Exemplare sollen erhalten bleiben. "
                                + "Bitte nur bestätigen, wenn dies Ihre bisherige BGK-3DText-Familie ist.",
                            CommonButtons = TaskDialogCommonButtons.Cancel
                        };
                        confirm.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Ja, bestehende BGK-Familie umwandeln");
                        if (confirm.Show() != TaskDialogResult.CommandLink1)
                            throw new InvalidOperationException("Umwandlung abgebrochen; vorhandene Familie unverändert.");
                        legacyForms = forms;
                    }
                    else
                    {
                        if (forms.Count != 0)
                            throw new InvalidOperationException("Die markierte BGK-Familie verwendet direkte Freiformen (v2). Eine ausdrückliche separate Migration ist erforderlich; nichts wurde verändert.");
                        VerifyAssociations(familyDoc, manager, managedTypes, batch.FamilyName);
                    }
                }
                else
                {
                    RejectForeignGeometry(familyDoc);
                    if (new FilteredElementCollector(familyDoc).OfClass(typeof(FreeFormElement)).GetElementCount() != 0
                        || new FilteredElementCollector(familyDoc).OfClass(typeof(ModelText)).GetElementCount() != 0
                        || originalTypes.Any(t => !IsEmptyTemplateType(manager, t)))
                        throw new InvalidOperationException("Die Familienvorlage enthält bereits Text/Typdaten oder Freiformgeometrie. Bitte eine leere Vorlage verwenden.");
                }

                IReadOnlyList<string> missing = batch.MissingFrom(managedTypes.Select(t => t.Name));
                HashSet<string> missingNames = new(missing, StringComparer.OrdinalIgnoreCase);
                HashSet<string> obsoleteNames = new(Bgk3DTextBatch.ObsoleteTemplateTypes(
                    (isNew ? originalTypes : emptyTypes).Select(t => t.Name), missing), StringComparer.OrdinalIgnoreCase);
                List<FamilyType> obsoleteTypes = (isNew ? originalTypes : emptyTypes)
                    .Where(t => obsoleteNames.Contains(t.Name)).ToList();
                bool avaDefaultsMissing = avaParameter != null && managedTypes.Any(type => !type.HasValue(avaParameter));
                HashSet<string> avaDefaultedTypes = new(StringComparer.OrdinalIgnoreCase);
                if (!isNew && !legacy)
                {
                    // Nie projektspezifische Sichtbarkeitsänderungen stillschweigend
                    // überschreiben – auch nicht, wenn ein weiterer Typ hinzukommt.
                    VerifyVisibilityValues(manager, managedTypes);
                    VerifyProjectVisibility(project, existing!, managedTypes);
                }
                if (!isNew && !legacy && emptyTypes.Count == 0 && missing.Count == 0 && !avaMissing && !avaDefaultsMissing)
                {
                    FamilySymbol unchanged = FindSymbol(project, existing!, batch.PlacementTypeName);
                    return new Bgk3DTextBuildResult(unchanged, 0, false);
                }

                // Eigene neue Typen anlegen, alte Vorlagentypen entfernen und Modelltext
                // zunächst regenerieren, bevor dessen Solids ausgelesen werden.
                Dictionary<string, ElementId> textIds = new(StringComparer.OrdinalIgnoreCase);
                using (Transaction transaction = new Transaction(familyDoc, "BGK-3DText-Typen und Modelltext vorbereiten"))
                {
                    transaction.Start();
                    if (isNew)
                    {
                        Parameter workPlane = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                        if (workPlane == null || workPlane.IsReadOnly || workPlane.StorageType != StorageType.Integer
                            || !workPlane.Set(1) || workPlane.AsInteger() != 1)
                            throw new InvalidOperationException("Die Vorlage lässt keine arbeitsebenenbasierte Platzierung zu.");
                        Parameter alwaysVertical = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALWAYS_VERTICAL);
                        if (alwaysVertical != null && alwaysVertical.AsInteger() != 0
                            && (alwaysVertical.IsReadOnly || !alwaysVertical.Set(0) || alwaysVertical.AsInteger() != 0))
                            throw new InvalidOperationException("Die Vorlage erzwingt eine vertikale Ausrichtung.");
                    }
                    foreach (Bgk3DTextSpec spec in batch.Types.Where(s => missingNames.Contains(s.TypeName)))
                    {
                        FamilyType type = (isNew ? originalTypes : emptyTypes)
                            .FirstOrDefault(t => string.Equals(t.Name, spec.TypeName, StringComparison.OrdinalIgnoreCase))
                            ?? manager.NewType(spec.TypeName);
                        manager.CurrentType = type;
                        SetFamilyTypeValue(manager, BuiltInParameter.ASSEMBLY_CODE, spec.AssemblyCode);
                        SetFamilyTypeValue(manager, BuiltInParameter.ALL_MODEL_DESCRIPTION, spec.Description);
                        SetFamilyTypeValue(manager, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, spec.TypeComments);
                    }
                    foreach (FamilyType blank in obsoleteTypes)
                    {
                        manager.CurrentType = blank;
                        manager.DeleteCurrentType();
                    }
                    List<FamilyType> allTypes = manager.Types.Cast<FamilyType>().ToList();
                    if (avaMissing) EnsureAvaParameter(manager);
                    avaParameter = FindParameter(manager, AvaName)
                        ?? throw new InvalidOperationException("AVA_Anzahl fehlt nach dem Anlegen.");
                    foreach (FamilyType type in allTypes)
                    {
                        if (!Bgk3DTextBatch.ShouldInitializeAvaDefault(
                                missingNames.Contains(type.Name), type.HasValue(avaParameter))) continue;
                        manager.CurrentType = type;
                        manager.Set(avaParameter, 1.0);
                        avaDefaultedTypes.Add(type.Name);
                    }
                    VerifyAvaDefaults(manager, avaDefaultedTypes);
                    marker ??= manager.AddParameter(MarkerName, GroupTypeId.IdentityData, SpecTypeId.String.Text, false);
                    if (marker.IsInstance || marker.StorageType != StorageType.String)
                        throw new InvalidOperationException("Der BGK-Marker ist kein beschreibbarer Typparameter.");
                    foreach (FamilyType type in allTypes)
                    {
                        manager.CurrentType = type;
                        manager.Set(marker, MarkerValue);
                        if (type.AsString(marker) != MarkerValue)
                            throw new InvalidOperationException("Der BGK-Familienmarker konnte nicht gespeichert werden.");
                    }
                    foreach (FamilyType type in allTypes)
                    {
                        string name = Bgk3DTextBatch.VisibilityParameterName(type.Name);
                        FamilyParameter? flag = FindParameter(manager, name);
                        flag ??= manager.AddParameter(name, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, false);
                        if (flag.IsInstance || flag.StorageType != StorageType.Integer
                            || !flag.Definition.GetDataType().Equals(SpecTypeId.Boolean.YesNo))
                            throw new InvalidOperationException($"Sichtbarkeitsparameter '{name}' ist kein Typ-Ja/Nein-Parameter.");
                    }
                    if (missing.Count > 0)
                    {
                        ModelTextType textType = new FilteredElementCollector(familyDoc)
                            .OfClass(typeof(ModelTextType)).Cast<ModelTextType>().FirstOrDefault()
                            ?? throw new InvalidOperationException("In der Familienvorlage ist kein Modelltext-Typ vorhanden.");
                        Parameter size = textType.get_Parameter(BuiltInParameter.MODEL_TEXT_SIZE);
                        double height = UnitUtils.ConvertToInternalUnits(heightMm, UnitTypeId.Millimeters);
                        if (size == null || size.IsReadOnly || size.StorageType != StorageType.Double
                            || !size.Set(height) || Math.Abs(size.AsDouble() - height) > 1e-8)
                            throw new InvalidOperationException("Die Texthöhe konnte nicht eingestellt werden.");
                        SketchPlane plane = SketchPlane.Create(familyDoc,
                            Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                        foreach (Bgk3DTextSpec spec in batch.Types.Where(s => missingNames.Contains(s.TypeName)))
                        {
                            manager.CurrentType = allTypes.First(t => string.Equals(t.Name, spec.TypeName, StringComparison.OrdinalIgnoreCase));
                            ModelText text = familyDoc.FamilyCreate.NewModelText(spec.Text, textType, plane,
                                XYZ.Zero, HorizontalAlign.Left,
                                UnitUtils.ConvertToInternalUnits(depthMm, UnitTypeId.Millimeters));
                            if (text == null || text.Text != spec.Text)
                                throw new InvalidOperationException("Der erzeugte 3D-Text entspricht nicht der BGK-Auswahl.");
                            textIds.Add(spec.TypeName, text.Id);
                        }
                    }
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Die Familientypen konnten nicht vorbereitet werden.");
                }

                Dictionary<string, List<Solid>> geometry = new(StringComparer.OrdinalIgnoreCase);
                if (legacy)
                {
                    List<Solid> oldSolids = new();
                    foreach (FreeFormElement form in legacyForms)
                        oldSolids.AddRange(ReadSolids(form, managedTypes[0].Name));
                    if (oldSolids.Count == 0)
                        throw new InvalidOperationException("Die alte Freiformgeometrie konnte nicht vollständig kopiert werden.");
                    geometry.Add(managedTypes[0].Name, oldSolids);
                }
                foreach ((string typeName, ElementId textId) in textIds)
                {
                    Element text = familyDoc.GetElement(textId)
                        ?? throw new InvalidOperationException("Der generierte Modelltext fehlt nach der Regeneration.");
                    geometry.Add(typeName, ReadSolids(text, typeName));
                }

                if (geometry.Count > 0)
                {
                    tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIMassist", "Temp", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                }
                string? childTemplate = geometry.Count > 0 ? ResolveTemplate(app) : null;
                Dictionary<string, FamilySymbol> childSymbols = new(StringComparer.OrdinalIgnoreCase);
                // Dateibasierter LoadFamily benötigt eine eigene Transaktion im äußeren
                // Familiendokument; zwischen den Kindfamilien ist sie jeweils geschlossen.
                foreach ((string typeName, List<Solid> solids) in geometry)
                    childSymbols.Add(typeName, CreateChild(app, familyDoc, childTemplate!,
                        tempDir!, batch.FamilyName, typeName, solids));

                Dictionary<long, (long FlagId, string FlagName)> expectedBindings = new();
                Dictionary<long, FamilyParameter> knownFlags = new();
                Dictionary<long, List<string>> bindingTrace = new();
                using (Transaction transaction = new Transaction(familyDoc, "BGK-3DText-Sichtbarkeit je Familientyp"))
                {
                    transaction.Start();
                    List<FamilyType> types = manager.Types.Cast<FamilyType>().ToList();
                    List<Level> referenceLevels = new FilteredElementCollector(familyDoc)
                        .OfClass(typeof(Level)).Cast<Level>()
                        .Where(level => Math.Abs(level.Elevation) < 1e-8).ToList();
                    if (childSymbols.Count > 0 && referenceLevels.Count != 1)
                        throw new InvalidOperationException("Die äußere Familienvorlage benötigt genau eine Bezugsebene auf Höhe 0 für die verschachtelten Texte.");
                    foreach ((string typeName, FamilySymbol childSymbol) in childSymbols)
                    {
                        FamilyParameter flag = FindParameter(manager, Bgk3DTextBatch.VisibilityParameterName(typeName))!;
                        if (!childSymbol.IsActive) childSymbol.Activate();
                        familyDoc.Regenerate();
                        FamilyInstance instance = familyDoc.FamilyCreate.NewFamilyInstance(
                            XYZ.Zero, childSymbol, referenceLevels[0], StructuralType.NonStructural)
                            ?? throw new InvalidOperationException($"Verschachtelte Textfamilie für '{typeName}' konnte nicht platziert werden.");
                        SetGeometryOwner(instance, typeName);
                        AssociateVisible(manager, instance, flag);
                        expectedBindings.Add(instance.Id.Value, (flag.Id.Value, flag.Definition.Name));
                        knownFlags.Add(instance.Id.Value, flag);
                        bindingTrace.Add(instance.Id.Value, new List<string>
                        {
                            ReadBinding(familyDoc, manager, instance.Id.Value, flag, "direkt").Evidence
                        });
                    }
                    foreach (ElementId textId in textIds.Values)
                        if (familyDoc.Delete(textId).Count != 1)
                            throw new InvalidOperationException("Beim Löschen des temporären Modelltexts würden weitere Familienelemente entfernt.");
                    foreach (FreeFormElement form in legacyForms)
                        if (familyDoc.Delete(form.Id).Count != 1)
                            throw new InvalidOperationException("Beim Migrieren alter Freiformen würden weitere Familienelemente entfernt.");
                    foreach ((long instanceId, (_, string flagName)) in expectedBindings)
                        bindingTrace[instanceId].Add(ReadBinding(familyDoc, manager,
                            instanceId, knownFlags[instanceId], "nach Textlöschung").Evidence);
                    // Nur bei neuen Typen die Matrix erweitern. Ein reines
                    // AVA_Anzahl-Upgrade darf alte/formelgesteuerte Flags nicht setzen.
                    if (Bgk3DTextBatch.ShouldRebuildVisibility(missing.Count))
                    foreach (FamilyType type in types)
                    {
                        manager.CurrentType = type;
                        foreach (FamilyType owner in types)
                        {
                            FamilyParameter flag = FindParameter(manager,
                                Bgk3DTextBatch.VisibilityParameterName(owner.Name))!;
                            int expected = batch.IsVisibleFor(owner.Name, type.Name) ? 1 : 0;
                            manager.Set(flag, expected);
                            if (type.AsInteger(flag) != expected)
                                throw new InvalidOperationException($"Sichtbarkeit für '{type.Name}' wurde nicht gespeichert.");
                        }
                    }
                    foreach ((long instanceId, (_, string flagName)) in expectedBindings)
                        bindingTrace[instanceId].Add(ReadBinding(familyDoc, manager,
                            instanceId, knownFlags[instanceId], "nach Typwerten").Evidence);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Die BGK-Textgeometrie wurde nicht fertiggestellt.");
                }
                if (textIds.Values.Any(id => familyDoc.GetElement(id) != null)
                    || familyDoc.OwnerFamily.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased)
                    throw new InvalidOperationException("Modelltext oder Platzierungsart konnten nicht verifiziert werden.");
                if (!ValidateAvaParameter(FindParameter(manager, AvaName), legacySharedAva))
                    throw new InvalidOperationException("AVA_Anzahl fehlt nach der Familientransaktion.");
                VerifyAvaDefaults(manager, avaDefaultedTypes);
                // Die Vorwärtsabfrage liefert in Revit 2026 für korrekt funktionierende
                // Testfamilien null. Exakte Rückwärtszuordnung und Typwertmatrix prüfen,
                // bevor die Familie in ein Projekt geladen wird.
                foreach ((long instanceId, (long flagId, string flagName)) in expectedBindings)
                {
                    var check = ReadBinding(familyDoc, manager, instanceId, knownFlags[instanceId], "nach Commit");
                    bindingTrace[instanceId].Add(check.Evidence);
                    if ((check.ForwardId.HasValue && check.ForwardId != flagId) || !check.ReverseMatch)
                    {
                        string diagnostic = SaveBindingTrace(bindingTrace);
                        string familyArtifact = string.Empty;
                        // Nur eine NEUE Testfamilie als Datei sichern; niemals die
                        // bestehende Projektfamilie trotz unklarer Bindung zurückladen.
                        if (isNew && check.ReverseMatch)
                        {
                            try
                            {
                                string directory = Path.Combine(Environment.GetFolderPath(
                                    Environment.SpecialFolder.LocalApplicationData), "BIMassist", "Diagnostics");
                                Directory.CreateDirectory(directory);
                                string path = Path.Combine(directory,
                                    Bgk3DTextBatch.DiagnosticFamilyFileName(DateTime.UtcNow, Guid.NewGuid()));
                                familyDoc.SaveAs(path);
                                familyArtifact = File.Exists(path)
                                    ? $" Testfamilie zur manuellen Prüfung: {path}."
                                    : " Die Testfamilie wurde nach SaveAs nicht auf dem Datenträger gefunden.";
                            }
                            catch (Exception error)
                            {
                                familyArtifact = $" Testfamilie konnte nicht gespeichert werden: {error.GetType().Name}: {error.Message}";
                            }
                        }
                        throw new InvalidOperationException($"Verschachtelte Instanz {instanceId} ist nach dem Commit nicht nachweislich an '{flagName}' gebunden. "
                            + $"Vorwärts: {(check.ForwardId?.ToString() ?? "keine")}, rückwärts: {(check.ReverseMatch ? "ja" : "nein")}. "
                            + $"Diagnose: {diagnostic}.{familyArtifact}");
                    }
                }
                VerifyAssociations(familyDoc, manager, manager.Types.Cast<FamilyType>().ToList(), batch.FamilyName);

                // Neue Familien vor dem Projekt-Load aus einer neu geöffneten Datei
                // prüfen; ein Commit allein reicht dafür nicht.
                if (isNew)
                {
                    tempDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIMassist", "Temp", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    string familyPath = Path.Combine(tempDir, batch.FamilyName + ".rfa");
                    familyDoc.SaveAs(familyPath);
                    if (!File.Exists(familyPath))
                        throw new InvalidOperationException("Die temporäre Familien-Datei wurde nicht erstellt.");
                    familyDoc.Close(false);
                    familyDoc = app.Application.OpenDocumentFile(familyPath);
                    manager = familyDoc.FamilyManager;
                    List<FamilyType> reopenedTypes = manager.Types.Cast<FamilyType>().ToList();
                    VerifyAssociations(familyDoc, manager, reopenedTypes, batch.FamilyName);
                    VerifyVisibilityValues(manager, reopenedTypes);
                    if (!ValidateAvaParameter(FindParameter(manager, AvaName), legacySharedAva))
                        throw new InvalidOperationException("AVA_Anzahl fehlt nach erneutem Öffnen der BGK-Familie.");
                    VerifyAvaDefaults(manager, avaDefaultedTypes);
                }
                else
                {
                    VerifyVisibilityValues(manager, manager.Types.Cast<FamilyType>().ToList());
                    if (!ValidateAvaParameter(FindParameter(manager, AvaName), legacySharedAva))
                        throw new InvalidOperationException("AVA_Anzahl fehlt vor dem Projekt-Load.");
                    VerifyAvaDefaults(manager, avaDefaultedTypes);
                }

                FamilySymbol? placedSymbol = null;
                string reloadPhase = "Familie ins Projekt laden";
                List<string> loadObservations = new();
                using (TransactionGroup group = new TransactionGroup(project, "BGK-3DText-Familie erstellen/ergänzen"))
                {
                    group.Start();
                    try
                    {
                        List<FamilyType> preparedTypes = manager.Types.Cast<FamilyType>().ToList();
                        CaptureReloadObservation(loadObservations, "Familie vor Load",
                            () => DescribeFamilyVisibility(manager, preparedTypes));
                        if (existing != null)
                            CaptureReloadObservation(loadObservations, "Projekt vor Load",
                                () => DescribeProjectVisibility(project, existing, managedTypes));
                        ReplaceSameFamilyLoad? replaceOptions = isNew ? null : new ReplaceSameFamilyLoad();
                        Family loaded = familyDoc.LoadFamily(project, isNew
                            ? new RefuseExistingLoad() : replaceOptions!)
                            ?? throw new InvalidOperationException("Revit konnte die BGK-Familie nicht ins Projekt laden.");
                        loadObservations.Add("Load-Callbacks: " + (replaceOptions == null
                            ? "neue Familie" : string.Join("; ", replaceOptions.Observations)));
                        reloadPhase = "Geladene Familienidentität prüfen";
                        long loadedId = loaded.Id.Value;
                        string loadedName = loaded.Name;
                        long[] matchingFamilyIds = new FilteredElementCollector(project)
                            .OfClass(typeof(Family)).Cast<Family>()
                            .Where(f => string.Equals(f.Name, batch.FamilyName, StringComparison.OrdinalIgnoreCase))
                            .Select(f => f.Id.Value).ToArray();
                        bool priorFamilyStillPresent = existingFamilyId.HasValue
                            && project.GetElement(new ElementId(existingFamilyId.Value)) != null;
                        if (!Bgk3DTextBatch.IsSafeFamilyReload(batch.FamilyName, loadedName,
                            loadedId, existingFamilyId, originalIds, matchingFamilyIds, priorFamilyStillPresent))
                            throw new InvalidOperationException($"Unsichere Familienidentität nach dem Laden: "
                                + $"erwartet '{batch.FamilyName}' (vorher ID {existingFamilyId?.ToString() ?? "keine"}), "
                                + $"geladen '{loadedName}' (ID {loadedId}), "
                                + $"gleichnamige IDs [{string.Join(", ", matchingFamilyIds)}], "
                                + $"vorherige ID noch vorhanden: {priorFamilyStillPresent}, "
                                + $"geladene ID zuvor im Projekt: {originalIds.Contains(loadedId)}. "
                                + "Keine fremde Familie wird ersetzt.");
                        CaptureReloadObservation(loadObservations, "Projekt nach Load",
                            () => DescribeProjectVisibility(project, loaded, preparedTypes));

                        // Die geladene Projektfamilie als unabhängige Kopie erneut öffnen
                        // und Bindungen/Typzuordnung dort prüfen, bevor die Gruppe committed.
                        reloadPhase = "Geladene Familie erneut öffnen";
                        familyDoc.Close(false);
                        familyDoc = project.EditFamily(loaded);
                        reloadPhase = "Familienstruktur nach Reload prüfen";
                        manager = familyDoc.FamilyManager;
                        List<FamilyType> loadedTypes = manager.Types.Cast<FamilyType>().ToList();
                        CaptureReloadObservation(loadObservations, "Familie nach Load",
                            () => DescribeFamilyVisibility(manager, loadedTypes));
                        VerifyAssociations(familyDoc, manager, loadedTypes, batch.FamilyName);
                        VerifyVisibilityValues(manager, loadedTypes);
                        if (!ValidateAvaParameter(FindParameter(manager, AvaName), legacySharedAva))
                            throw new InvalidOperationException("AVA_Anzahl fehlt in der erneut geöffneten BGK-Familie.");
                        VerifyAvaDefaults(manager, avaDefaultedTypes);
                        if (legacySharedAva)
                            VerifyProjectAvaDefinition(project, priorInstances.Count > 0);
                        reloadPhase = "Projekttypen und vorhandene Exemplare prüfen";
                        using (Transaction transaction = new Transaction(project, "BGK-3DText-Typen prüfen"))
                        {
                            transaction.Start();
                            IEnumerable<FamilyType> projectObsolete = isNew ? Array.Empty<FamilyType>() : obsoleteTypes;
                            foreach (FamilyType obsolete in projectObsolete)
                            {
                                FamilySymbol? symbol = new FilteredElementCollector(project)
                                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                                    .FirstOrDefault(s => s.Family.Id == loaded.Id && s.Name == obsolete.Name);
                                if (symbol == null) continue;
                                if (new FilteredElementCollector(project).OfClass(typeof(FamilyInstance))
                                    .Cast<FamilyInstance>().Any(i => i.Symbol?.Id == symbol.Id))
                                    throw new InvalidOperationException("Ein alter Vorlagentyp ist noch im Projekt platziert. Kein Typ wurde gelöscht.");
                                ICollection<ElementId> deleted = project.Delete(symbol.Id);
                                if (deleted.Count != 1 || !deleted.Contains(symbol.Id))
                                    throw new InvalidOperationException("Beim Entfernen eines Vorlagentyps wären weitere Projektelemente gelöscht worden. Änderung zurückgenommen.");
                            }
                            string[] expectedNames = manager.Types.Cast<FamilyType>()
                                .Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                            string[] actualNames = new FilteredElementCollector(project)
                                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                                .Where(s => s.Family.Id == loaded.Id).Select(s => s.Name)
                                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
                            if (!actualNames.SequenceEqual(expectedNames))
                                throw new InvalidOperationException("Revit hat nicht alle Familientypen wie erwartet neu geladen. Änderung zurückgenommen.");
                            foreach (FamilyType familyType in manager.Types.Cast<FamilyType>())
                            {
                                FamilySymbol symbol = FindSymbol(project, loaded, familyType.Name);
                                foreach (FamilyType owner in manager.Types.Cast<FamilyType>())
                                {
                                    Parameter flag = FindUniqueParameter(symbol, Bgk3DTextBatch.VisibilityParameterName(owner.Name))
                                        ?? throw new InvalidOperationException("Der Sichtbarkeitsparameter wurde nicht ins Projekt geladen.");
                                    int expected = batch.IsVisibleFor(owner.Name, familyType.Name) ? 1 : 0;
                                    if (flag.StorageType != StorageType.Integer
                                        || (flag.AsInteger() != expected && (flag.IsReadOnly || !flag.Set(expected)))
                                        || flag.AsInteger() != expected)
                                        throw new InvalidOperationException($"Sichtbarkeit von Typ '{familyType.Name}' ist im Projekt nicht korrekt.");
                                }
                                Parameter tag = FindUniqueParameter(symbol, MarkerName)
                                    ?? throw new InvalidOperationException("Der BGK-Familienmarker wurde nicht geladen.");
                                if (tag.StorageType != StorageType.String
                                    || (tag.AsString() != MarkerValue && (tag.IsReadOnly || !tag.Set(MarkerValue)))
                                    || tag.AsString() != MarkerValue)
                                    throw new InvalidOperationException("Der Familienmarker konnte nicht geprüft werden.");
                            }
                            foreach (Bgk3DTextSpec spec in batch.Types.Where(s => missingNames.Contains(s.TypeName)))
                            {
                                FamilySymbol symbol = FindSymbol(project, loaded, spec.TypeName);
                                VerifySymbolValue(symbol, BuiltInParameter.ASSEMBLY_CODE, spec.AssemblyCode);
                                VerifySymbolValue(symbol, BuiltInParameter.ALL_MODEL_DESCRIPTION, spec.Description);
                                VerifySymbolValue(symbol, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, spec.TypeComments);
                            }
                            placedSymbol = FindSymbol(project, loaded, batch.PlacementTypeName);
                            if (!placedSymbol.IsActive) placedSymbol.Activate();
                            project.Regenerate();
                            foreach (ExistingInstanceSnapshot before in priorInstances)
                            {
                                if (project.GetElement(before.Id) is not FamilyInstance prior
                                    || prior.Symbol?.Family.Id != loaded.Id
                                    || (legacySharedAva ? prior.get_Parameter(AvaGuid)
                                        : FindUniqueParameter(prior, AvaName)) is not Parameter actualAva
                                    || actualAva.StorageType != StorageType.Double
                                    || !actualAva.Definition.GetDataType().Equals(SpecTypeId.Number)
                                    || !SameInstance(before, SnapshotInstance(prior), avaMissing))
                                    throw new InvalidOperationException("Position, Host, Typ oder Parameter einer vorhandenen BGK-Instanz wurden durch das Neuladen verändert; Änderung zurückgenommen.");
                            }
                            if (transaction.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Die Projekttransaktion wurde nicht abgeschlossen.");
                        }
                        reloadPhase = "Projektwerte nach Commit prüfen";
                        VerifyProjectVisibility(project, loaded, loadedTypes);
                        VerifyAssociations(familyDoc, manager, loadedTypes, batch.FamilyName);
                        if (group.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Die BGK-Familienaktualisierung wurde nicht abgeschlossen.");
                    }
                    catch (Exception error)
                    {
                        string diagnostic = SaveReloadFailureTrace(reloadPhase, error, loadObservations);
                        if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                        throw new InvalidOperationException($"BGK-Aktualisierung scheiterte bei '{reloadPhase}': {error.Message}. Diagnose: {diagnostic}", error);
                    }
                }
                return new Bgk3DTextBuildResult(placedSymbol!, missing.Count, isNew);
            }
            finally
            {
                if (familyDoc != null && familyDoc.IsValidObject) familyDoc.Close(false);
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException) { /* Revit kann die Datei kurz sperren. */ }
                    catch (UnauthorizedAccessException) { /* Später bereinigen. */ }
                }
            }
        }

        private static FamilyParameter? FindParameter(FamilyManager manager, string name) =>
            manager.GetParameters().FirstOrDefault(p => string.Equals(p.Definition.Name, name, StringComparison.OrdinalIgnoreCase));

        private static string SaveReloadFailureTrace(string phase, Exception error, IReadOnlyList<string> observations)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIMassist", "Diagnostics");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"BGK-Reload-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
                File.WriteAllText(path, $"Phase: {phase}{Environment.NewLine}"
                    + string.Join(Environment.NewLine, observations) + Environment.NewLine + error);
                return path;
            }
            catch (Exception failure)
            {
                // Ein Diagnosefehler darf den anschließenden Projekt-Rollback nie verhindern.
                return $"nicht gespeichert ({failure.GetType().Name})";
            }
        }

        private static void CaptureReloadObservation(List<string> observations, string stage, Func<string> capture)
        {
            try { observations.Add($"{stage}: {capture()}"); }
            catch (Exception failure)
            {
                observations.Add($"{stage}: Diagnosefehler {failure.GetType().Name}");
            }
        }

        private static string DescribeFamilyVisibility(FamilyManager manager, IReadOnlyList<FamilyType> types)
        {
            Dictionary<string, FamilyType> byName = types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            return DescribeVisibilityMatrix(byName.Keys.ToArray(), (active, owner) =>
            {
                FamilyParameter? flag = FindParameter(manager, Bgk3DTextBatch.VisibilityParameterName(owner));
                return flag?.StorageType == StorageType.Integer ? byName[active].AsInteger(flag) : null;
            });
        }

        private static string DescribeProjectVisibility(Document project, Family family, IReadOnlyList<FamilyType> types)
        {
            Dictionary<string, FamilySymbol> symbols = new FilteredElementCollector(project)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Family.Id.Value == family.Id.Value)
                .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            return DescribeVisibilityMatrix(types.Select(t => t.Name).ToArray(), (active, owner) =>
            {
                if (!symbols.TryGetValue(active, out FamilySymbol? symbol)) return null;
                Parameter? flag = FindUniqueParameter(symbol, Bgk3DTextBatch.VisibilityParameterName(owner));
                return flag?.StorageType == StorageType.Integer ? flag.AsInteger() : null;
            });
        }

        private static string DescribeVisibilityMatrix(IReadOnlyList<string> names, Func<string, string, int?> read)
        {
            List<string> differences = new();
            foreach (string active in names)
                foreach (string owner in names)
                {
                    int expected = string.Equals(active, owner, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    try
                    {
                        int? actual = read(active, owner);
                        if (actual != expected && differences.Count < 50)
                            differences.Add($"Typ '{active}' / Flag '{owner}': erwartet {expected}, gelesen {actual?.ToString() ?? "fehlt"}");
                    }
                    catch (Exception failure)
                    {
                        if (differences.Count < 50)
                            differences.Add($"Typ '{active}' / Flag '{owner}': Abfragefehler {failure.GetType().Name}");
                    }
                }
            return $"{names.Count} Typen, {names.Count * names.Count} Zellen; "
                + (differences.Count == 0 ? "keine Abweichung" : string.Join("; ", differences));
        }

        private static bool ValidateAvaParameter(FamilyParameter? parameter, bool legacySharedAva)
        {
            if (parameter == null) return false;
            if (parameter.IsShared != legacySharedAva || (legacySharedAva && parameter.GUID != AvaGuid)
                || !parameter.IsInstance
                || parameter.StorageType != StorageType.Double
                || !parameter.Definition.GetDataType().Equals(SpecTypeId.Number))
                throw new InvalidOperationException($"Der vorhandene Parameter '{AvaName}' hat eine andere Herkunft, GUID, Einheit oder Typ-/Exemplarzuordnung. Er wurde nicht ersetzt.");
            return true;
        }

        private static void VerifyAvaDefaults(FamilyManager manager, HashSet<string> typeNames)
        {
            if (typeNames.Count == 0) return;
            FamilyParameter parameter = FindParameter(manager, AvaName)
                ?? throw new InvalidOperationException("AVA_Anzahl fehlt beim Prüfen der Standardwerte.");
            foreach (FamilyType type in manager.Types.Cast<FamilyType>()
                .Where(type => typeNames.Contains(type.Name)))
                if (!type.HasValue(parameter) || type.AsDouble(parameter) is not double actual
                    || Math.Abs(actual - 1.0) > 1e-8)
                    throw new InvalidOperationException($"Der AVA_Anzahl-Standardwert für '{type.Name}' ist nicht 1.");
        }

        private static void VerifyProjectAvaDefinition(Document project, bool required = false)
        {
            List<ParameterElement> candidates = new FilteredElementCollector(project)
                .OfClass(typeof(ParameterElement)).Cast<ParameterElement>()
                .Where(p => string.Equals(p.Name, AvaName, StringComparison.OrdinalIgnoreCase)
                    || p is SharedParameterElement shared && shared.GuidValue == AvaGuid).ToList();
            if (candidates.Count == 0 && !required) return;
            if (candidates.Count != 1 || candidates[0] is not SharedParameterElement sharedParameter
                || !string.Equals(sharedParameter.Name, AvaName, StringComparison.OrdinalIgnoreCase)
                || sharedParameter.GuidValue != AvaGuid
                || !sharedParameter.GetDefinition().GetDataType().Equals(SpecTypeId.Number))
                throw new InvalidOperationException($"Im Projekt ist '{AvaName}' nicht mit der BGK-GUID und dem Datentyp Zahl eindeutig vorhanden. Keine fremden Parameter werden überschrieben.");
        }

        private static void EnsureAvaParameter(FamilyManager manager)
        {
            FamilyParameter added = manager.AddParameter(AvaName, GroupTypeId.Data, SpecTypeId.Number, true);
            if (!ValidateAvaParameter(added, false))
                throw new InvalidOperationException($"Exemplar-Familienparameter '{AvaName}' konnte nicht in der BGK-Familie gespeichert werden.");
        }

        private static string ResolveTemplate(UIApplication app)
        {
            string? configured = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT 2026", "Family Templates");
            return Bgk3DTextBatch.FindGenericModelTemplate(configured,
                app.Application.FamilyTemplatePath, root, File.Exists)
                ?? throw new FileNotFoundException(string.IsNullOrWhiteSpace(configured)
                    ? "Keine Vorlage für Allgemeine Modelle gefunden. Bitte in den Einstellungen eine gültige Vorlage auswählen."
                    : "Die eingestellte Familienvorlage fehlt. Bitte in den Einstellungen eine gültige Vorlage auswählen.",
                    configured);
        }

        private static ExistingInstanceSnapshot SnapshotInstance(FamilyInstance instance)
        {
            List<string> values = instance.Parameters.Cast<Parameter>()
                .Where(p => (p.Id.Value > 0 || !p.IsReadOnly) && p.StorageType != StorageType.None)
                .Select(p => $"{p.Id.Value}|{p.Definition.Name}|{p.StorageType}|{ParameterValue(p)}")
                .OrderBy(value => value, StringComparer.Ordinal).ToList();
            return new ExistingInstanceSnapshot(instance.Id, instance.Symbol.Name,
                instance.GetTransform(), instance.Host?.Id.Value ?? ElementId.InvalidElementId.Value,
                instance.LevelId.Value, instance.Pinned, values);
        }

        private static string ParameterValue(Parameter parameter) => parameter.StorageType switch
        {
            StorageType.String => parameter.AsString() ?? "<null>",
            StorageType.Integer => parameter.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture),
            StorageType.Double => parameter.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            StorageType.ElementId => parameter.AsElementId().Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Unbekannter Instanzparametertyp.")
        };

        private static bool SameInstance(ExistingInstanceSnapshot before, ExistingInstanceSnapshot after, bool avaAdded) =>
            before.TypeName == after.TypeName && before.HostId == after.HostId
            && before.LevelId == after.LevelId && before.Pinned == after.Pinned
            && before.Transform.Origin.DistanceTo(after.Transform.Origin) < 1e-8
            && before.Transform.BasisX.DistanceTo(after.Transform.BasisX) < 1e-8
            && before.Transform.BasisY.DistanceTo(after.Transform.BasisY) < 1e-8
            && before.Transform.BasisZ.DistanceTo(after.Transform.BasisZ) < 1e-8
            && Bgk3DTextBatch.InstanceValuesPreserved(before.Values, after.Values, AvaName, avaAdded);

        private static List<Solid> ReadSolids(Element element, string typeName)
        {
            IList<GeometryObject> objects = FamilyGeometryTools.GetAllGeometryObjects(
                element, new Options { DetailLevel = ViewDetailLevel.Fine });
            List<Solid> solids = objects.OfType<Solid>().Where(s => s.Volume > 0)
                .Select(s => SolidUtils.CreateTransformed(s, Transform.Identity)).ToList();
            if (solids.Count == 0 || objects.Count != solids.Count)
                throw new InvalidOperationException($"Typ '{typeName}' lieferte keine vollständige Volumengeometrie; nichts wurde geladen.");
            return solids;
        }

        private static FamilySymbol CreateChild(UIApplication app, Document outer, string template,
            string tempDir, string outerName, string typeName, IReadOnlyList<Solid> solids)
        {
            string name = Bgk3DTextBatch.ChildFamilyName(outerName, typeName);
            if (new FilteredElementCollector(outer).OfClass(typeof(Family)).Cast<Family>()
                .Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Der Kindfamilienname '{name}' ist bereits belegt; nichts wird ersetzt.");
            if (!File.Exists(template))
                throw new FileNotFoundException("Die Vorlage für die verschachtelte Allgemeine Familie fehlt.", template);
            Document child = app.Application.NewFamilyDocument(template);
            try
            {
                if (child.OwnerFamily?.FamilyCategory?.Id.Value != (long)BuiltInCategory.OST_GenericModel)
                    throw new InvalidOperationException("Die Kindvorlage ist keine Allgemeine Familie.");
                Parameter? shared = child.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED);
                if (shared == null || shared.AsInteger() != 0)
                    throw new InvalidOperationException("Die Kindfamilie muss nicht gemeinsam genutzt (non-shared) sein.");
                RejectForeignGeometry(child);
                if (new FilteredElementCollector(child).OfClass(typeof(FreeFormElement)).GetElementCount() != 0
                    || new FilteredElementCollector(child).OfClass(typeof(ModelText)).GetElementCount() != 0)
                    throw new InvalidOperationException("Die Kindvorlage enthält bereits Modellgeometrie.");
                using (Transaction transaction = new Transaction(child, "BGK-Textgeometrie im Kind erzeugen"))
                {
                    transaction.Start();
                    FamilyManager childManager = child.FamilyManager;
                    List<FamilyType> childTypes = childManager.Types.Cast<FamilyType>().ToList();
                    if (childTypes.Count > 1)
                        throw new InvalidOperationException("Die Kindvorlage enthält mehrere Familientypen; eine eindeutige Textfamilie ist nicht möglich.");
                    childManager.CurrentType = childTypes.Count == 0
                        ? childManager.NewType("BGK Text") : childTypes[0];
                    Parameter? workPlane = child.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                    if (workPlane == null || workPlane.StorageType != StorageType.Integer)
                        throw new InvalidOperationException("Die Kindvorlage besitzt keine einstellbare Platzierungsart.");
                    if (workPlane.AsInteger() != 0 && (workPlane.IsReadOnly || !workPlane.Set(0)))
                        throw new InvalidOperationException("Die Kindfamilie kann nicht auf ebenenbasierte Platzierung umgestellt werden.");
                    foreach (Solid solid in solids)
                        if (FreeFormElement.Create(child, solid) == null)
                            throw new InvalidOperationException($"Freiform für '{typeName}' konnte nicht erzeugt werden.");
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Die Kindfamilie konnte nicht fertiggestellt werden.");
                }
                if (new FilteredElementCollector(child).OfClass(typeof(FreeFormElement)).GetElementCount() != solids.Count)
                    throw new InvalidOperationException("Die Kindfamilie enthält nicht alle Text-Solids.");
                if (child.FamilyManager.Types.Size != 1)
                    throw new InvalidOperationException("Die Kindfamilie besitzt nicht genau einen Typ.");
                FamilyPlacementType placement = child.OwnerFamily.FamilyPlacementType;
                if (placement != FamilyPlacementType.OneLevelBased)
                    throw new InvalidOperationException($"Kindvorlage hat Platzierungsart {placement}; für die Platzierung auf der Bezugsebene wird OneLevelBased benötigt.");
                string path = Path.Combine(tempDir, name + ".rfa");
                child.SaveAs(path);
                if (!File.Exists(path))
                    throw new InvalidOperationException("Kindfamilien-Datei wurde nicht gespeichert.");
                child.Close(false);
                Family? loaded;
                using (Transaction load = new Transaction(outer, "BGK-Kindfamilie laden"))
                {
                    if (load.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException($"Transaktion zum Laden der Kindfamilie '{name}' wurde nicht gestartet.");
                    try
                    {
                        bool succeeded = outer.LoadFamily(path, new RefuseExistingLoad(), out loaded);
                        string? failure = Bgk3DTextBatch.ChildLoadFailure(name, loaded?.Name, succeeded);
                        if (failure != null)
                            throw new InvalidOperationException($"Kindfamilie '{name}': {failure}");
                        if (load.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException($"Ladetransaktion für Kindfamilie '{name}' wurde nicht abgeschlossen.");
                    }
                    catch
                    {
                        if (load.GetStatus() == TransactionStatus.Started) load.RollBack();
                        throw;
                    }
                }
                if (loaded == null || !loaded.IsValidObject)
                    throw new InvalidOperationException($"Kindfamilie '{name}' ist nach dem Laden nicht mehr gültig.");
                FamilySymbol symbol = new FilteredElementCollector(outer).OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>().SingleOrDefault(s => s.Family.Id == loaded.Id)
                    ?? throw new InvalidOperationException($"Kindfamilie '{name}' hat nicht genau einen Typ.");
                if (loaded.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() != 0
                    || loaded.FamilyPlacementType != placement)
                    throw new InvalidOperationException($"Kindfamilie '{name}' hat nach dem Laden andere Eigenschaften.");
                return symbol;
            }
            finally
            {
                if (child.IsValidObject) child.Close(false);
            }
        }

        private static void RejectForeignGeometry(Document familyDoc, bool allowNested = false)
        {
            if (new FilteredElementCollector(familyDoc).WhereElementIsNotElementType().Any(element =>
                    element is GenericForm and not FreeFormElement
                    || (!allowNested && element is FamilyInstance)
                    || element is ImportInstance or DirectShape or ModelCurve))
                throw new InvalidOperationException("Die Familie enthält zusätzliche Modellgeometrie, deren Sichtbarkeit nicht typweise gesteuert werden kann. Sie wurde nicht verändert.");
        }

        private static bool IsLegacyType(FamilyManager manager, FamilyType type, string familyName) =>
            Bgk3DTextBatch.IsLegacyType(familyName, type.Name,
                FamilyString(manager, type, BuiltInParameter.ASSEMBLY_CODE),
                FamilyString(manager, type, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS),
                FamilyString(manager, type, BuiltInParameter.ALL_MODEL_DESCRIPTION));

        private static bool IsEmptyTemplateType(FamilyManager manager, FamilyType type) =>
            string.IsNullOrWhiteSpace(FamilyString(manager, type, BuiltInParameter.ASSEMBLY_CODE))
            && string.IsNullOrWhiteSpace(FamilyString(manager, type, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS))
            && string.IsNullOrWhiteSpace(FamilyString(manager, type, BuiltInParameter.ALL_MODEL_DESCRIPTION));

        private static string? FamilyString(FamilyManager manager, FamilyType type, BuiltInParameter id)
        {
            FamilyParameter? parameter = manager.get_Parameter(id);
            return parameter == null ? null : type.AsString(parameter);
        }

        private static void VerifyVisibilityValues(FamilyManager manager, IReadOnlyList<FamilyType> types)
        {
            foreach (FamilyType active in types)
                foreach (FamilyType owner in types)
                {
                    string flagName = Bgk3DTextBatch.VisibilityParameterName(owner.Name);
                    FamilyParameter? flag = FindParameter(manager, flagName);
                    int expected = string.Equals(active.Name, owner.Name, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                    int? actual = flag?.StorageType == StorageType.Integer ? active.AsInteger(flag) : null;
                    if (actual != expected)
                        throw new InvalidOperationException($"Sichtbarkeitsmatrix weicht ab: Familientyp '{active.Name}', "
                            + $"Parameter '{flagName}', erwartet {expected}, gelesen {(actual?.ToString() ?? "fehlt")}. "
                            + "Keine vorhandenen Werte werden ungeprüft überschrieben.");
                }
        }

        private static void VerifyProjectVisibility(Document project, Family family, IReadOnlyList<FamilyType> types)
        {
            Dictionary<string, FamilySymbol> symbols = types.ToDictionary(t => t.Name,
                t => FindSymbol(project, family, t.Name), StringComparer.OrdinalIgnoreCase);
            if (!Bgk3DTextBatch.HasExclusiveVisibility(types.Select(t => t.Name).ToList(), (active, owner) =>
                {
                    Parameter? flag = FindUniqueParameter(symbols[active], Bgk3DTextBatch.VisibilityParameterName(owner));
                    return flag?.StorageType == StorageType.Integer ? flag.AsInteger() : null;
                }))
                throw new InvalidOperationException("Die Sichtbarkeit im Projekt weicht von der BGK-Familie ab. Ergänzung abgebrochen.");
        }

        private static Parameter VisibleParameter(Element element) =>
            element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)
            ?? throw new InvalidOperationException($"Instanz {element.Id.Value} besitzt keinen zuordenbaren Sichtbarkeitsparameter.");

        private static void AssociateVisible(FamilyManager manager, FamilyInstance instance, FamilyParameter flag)
        {
            Parameter visible = VisibleParameter(instance);
            ForgeTypeId sourceType = visible.Definition.GetDataType();
            ForgeTypeId targetType = flag.Definition.GetDataType();
            if (!manager.CanElementParameterBeAssociated(visible))
                throw new InvalidOperationException("Die Sichtbarkeit der verschachtelten Textinstanz lässt sich nicht an einen Familientyp binden.");
            if (!sourceType.Equals(targetType))
                throw new InvalidOperationException($"Die Parametertypen passen nicht: Instanz {sourceType.TypeId}, Familientyp {targetType.TypeId}.");
            manager.AssociateElementParameterToFamilyParameter(visible, flag);
        }

        private static (long? ForwardId, bool ReverseMatch, string Evidence) ReadBinding(
            Document familyDoc, FamilyManager manager, long instanceId, FamilyParameter knownFlag, string stage)
        {
            try
            {
                FamilyInstance instance = familyDoc.GetElement(new ElementId(instanceId)) as FamilyInstance
                    ?? throw new InvalidOperationException($"Die verschachtelte Textfamilie {instanceId} fehlt.");
                FamilyParameter? freshFlag = FindParameter(manager, knownFlag.Definition.Name);
                FamilyParameter flag = freshFlag ?? knownFlag;
                Parameter visible = VisibleParameter(instance);
                long? forward = manager.GetAssociatedFamilyParameter(visible)?.Id.Value;
                List<Parameter> reverse = flag.AssociatedParameters.Cast<Parameter>().ToList();
                Parameter? reverseParameter = reverse.FirstOrDefault(parameter =>
                    parameter.Element?.Id.Value == instanceId && parameter.Id.Value == visible.Id.Value);
                long? forwardViaReverse = null;
                string? reverseProbeError = null;
                if (reverseParameter != null)
                    try { forwardViaReverse = manager.GetAssociatedFamilyParameter(reverseParameter)?.Id.Value; }
                    catch (Exception error) { reverseProbeError = error.GetType().Name; }
                string evidence = Bgk3DTextBatch.BindingProbeObservation(stage, instanceId, visible.Id.Value,
                    flag.Id.Value, manager.CanElementParameterBeAssociated(visible), forward,
                    forwardViaReverse, reverseParameter != null, reverse.Count, reverseProbeError)
                    + (freshFlag == null ? ", Flag aus ursprünglicher Referenz" : ", Flag über GetParameters");
                return (forward, reverseParameter != null, evidence);
            }
            catch (Exception error)
            {
                // Nur die Diagnose darf weiterlaufen: am Ende wird bei fehlendem
                // Read-back vor jedem Projekt-Reload abgebrochen und das Protokoll gesichert.
                return (null, false, $"{stage}: Diagnosefehler bei Instanz {instanceId}: "
                    + $"{error.GetType().Name}: {error.Message}");
            }
        }

        private static string SaveBindingTrace(IReadOnlyDictionary<long, List<string>> traces)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIMassist", "Diagnostics");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"BGK-Sichtbarkeit-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
                File.WriteAllLines(path, traces.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value));
                return path;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"Protokoll konnte nicht gespeichert werden ({error.Message}); "
                    + string.Join("; ", traces.SelectMany(pair => pair.Value));
            }
        }

        private static void VerifyAssociations(Document familyDoc, FamilyManager manager,
            IReadOnlyList<FamilyType> types, string outerName)
        {
            Dictionary<string, int> counts = types.ToDictionary(t => t.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
            string[] typeNames = types.Select(t => t.Name).ToArray();
            Dictionary<long, string> expectedOwners = new();
            if (new FilteredElementCollector(familyDoc).OfClass(typeof(FreeFormElement)).GetElementCount() != 0
                || new FilteredElementCollector(familyDoc).OfClass(typeof(ModelText)).GetElementCount() != 0)
                throw new InvalidOperationException("Die äußere Familie enthält direkte oder unbearbeitete Textgeometrie.");
            RejectForeignGeometry(familyDoc, true);
            foreach (FamilyInstance instance in new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                string? storedOwner = GetGeometryOwner(instance);
                string? childName = instance.Symbol?.Family?.Name;
                string? owner = Bgk3DTextBatch.FindChildOwner(outerName, typeNames, storedOwner, childName);
                if (owner == null || instance.Symbol?.Family?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() != 0)
                    throw new InvalidOperationException($"Kindinstanz {instance.Id.Value} ist keiner eindeutigen, nicht gemeinsam genutzten BGK-Kindfamilie zuordenbar "
                        + $"(Marker: '{storedOwner ?? "fehlt"}', Kindfamilie: '{childName ?? "fehlt"}'). Nichts wurde verändert.");
                // Bestehende Kindinstanzen dürfen im Familieneditor verschoben worden
                // sein. Identität und exakte Sichtbarkeitsbindung, nicht XYZ.Zero,
                // entscheiden über die sichere Ergänzung; ihre Position bleibt erhalten.
                expectedOwners.Add(instance.Id.Value, owner);
            }
            Dictionary<long, long> expectedFlags = expectedOwners.ToDictionary(pair => pair.Key,
                pair => FindParameter(manager, Bgk3DTextBatch.VisibilityParameterName(pair.Value))?.Id.Value
                    ?? throw new InvalidOperationException($"Der Sichtbarkeitsparameter für '{pair.Value}' fehlt."));
            if (!Bgk3DTextBatch.HasExactAssociations(expectedFlags,
                elementId =>
                {
                    FamilyInstance? instance = familyDoc.GetElement(new ElementId(elementId)) as FamilyInstance;
                    if (instance == null) return Array.Empty<long>();
                    long visibleId = VisibleParameter(instance).Id.Value;
                    return manager.GetParameters().Cast<FamilyParameter>()
                        .Where(flag => flag.AssociatedParameters.Cast<Parameter>().Any(parameter =>
                            parameter.Element?.Id.Value == elementId && parameter.Id.Value == visibleId))
                        .Select(flag => flag.Id.Value).ToArray();
                },
                elementId =>
                {
                    FamilyInstance? instance = familyDoc.GetElement(new ElementId(elementId)) as FamilyInstance;
                    return instance == null ? null
                        : manager.GetAssociatedFamilyParameter(VisibleParameter(instance))?.Id.Value;
                }))
                throw new InvalidOperationException("Mindestens eine verschachtelte Textinstanz ist nicht an ihren BGK-Typ gebunden.");
            foreach (string owner in expectedOwners.Values) counts[owner]++;
            if (counts.Count == 0 || counts.Values.Any(count => count != 1))
                throw new InvalidOperationException("Jeder BGK-Typ muss genau eine verschachtelte Textinstanz besitzen.");
        }


        private static string? GetGeometryOwner(Element element)
        {
            Schema schema = GetGeometryOwnerSchema();
            Entity entity = element.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(GeometryOwnerField) : null;
        }

        private static void SetGeometryOwner(Element element, string typeName)
        {
            Schema schema = GetGeometryOwnerSchema();
            Entity entity = new(schema);
            entity.Set(GeometryOwnerField, typeName);
            element.SetEntity(entity);
            if (!string.Equals(GetGeometryOwner(element), typeName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Die BGK-Typzuordnung für Instanz {element.Id.Value} konnte nicht gespeichert werden.");
        }

        private static Schema GetGeometryOwnerSchema()
        {
            Schema? schema = Schema.Lookup(GeometryOwnerSchemaId);
            if (schema != null)
            {
                Field? field = schema.GetField(GeometryOwnerField);
                if (field == null || field.ValueType != typeof(string)
                    || field.ContainerType != ContainerType.Simple)
                    throw new InvalidOperationException("Das BIMassist-Schema für die BGK-Freiformzuordnung ist inkompatibel.");
                return schema;
            }
            SchemaBuilder builder = new(GeometryOwnerSchemaId);
            builder.SetSchemaName("BIMassist_BGK3DTextGeometryOwner");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(GeometryOwnerField, typeof(string));
            return builder.Finish();
        }

        private static Parameter? FindUniqueParameter(Element element, string name)
        {
            List<Parameter> matches = element.GetParameters(name).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException($"Der Projektparameter '{name}' ist mehrfach vorhanden; sichere Zuordnung nicht möglich.");
            return matches.SingleOrDefault();
        }

        private static FamilySymbol FindSymbol(Document project, Family family, string typeName) =>
            new FilteredElementCollector(project).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Family.Id == family.Id && string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Familientyp '{typeName}' wurde im Projekt nicht gefunden.");

        private static void SetFamilyTypeValue(FamilyManager manager, BuiltInParameter id, string value)
        {
            FamilyParameter parameter = manager.get_Parameter(id);
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
                throw new InvalidOperationException($"Typparameter {id} ist in der Familienvorlage nicht schreibbar.");
            manager.Set(parameter, value);
            if (manager.CurrentType?.AsString(parameter) != value)
                throw new InvalidOperationException($"Typparameter {id} wurde nicht in der Familie gespeichert.");
        }

        private static void VerifySymbolValue(FamilySymbol symbol, BuiltInParameter id, string expected)
        {
            Parameter parameter = symbol.get_Parameter(id);
            if (parameter == null || parameter.StorageType != StorageType.String || parameter.AsString() != expected)
                throw new InvalidOperationException($"Typparameter {id} wurde nicht korrekt ins Projekt geladen.");
        }

        private sealed class RefuseExistingLoad : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return false;
            }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return false;
            }
        }

        private sealed class ReplaceSameFamilyLoad : IFamilyLoadOptions
        {
            internal List<string> Observations { get; } = new();

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false; // Bestehende projektseitige Typwerte erhalten.
                Observations.Add($"OnFamilyFound: inUse={familyInUse}, overwriteParameterValues={overwriteParameterValues}");
                return true;
            }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                Observations.Add($"OnSharedFamilyFound: inUse={familyInUse}, source={source}, overwriteParameterValues={overwriteParameterValues}");
                return true;
            }
        }
    }
}
