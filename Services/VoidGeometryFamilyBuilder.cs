using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using BIMassist.Core;
using System.IO;

namespace BIMassist.Services
{
    internal static class VoidGeometryFamilyBuilder
    {
        private const double MinVoidVolume = 1e-9;

        internal static void CreateVoidFamily(UIApplication uiapp, Document project,
            IList<GeometryObject> geometry, string familyName)
        {
            if (project.IsFamilyDocument || project.IsReadOnly || project.IsModifiable)
                throw new InvalidOperationException("Bitte ein bearbeitbares Projekt ohne offene Transaktion verwenden.");
            if (string.IsNullOrWhiteSpace(familyName)
                || familyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || familyName.EndsWith(".", StringComparison.Ordinal))
                throw new InvalidOperationException("Bitte einen gültigen Familiennamen eingeben.");
            if (new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>()
                .Any(family => string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Eine Familie mit diesem Namen existiert bereits. Sie wurde nicht ersetzt.");

            // Der Mesh-/DirectShape-Fallback der normalen Geometriefamilie kann nicht schneiden.
            if (geometry.Count == 0 || geometry.Any(item => item is not Solid)
                || geometry.OfType<Solid>().Any(solid => solid.Volume <= MinVoidVolume))
                throw new InvalidOperationException("Abzugskörper erfordern ausschließlich geschlossene Volumenkörper. Meshes oder offene Flächen können nicht als schneidende Abzugskörper geladen werden.");

            string templatePath = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            if (string.IsNullOrWhiteSpace(templatePath))
                templatePath = Path.Combine(uiapp.Application.FamilyTemplatePath, "Allgemeine Familie.rft");
            if (!File.Exists(templatePath))
            {
                TaskDialog.Show("Abzugskörper", "Die Familienvorlage fehlt. Bitte eine Vorlage für Allgemeine Modelle auswählen.");
                Microsoft.Win32.OpenFileDialog picker = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Revit-Familienvorlage (*.rft)|*.rft",
                    InitialDirectory = uiapp.Application.FamilyTemplatePath
                };
                if (picker.ShowDialog() != true)
                    throw new InvalidOperationException("Vorlagenauswahl abgebrochen; es wurde keine Familie erzeugt.");
                templatePath = picker.FileName;
                Properties.Settings.Default.PfadVorlageAllgemeineFamilie = templatePath;
                Properties.Settings.Default.Save();
            }

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMassist", "Temp", Guid.NewGuid().ToString("N"));
            Document? familyDoc = null;
            try
            {
                familyDoc = uiapp.Application.NewFamilyDocument(templatePath);
                if (familyDoc.OwnerFamily?.FamilyCategory?.Id.Value != (long)BuiltInCategory.OST_GenericModel)
                    throw new InvalidOperationException("Die Vorlage ist keine Familie der Kategorie Allgemeine Modelle.");
                List<ElementId> voidIds = new();
                using (Transaction transaction = new Transaction(familyDoc, "Abzugskörper erstellen"))
                {
                    transaction.Start();
                    FamilyManager manager = familyDoc.FamilyManager;
                    manager.CurrentType = manager.Types.Cast<FamilyType>().FirstOrDefault()
                        ?? manager.NewType("Geometrie");
                    foreach (Solid solid in geometry.Cast<Solid>())
                    {
                        FreeFormElement form = FreeFormElement.Create(familyDoc, solid)
                            ?? throw new InvalidOperationException("Ein Abzugskörper konnte nicht erstellt werden.");
                        Parameter cutting = form.get_Parameter(BuiltInParameter.ELEMENT_IS_CUTTING);
                        if (cutting == null || cutting.IsReadOnly || cutting.StorageType != StorageType.Integer
                            || (cutting.AsInteger() != 1 && !cutting.Set(1)) || cutting.AsInteger() != 1)
                            throw new InvalidOperationException("Die Freiform konnte nicht in einen Abzugskörper umgewandelt werden.");
                        voidIds.Add(form.Id);
                    }
                    Parameter option = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
                    if (option == null || option.IsReadOnly || option.StorageType != StorageType.Integer
                        || (option.AsInteger() != 1 && !option.Set(1)) || option.AsInteger() != 1)
                        throw new InvalidOperationException("Die Familienoption 'Mit Abzugskörpern schneiden, wenn geladen' konnte nicht aktiviert werden.");
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Die Abzugskörper wurden nicht gespeichert.");
                }
                VerifyVoidFamily(familyDoc, voidIds.Count);

                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, familyName + ".rfa");
                familyDoc.SaveAs(path);
                familyDoc.Close(false);
                familyDoc = uiapp.Application.OpenDocumentFile(path);
                VerifyVoidFamily(familyDoc, voidIds.Count);
                Family loaded;
                using (TransactionGroup group = new TransactionGroup(project, "Abzugskörper-Familie erstellen und platzieren"))
                {
                    group.Start();
                    try
                    {
                        loaded = familyDoc.LoadFamily(project, new RefuseExistingFamilyLoad())
                            ?? throw new InvalidOperationException("Die Abzugskörper-Familie konnte nicht geladen werden.");
                        if (!string.Equals(loaded.Name, familyName, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Die geladene Familie hat nicht den gewünschten Namen.");
                        familyDoc.Close(false);
                        familyDoc = project.EditFamily(loaded);
                        VerifyVoidFamily(familyDoc, voidIds.Count);
                        familyDoc.Close(false);
                        familyDoc = null;

                        using (Transaction transaction = new Transaction(project, "Abzugskörper-Familie platzieren"))
                        {
                            transaction.Start();
                            FamilySymbol symbol = new FilteredElementCollector(project)
                                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                                .FirstOrDefault(candidate => candidate.Family.Id == loaded.Id)
                                ?? throw new InvalidOperationException("Die geladene Familie enthält keinen Familientyp.");
                            if (!symbol.IsActive) symbol.Activate();
                            project.Regenerate();
                            FamilyInstance instance = project.Create.NewFamilyInstance(
                                XYZ.Zero, symbol, StructuralType.NonStructural)
                                ?? throw new InvalidOperationException("Die Familie konnte nicht platziert werden.");
                            project.Regenerate();
                            if (!InstanceVoidCutUtils.IsVoidInstanceCuttingElement(instance))
                                throw new InvalidOperationException("Die geladene Instanz ist kein schneidender Abzugskörper.");
                            if (transaction.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Die Abzugskörper-Instanz wurde nicht gespeichert.");
                        }
                        if (group.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Die Abzugskörper-Familie wurde nicht abgeschlossen.");
                    }
                    catch
                    {
                        if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                        throw;
                    }
                }
            }
            finally
            {
                if (familyDoc != null && familyDoc.IsValidObject) familyDoc.Close(false);
                if (Directory.Exists(folder))
                {
                    try { Directory.Delete(folder, true); }
                    catch (IOException) { /* Revit kann die temporäre RFA noch sperren. */ }
                    catch (UnauthorizedAccessException) { /* Die Projekttransaktion bleibt davon unberührt. */ }
                }
            }
        }

        private static void VerifyVoidFamily(Document familyDoc, int expectedCount)
        {
            Parameter option = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
            if (option == null || option.AsInteger() != 1)
                throw new InvalidOperationException("Die Familienoption 'Mit Abzugskörpern schneiden, wenn geladen' ist nicht aktiv.");
            List<FreeFormElement> forms = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FreeFormElement)).Cast<FreeFormElement>().ToList();
            if (forms.Count != expectedCount || forms.Any(form => form.IsSolid
                || form.get_Parameter(BuiltInParameter.ELEMENT_IS_CUTTING)?.AsInteger() != 1))
                throw new InvalidOperationException("Die Familiengeometrie enthält nicht ausschließlich die erwarteten Abzugskörper.");
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
