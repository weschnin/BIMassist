using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Models;
using Microsoft.Win32;
using System.IO;

namespace BIMassist.Helpers
{
    public static class FamilyFileHelper
    {
        /// <summary>
        /// Fragt den Benutzer, wo die Familiendatei gespeichert werden soll.
        /// Gibt den Zielpfad zurück oder null, wenn abgebrochen.
        /// </summary>
        public static string AskAndGetFamilySavePath(Document familyDoc, string familyName)
        {
            // Prüfe, ob Familiendokument bereits lokal gespeichert wurde
            string currentPath = familyDoc.PathName;
            string defaultName = familyName + ".rfa";

            // Falls Datei noch nie gespeichert wurde oder Pfad ungültig
            bool needsSaveAs = string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath);

            // Hinweistext
            string msg =
                "Für das Speichern von Metadaten ist eine lokale Familiendatei (*.rfa) erforderlich.\n" +
                "Bitte wählen Sie, wo die Familie gespeichert werden soll.";

            // Wähle Zielverzeichnis aus
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Title = msg;
            sfd.Filter = "Revit Familie (*.rfa)|*.rfa";
            sfd.FileName = defaultName;
            if (!needsSaveAs && File.Exists(currentPath))
            {
                sfd.InitialDirectory = Path.GetDirectoryName(currentPath);
            }

            bool? result = sfd.ShowDialog();
            if (result != true) return null;

            return sfd.FileName;
        }

        /// <summary>
        /// Speichert Metadaten in der angegebenen Familie (über FamilyDocument, also .rfa!).
        /// </summary>
        public static bool SaveMetadataToFamilyRFA(Document familyDoc, string familyName, Dictionary<string, string> values, UIApplication uiapp)
        {
            try
            {
                // Suche Family im familyDoc per Name
                Family fam = new FilteredElementCollector(familyDoc)
                                .OfClass(typeof(Family))
                                .Cast<Family>()
                                .FirstOrDefault(f => f.Name == familyName);

                if (fam == null)
                {
                    TaskDialog.Show("Fehler", $"Die Familie '{familyName}' konnte im Familiendokument nicht gefunden werden.");
                    return false;
                }

                MetadataStorage.SaveMetadata(familyDoc, fam, values);

                string savePath = familyDoc.PathName;
                if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath))
                {
                    savePath = AskAndGetFamilySavePath(familyDoc, familyName);
                    if (string.IsNullOrEmpty(savePath))
                        return false;
                }

                familyDoc.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = true });
                return true;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("Fehler beim Speichern", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Öffnet die Familie temporär im Edit-Modus, speichert Metadaten und schließt wieder.
        /// </summary>
        public static bool OpenFamilyAndSaveMetadata(UIApplication uiapp, Family family, Dictionary<string, string> values)
        {
            // Versuche Familie zu öffnen
            string famPath = family.Document.PathName;
            if (string.IsNullOrEmpty(famPath) || !File.Exists(famPath))
            {
                // Versuche zu fragen...
                famPath = AskAndGetFamilySavePath(family.Document, family.Name);
                if (string.IsNullOrEmpty(famPath))
                    return false;
            }

            // Öffne das Dokument im Modus Family-Editor (HINWEIS: Das Original-Dokument bleibt im Projekt offen)
            Document familyDoc = uiapp.Application.OpenDocumentFile(famPath);
            bool result = false;
            try
            {
                result = SaveMetadataToFamilyRFA(familyDoc, family.Name, values, uiapp);
            }
            finally
            {
                if (familyDoc != null && familyDoc.IsModifiable)
                    familyDoc.Close(false); // ohne speichern, da wir schon SaveAs gemacht haben
            }
            return result;
        }

        /// <summary>
        /// Lädt Metadaten aus einer rfa-Familie. Gibt Dictionary mit Werten oder null zurück.
        /// </summary>
        public static Dictionary<string, string> LoadMetadataFromFamilyFile(UIApplication uiapp, Family family)
        {
            // 1. Prüfe Pfad:
            string famPath = family.Document.PathName;
            if (string.IsNullOrEmpty(famPath) || !File.Exists(famPath))
                return null;

            // 2. FamilyDoc öffnen (nur lesen!)
            Document famDoc = null;
            Dictionary<string, string> values = null;
            try
            {
                famDoc = uiapp.Application.OpenDocumentFile(famPath);
                // Hole die einzige Family in diesem .rfa-Dokument
                Family fam = new FilteredElementCollector(famDoc)
                                .OfClass(typeof(Family))
                                .FirstOrDefault() as Family;
                if (fam == null)
                    return null;

                // Hole Entity (Extensible Storage)
                Entity entity = MetadataStorage.LoadMetadata(famDoc, fam);
                if (entity != null && entity.IsValid())
                {
                    values = new Dictionary<string, string>();
                    foreach (var field in entity.Schema.ListFields())
                    {
                        values[field.FieldName] = entity.Get<string>(field);
                    }
                }
            }
            finally
            {
                if (famDoc != null && famDoc.IsModifiable)
                    famDoc.Close(false);
                else if (famDoc != null)
                    famDoc.Close(false);
            }
            return values;
        }
    }
}
