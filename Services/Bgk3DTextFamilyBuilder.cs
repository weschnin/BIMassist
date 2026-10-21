using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Core;
using System.IO;

namespace BIMassist.Services
{
    /// <summary>
    /// Erstellt den Modelltext nur im temporären Familiendokument. Die Geometrie wird
    /// über den Geometrie-Ausleser von „Neue Familie aus Geometrie“ in Freiformen umgewandelt.
    /// Im Projekt wird kein kurzlebiger Modelltext angelegt oder gelöscht.
    /// </summary>
    internal static class Bgk3DTextFamilyBuilder
    {
        internal static FamilySymbol Create(UIApplication app, Document project, Bgk3DTextSpec spec, double heightMm, double depthMm)
        {
            if (project.IsFamilyDocument)
                throw new InvalidOperationException("3DText kann nur in einem Projekt erstellt werden.");
            List<Family> originalFamilies = new FilteredElementCollector(project)
                .OfClass(typeof(Family)).Cast<Family>().ToList();
            if (originalFamilies.Any(f => string.Equals(f.Name, spec.FamilyName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Die Familie '{spec.FamilyName}' ist bereits vorhanden. Keine bestehende Familie wird überschrieben.");
            HashSet<long> originalIds = originalFamilies.Select(f => f.Id.Value).ToHashSet();
            if (spec.FamilyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || spec.FamilyName.EndsWith(".", StringComparison.Ordinal))
                throw new InvalidOperationException("BGK-Nummer und -Bezeichnung ergeben keinen gültigen Familien-/Dateinamen.");

            string template = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            if (string.IsNullOrWhiteSpace(template))
                template = Path.Combine(app.Application.FamilyTemplatePath, "Allgemeine Familie.rft");
            if (!File.Exists(template))
                throw new FileNotFoundException("Die Vorlage 'Allgemeine Familie.rft' fehlt. Bitte zuerst eine gültige Vorlagendatei konfigurieren.", template);

            Document? familyDoc = null;
            string? tempDir = null;
            try
            {
                familyDoc = app.Application.NewFamilyDocument(template);
                if (familyDoc.OwnerFamily?.FamilyCategory?.Id.Value != (long)BuiltInCategory.OST_GenericModel)
                    throw new InvalidOperationException("Die gewählte Familienvorlage gehört nicht zur Kategorie 'Allgemeine Modelle'.");

                ElementId modelTextId;
                using (Transaction transaction = new Transaction(familyDoc, "BGK-Modelltext erzeugen"))
                {
                    transaction.Start();
                    FamilyManager manager = familyDoc.FamilyManager;
                    if (manager.Types.Cast<FamilyType>().Any(t => t.Name == spec.TypeName))
                        manager.CurrentType = manager.Types.Cast<FamilyType>().First(t => t.Name == spec.TypeName);
                    else
                        manager.CurrentType = manager.NewType(spec.TypeName);

                    // Die gleichen Typparameter wie bei „Übertragen“ bereits im RFA setzen.
                    SetFamilyTypeValue(manager, BuiltInParameter.ASSEMBLY_CODE, spec.AssemblyCode);
                    SetFamilyTypeValue(manager, BuiltInParameter.ALL_MODEL_DESCRIPTION, spec.Description);
                    SetFamilyTypeValue(manager, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, spec.TypeComments);

                    Parameter workPlaneBased = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                    if (workPlaneBased == null || workPlaneBased.IsReadOnly || workPlaneBased.StorageType != StorageType.Integer
                        || !workPlaneBased.Set(1) || workPlaneBased.AsInteger() != 1)
                        throw new InvalidOperationException("Die Familienvorlage lässt keine arbeitsebenenbasierte Platzierung zu.");
                    Parameter alwaysVertical = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALWAYS_VERTICAL);
                    if (alwaysVertical != null && alwaysVertical.AsInteger() != 0
                        && (alwaysVertical.IsReadOnly || !alwaysVertical.Set(0) || alwaysVertical.AsInteger() != 0))
                        throw new InvalidOperationException("Die Familienvorlage erzwingt eine vertikale Ausrichtung; geneigte Flächen sind damit nicht möglich.");

                    ModelTextType textType = new FilteredElementCollector(familyDoc)
                        .OfClass(typeof(ModelTextType)).Cast<ModelTextType>().FirstOrDefault()
                        ?? throw new InvalidOperationException("In der Familienvorlage ist kein Modelltext-Typ vorhanden.");
                    Parameter size = textType.get_Parameter(BuiltInParameter.MODEL_TEXT_SIZE);
                    if (size == null || size.IsReadOnly || size.StorageType != StorageType.Double
                        || !size.Set(UnitUtils.ConvertToInternalUnits(heightMm, UnitTypeId.Millimeters)))
                        throw new InvalidOperationException("Texthöhe konnte nicht eingestellt werden.");
                    double height = UnitUtils.ConvertToInternalUnits(heightMm, UnitTypeId.Millimeters);
                    if (Math.Abs(size.AsDouble() - height) > 1e-8)
                        throw new InvalidOperationException("Revit hat die Texthöhe nicht übernommen.");

                    SketchPlane plane = SketchPlane.Create(familyDoc,
                        Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                    ModelText text = familyDoc.FamilyCreate.NewModelText(spec.Text, textType, plane,
                        XYZ.Zero, HorizontalAlign.Left,
                        UnitUtils.ConvertToInternalUnits(depthMm, UnitTypeId.Millimeters));
                    if (text == null || text.Text != spec.Text)
                        throw new InvalidOperationException("Der erzeugte 3D-Text entspricht nicht der BGK-Auswahl.");
                    modelTextId = text.Id;
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Modelltext-Transaktion wurde nicht abgeschlossen.");
                }

                // Der bestehende Geometrie-Ausleser, aber ohne View-Filter: Ein frisch
                // angelegter Modelltext kann in der aktiven Vorlagenansicht unsichtbar sein.
                Element modelText = familyDoc.GetElement(modelTextId)
                    ?? throw new InvalidOperationException("Der 3D-Modelltext existiert nicht mehr.");
                IList<GeometryObject> geometries = FamilyGeometryTools.GetAllGeometryObjects(
                    modelText, new Options { DetailLevel = ViewDetailLevel.Fine });
                List<Solid> solids = geometries.OfType<Solid>()
                    .Where(s => s.Volume > 0).Select(s => SolidUtils.CreateTransformed(s, Transform.Identity)).ToList();
                if (solids.Count == 0 || geometries.Count != solids.Count)
                    throw new InvalidOperationException("Der Modelltext lieferte keine vollständige Volumengeometrie; die Familie wurde nicht geladen.");

                using (Transaction transaction = new Transaction(familyDoc, "3D-Text in Familiengeometrie umwandeln"))
                {
                    transaction.Start();
                    foreach (Solid solid in solids)
                        FreeFormElement.Create(familyDoc, solid);
                    familyDoc.Delete(modelTextId);
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Die Geometrieumwandlung wurde nicht abgeschlossen.");
                }
                if (familyDoc.GetElement(modelTextId) != null
                    || new FilteredElementCollector(familyDoc).OfClass(typeof(FreeFormElement)).GetElementCount() < solids.Count)
                    throw new InvalidOperationException("Der ursprüngliche Modelltext wurde nicht vollständig in Familiengeometrie umgewandelt.");
                if (familyDoc.OwnerFamily.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased)
                    throw new InvalidOperationException("Die Familienvorlage wurde nicht arbeitsebenenbasiert. Bitte eine passende Vorlage konfigurieren.");

                tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIMassist", "Temp", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string familyPath = Path.Combine(tempDir, spec.FamilyName + ".rfa");
                familyDoc.SaveAs(familyPath);
                if (!File.Exists(familyPath))
                    throw new InvalidOperationException("Die temporäre Familien-Datei wurde nicht erstellt.");

                FamilySymbol? createdSymbol = null;
                using (TransactionGroup group = new TransactionGroup(project, "BGK-3DText-Familie erstellen"))
                {
                    group.Start();
                    try
                    {
                        Family loaded = familyDoc.LoadFamily(project, new RefuseExistingFamilyLoad())
                            ?? throw new InvalidOperationException("Revit konnte die Familie nicht ins Projekt laden.");
                        if (originalIds.Contains(loaded.Id.Value) || loaded.Name != spec.FamilyName)
                            throw new InvalidOperationException("Die temporäre Familie hatte einen unerwarteten Namen oder würde eine vorhandene Familie ersetzen.");
                        using (Transaction transaction = new Transaction(project, "BGK-3DText-Typ prüfen"))
                        {
                            transaction.Start();
                            FamilySymbol symbol = new FilteredElementCollector(project)
                                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                                .FirstOrDefault(s => s.Family.Id == loaded.Id && s.Name == spec.TypeName)
                                ?? throw new InvalidOperationException("Der angeforderte Familientyp wurde nicht geladen.");
                            VerifySymbolValue(symbol, BuiltInParameter.ASSEMBLY_CODE, spec.AssemblyCode);
                            VerifySymbolValue(symbol, BuiltInParameter.ALL_MODEL_DESCRIPTION, spec.Description);
                            VerifySymbolValue(symbol, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, spec.TypeComments);
                            if (!symbol.IsActive) symbol.Activate();
                            project.Regenerate();
                            if (loaded.Name != spec.FamilyName || symbol.Name != spec.TypeName
                                || symbol.Family.FamilyPlacementType != FamilyPlacementType.WorkPlaneBased)
                                throw new InvalidOperationException("Familienname, Typname oder Platzierungsart konnte nicht verifiziert werden.");
                            if (transaction.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Die Projekttransaktion wurde nicht abgeschlossen.");
                            createdSymbol = symbol;
                        }
                        if (group.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Die Familienerstellung wurde nicht abgeschlossen.");
                    }
                    catch
                    {
                        if (group.GetStatus() == TransactionStatus.Started)
                            group.RollBack();
                        throw;
                    }
                }
                return createdSymbol ?? throw new InvalidOperationException("Der Familientyp konnte nicht ermittelt werden.");
            }
            finally
            {
                if (familyDoc != null && familyDoc.IsValidObject)
                    familyDoc.Close(false);
                if (tempDir != null && Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); }
                    catch (IOException) { /* Revit kann die Datei noch kurz sperren. */ }
                    catch (UnauthorizedAccessException) { /* Gegebenenfalls später bereinigen. */ }
                }
            }
        }

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

        private sealed class RefuseExistingFamilyLoad : IFamilyLoadOptions
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
    }
}
