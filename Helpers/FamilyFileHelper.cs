using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using BIMassist.Core;
using BIMassist.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist.Helpers
{
    public static class FamilyFileHelper
    {
        /// <summary>
        /// Speichert Metadaten in einer geladenen Familie im aktuellen Projekt mit Hilfe von EditFamily.
        /// </summary>
        public static bool SaveMetadataToLoadedFamily(Document projectDoc, Family family, Dictionary<string, string> values)
        {
            Document famDoc = null;
            try
            {
                famDoc = projectDoc.EditFamily(family);

                // Family-Objekt im FamilyDocument suchen
                Family fam = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault();

                if (fam == null)
                {
                    TaskDialog.Show("Fehler", "Family im Family-Editor nicht gefunden.");
                    famDoc.Close(false);
                    return false;
                }

                using (Transaction t = new Transaction(famDoc, "Metadaten speichern"))
                {
                    t.Start();
                    MetadataStorage.SaveMetadata(famDoc, fam, values); // Ohne SubTransaction reicht völlig!
                    t.Commit();
                }

                famDoc.LoadFamily(projectDoc, new JtFamilyLoadOptions());
                famDoc.Close(false);
                return true;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler beim Speichern", ex.ToString());
                if (famDoc != null && famDoc.IsModifiable)
                    famDoc.Close(false);
                return false;
            }
        }

        /// <summary>
        /// Liest Metadaten aus einer geladenen Familie im Projekt über EditFamily.
        /// </summary>
        public static Dictionary<string, string> LoadMetadataFromLoadedFamily(Document projectDoc, Family family)
        {
            Document famDoc = null;
            try
            {
                famDoc = projectDoc.EditFamily(family);

                // Family-Objekt im FamilyDocument suchen
                Family fam = new FilteredElementCollector(famDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault();

                if (fam == null)
                    return null;

                Entity entity = MetadataStorage.LoadMetadata(famDoc, fam);
                if (entity != null && entity.IsValid())
                {
                    var values = new Dictionary<string, string>();
                    foreach (var field in entity.Schema.ListFields())
                        values[field.FieldName] = entity.Get<string>(field);
                    return values;
                }
                return null;
            }
            finally
            {
                if (famDoc != null && famDoc.IsModifiable)
                    famDoc.Close(false);
            }
        }
    }
}
