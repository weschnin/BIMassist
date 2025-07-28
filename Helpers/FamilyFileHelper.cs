using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Models;
using Microsoft.Win32;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist.Helpers
{
    public static class FamilyFileHelper
    {
        public static string AskAndGetFamilySavePath(Document familyDoc, string familyName)
        {
            string currentPath = familyDoc.PathName;
            string defaultName = familyName + ".rfa";
            bool needsSaveAs = string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath);

            string msg =
                "Für das Speichern von Metadaten ist eine lokale Familiendatei (*.rfa) erforderlich.\n" +
                "Bitte wählen Sie, wo die Familie gespeichert werden soll.";

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

        public static bool OpenFamilyAndSaveMetadata(UIApplication uiapp, Family family, Dictionary<string, string> values)
        {
            string famPath = family.Document.PathName;
            bool openedByMe = false;
            Document familyDoc = null;

            // Versuche Pfad zu bestimmen
            if (string.IsNullOrEmpty(famPath) || !File.Exists(famPath))
            {
                famPath = AskAndGetFamilySavePath(family.Document, family.Name);
                if (string.IsNullOrEmpty(famPath))
                    return false;
            }

            // Wenn die Familie bereits als aktives Dokument offen ist, nutze es – NICHT schließen!
            if (uiapp.ActiveUIDocument != null && uiapp.ActiveUIDocument.Document.PathName == famPath)
            {
                familyDoc = uiapp.ActiveUIDocument.Document;
                openedByMe = false;
            }
            else
            {
                familyDoc = uiapp.Application.OpenDocumentFile(famPath);
                openedByMe = true;
            }

            bool result = false;
            try
            {
                result = SaveMetadataToFamilyRFA(familyDoc, family.Name, values, uiapp);
            }
            finally
            {
                // Niemals das aktive Dokument schließen!
                if (openedByMe && familyDoc != null && familyDoc.IsModifiable)
                    familyDoc.Close(false);
            }
            return result;
        }

        public static Dictionary<string, string> LoadMetadataFromFamilyFile(UIApplication uiapp, Family family)
        {
            string famPath = family.Document.PathName;
            bool openedByMe = false;
            Document famDoc = null;
            Dictionary<string, string> values = null;

            // Keine Datei gefunden = keine Metadaten!
            if (string.IsNullOrEmpty(famPath) || !File.Exists(famPath))
                return null;

            try
            {
                // Dokument nur öffnen, wenn es NICHT aktiv ist
                if (uiapp.ActiveUIDocument != null && uiapp.ActiveUIDocument.Document.PathName == famPath)
                {
                    famDoc = uiapp.ActiveUIDocument.Document;
                    openedByMe = false;
                }
                else
                {
                    famDoc = uiapp.Application.OpenDocumentFile(famPath);
                    openedByMe = true;
                }

                Family fam = new FilteredElementCollector(famDoc)
                                .OfClass(typeof(Family))
                                .Cast<Family>()
                                .FirstOrDefault(f => f.Name == family.Name);

                if (fam == null)
                    return null;

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
                // Nur wenn selbst geöffnet schließen!
                if (openedByMe && famDoc != null && famDoc.IsModifiable)
                    famDoc.Close(false);
            }
            return values;
        }
    }
}
