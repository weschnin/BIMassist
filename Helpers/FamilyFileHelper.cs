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
                    Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Family im Family-Editor nicht gefunden.");
                    return false;
                }

                using (Transaction t = new Transaction(famDoc, "Metadaten speichern"))
                {
                    try
                    {
                        t.Start();
                        MetadataStorage.SaveMetadata(famDoc, fam, values);
                        t.Commit();
                    }
                    catch
                    {
                        if (t.GetStatus() == TransactionStatus.Started)
                            t.RollBack();
                        throw;
                    }
                }

                famDoc.LoadFamily(projectDoc, new JtFamilyLoadOptions());
                return true;
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Fehler beim Speichern", ex.ToString());
                return false;
            }
            finally
            {
                TryCloseFamilyDocument(famDoc);
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
                TryCloseFamilyDocument(famDoc);
            }
        }

        private static void TryCloseFamilyDocument(Document famDoc)
        {
            if (famDoc == null)
                return;

            try
            {
                if (famDoc.IsValidObject)
                    famDoc.Close(false);
            }
            catch
            {
                // Best effort cleanup; shutdown/transaction state can already be in teardown.
            }
        }
    }
}
