using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using System.IO;

namespace BIMassist.Services
{
    internal static class SurfaceEnclosureFamilyBuilder
    {
        internal static void CreateFamily(UIApplication uiapp, Document project,
            Solid solid, string familyName)
        {
            ArgumentNullException.ThrowIfNull(uiapp);
            ArgumentNullException.ThrowIfNull(project);
            if (project.IsFamilyDocument || project.IsReadOnly || project.IsModifiable)
                throw new InvalidOperationException("Bitte ein bearbeitbares Projekt ohne offene Transaktion verwenden.");
            if (solid == null || !double.IsFinite(solid.Volume) || solid.Volume <= 0)
                throw new InvalidOperationException("Die umschließenden Flächen ergeben keinen geschlossenen Volumenkörper mit positivem Volumen.");
            int[] expectedLoopCounts = solid.Faces.Cast<Face>()
                .Select(face => face.GetEdgesAsCurveLoops().Count).OrderBy(count => count).ToArray();
            if (string.IsNullOrWhiteSpace(familyName)
                || familyName != familyName.Trim()
                || familyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || familyName.EndsWith(".", StringComparison.Ordinal))
                throw new InvalidOperationException("Bitte einen gültigen Familiennamen eingeben.");
            if (new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>()
                .Any(family => string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Eine Familie mit diesem Namen existiert bereits. Sie wurde nicht ersetzt.");

            string templatePath = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
            if (string.IsNullOrWhiteSpace(templatePath))
                templatePath = Path.Combine(uiapp.Application.FamilyTemplatePath, "Allgemeine Familie.rft");
            if (!File.Exists(templatePath))
            {
                TaskDialog.Show("Flächenfamilie", "Die Familienvorlage fehlt. Bitte eine Vorlage für Allgemeine Modelle auswählen.");
                Microsoft.Win32.OpenFileDialog picker = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Revit-Familienvorlage (*.rft)|*.rft",
                    InitialDirectory = uiapp.Application.FamilyTemplatePath
                };
                if (picker.ShowDialog() != true)
                    throw new System.OperationCanceledException("Vorlagenauswahl abgebrochen; es wurde keine Familie erzeugt.");
                templatePath = picker.FileName;
                if (!File.Exists(templatePath))
                    throw new InvalidOperationException("Die gewählte Familienvorlage existiert nicht.");
                Properties.Settings.Default.PfadVorlageAllgemeineFamilie = templatePath;
                Properties.Settings.Default.Save();
            }

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMassist", "Temp", Guid.NewGuid().ToString("N"));
            Document? familyDoc = null;
            try
            {
                familyDoc = uiapp.Application.NewFamilyDocument(templatePath)
                    ?? throw new InvalidOperationException("Die Familienvorlage konnte nicht geöffnet werden.");
                VerifyGenericModelCategory(familyDoc);
                using (Transaction transaction = new Transaction(familyDoc, "Volumenkörper erstellen"))
                {
                    if (transaction.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Die Familientransaktion konnte nicht begonnen werden.");
                    FamilyManager manager = familyDoc.FamilyManager;
                    manager.CurrentType = manager.Types.Cast<FamilyType>().FirstOrDefault()
                        ?? manager.NewType("Geometrie");
                    FreeFormElement form = FreeFormElement.Create(familyDoc, solid)
                        ?? throw new InvalidOperationException("Der Volumenkörper konnte nicht erstellt werden.");
                    if (!form.IsSolid)
                        throw new InvalidOperationException("Die erzeugte Freiform ist kein positiver Volumenkörper.");
                    if (transaction.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Der Volumenkörper wurde nicht gespeichert.");
                }
                VerifySolidFamily(familyDoc, solid.Volume, solid.SurfaceArea, expectedLoopCounts);

                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, familyName + ".rfa");
                familyDoc.SaveAs(path);
                familyDoc.Close(false);
                familyDoc = uiapp.Application.OpenDocumentFile(path)
                    ?? throw new InvalidOperationException("Die gespeicherte Familie konnte nicht erneut geöffnet werden.");
                VerifySolidFamily(familyDoc, solid.Volume, solid.SurfaceArea, expectedLoopCounts);

                using (TransactionGroup group = new TransactionGroup(project, "Flächenfamilie erstellen und platzieren"))
                {
                    if (group.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Die Projekttransaktionsgruppe konnte nicht begonnen werden.");
                    try
                    {
                        Family loaded = familyDoc.LoadFamily(project, new RefuseExistingFamilyLoad())
                            ?? throw new InvalidOperationException("Die Volumenkörper-Familie konnte nicht geladen werden.");
                        if (!string.Equals(loaded.Name, familyName, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Die geladene Familie hat nicht den gewünschten Namen.");
                        familyDoc.Close(false);
                        familyDoc = project.EditFamily(loaded)
                            ?? throw new InvalidOperationException("Die geladene Familie konnte nicht überprüft werden.");
                        VerifySolidFamily(familyDoc, solid.Volume, solid.SurfaceArea, expectedLoopCounts);
                        familyDoc.Close(false);
                        familyDoc = null;

                        using (Transaction transaction = new Transaction(project, "Flächenfamilie platzieren"))
                        {
                            if (transaction.Start() != TransactionStatus.Started)
                                throw new InvalidOperationException("Die Platzierungstransaktion konnte nicht begonnen werden.");
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
                            if (instance.Symbol.Id != symbol.Id || instance.Id == ElementId.InvalidElementId)
                                throw new InvalidOperationException("Die platzierte Familieninstanz konnte nicht überprüft werden.");
                            if (transaction.Commit() != TransactionStatus.Committed)
                                throw new InvalidOperationException("Die Familieninstanz wurde nicht gespeichert.");
                        }
                        if (group.Assimilate() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Die Flächenfamilie wurde nicht abgeschlossen.");
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

        private static void VerifyGenericModelCategory(Document familyDoc)
        {
            if (!familyDoc.IsFamilyDocument
                || familyDoc.OwnerFamily?.FamilyCategory?.Id.Value != (long)BuiltInCategory.OST_GenericModel)
                throw new InvalidOperationException("Die Vorlage bzw. geladene Familie ist keine Familie der Kategorie Allgemeine Modelle.");
        }

        private static void VerifySolidFamily(Document familyDoc, double expectedVolume, double expectedArea,
            IReadOnlyList<int> expectedLoopCounts)
        {
            VerifyGenericModelCategory(familyDoc);
            List<FreeFormElement> forms = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FreeFormElement)).Cast<FreeFormElement>().ToList();
            if (forms.Count != 1 || !forms[0].IsSolid)
                throw new InvalidOperationException("Die Familie enthält nicht genau einen positiven Freiform-Volumenkörper.");
            FreeFormElement form = forms[0];
            GeometryElement? geometry = form.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            Solid? candidate = geometry?.OfType<Solid>()
                .SingleOrDefault(item => double.IsFinite(item.Volume) && item.Volume > 0);
            if (candidate == null)
                throw new InvalidOperationException("Der Freiform-Volumenkörper enthält keine positive Geometrie.");
            if (!SolidUtils.ComputeIsGeometricallyClosed(candidate)
                || Math.Abs(candidate.Volume - expectedVolume) > Math.Max(expectedVolume * 1e-5, 1e-9)
                || Math.Abs(candidate.SurfaceArea - expectedArea) > Math.Max(expectedArea * 1e-5, 1e-8)
                || !candidate.Faces.Cast<Face>().Select(face => face.GetEdgesAsCurveLoops().Count)
                    .OrderBy(count => count).SequenceEqual(expectedLoopCounts))
                throw new InvalidOperationException("Die gespeicherte Familie hat eine geänderte oder nicht geschlossene Oberfläche; die Öffnung wurde nicht sicher erhalten.");
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
