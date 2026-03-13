using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;

namespace BIMassist.Commands
{
    /// <summary>
    /// Dieses ExternalCommand löscht alle Materialien und AppearanceAssets mit dem Namen "Standard" aus dem aktuellen Revit-Modell.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MaterialBereinigenCommand : IExternalCommand
    {
        /// <summary>
        /// Haupteinstiegspunkt für das ExternalCommand.
        /// </summary>
        /// <param name="commandData">Stellt Zugriff auf das aktuelle Revit-Modell bereit.</param>
        /// <param name="message">Wird für Fehlermeldungen verwendet.</param>
        /// <param name="elements">ElementSet, das bei Fehlern verwendet werden kann.</param>
        /// <returns>Result.Succeeded wenn erfolgreich, sonst Result.Failed.</returns>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Zugriff auf das aktuelle Dokument
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Liste für zu löschende ElementIds anlegen
                var materialsToDelete = new List<ElementId>();

                // Alle Material-Elemente durchsuchen und solche mit dem Namen "Standard" markieren
                foreach (Material mat in new FilteredElementCollector(doc).OfClass(typeof(Material)))
                {
                    if (mat.Name == "Standard")
                        materialsToDelete.Add(mat.Id);
                }

                // Auch alle AppearanceAssetElement-Elemente mit Namen "Standard" aufnehmen
                foreach (AppearanceAssetElement asset in new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)))
                {
                    if (asset.Name == "Standard")
                        materialsToDelete.Add(asset.Id);
                }

                // Prüfen, ob zu löschende Elemente gefunden wurden
                if (materialsToDelete.Count > 0)
                {
                    // Benutzerabfrage: Löschen bestätigen
                    var result = System.Windows.MessageBox.Show(
                        $"Sollen {materialsToDelete.Count} 'Standard'-Materialelement(e) gelöscht werden?",
                        "Materialbibliothek bereinigen",
                        MessageBoxButton.YesNoCancel
                    );

                    if (result == MessageBoxResult.Yes)
                    {
                        // Transaktion zum Löschen starten
                        using (Transaction trans = new Transaction(doc, "Standard-Materialien löschen"))
                        {
                            trans.Start();
                            doc.Delete(materialsToDelete);
                            trans.Commit();
                        }
                    }
                }
                else
                {
                    // Keine passenden Elemente gefunden
                    Autodesk.Revit.UI.TaskDialog.Show("Materialbereinigung", "Es wurden keine 'Standard'-Materialelemente gefunden.");
                }
            }
            catch (Exception ex)
            {
                // Fehlerbehandlung: Meldung ausgeben und Fehler zurückgeben
                Autodesk.Revit.UI.TaskDialog.Show("Fehler bei der Materialbereinigung", ex.Message);
                message = ex.Message;
                return Result.Failed;
            }

            // Erfolg zurückgeben
            return Result.Succeeded;
        }
    }
}
