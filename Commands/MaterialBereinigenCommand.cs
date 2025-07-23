using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class MaterialBereinigenCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                ICollection<ElementId> materialsToDelete = new Collection<ElementId>();
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Material)))
                {
                    if (e.Name == "Standard")
                        materialsToDelete.Add(e.Id);
                }

                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)))
                {
                    if (e.Name == "Standard")
                        materialsToDelete.Add(e.Id);
                }

                if (materialsToDelete.Count > 0)
                {
                    MessageBoxResult rslt = MessageBox.Show("Sollen " + materialsToDelete.Count().ToString() + " Standard-Materialelemente gelöscht werden?", "Materialbibliothek bereinigen", MessageBoxButton.YesNoCancel);
                    if (rslt == MessageBoxResult.Yes)
                    {
                        Transaction trans = new Transaction(doc, "Delete Standard Materials");
                        trans.Start();
                        doc.Delete(materialsToDelete);
                        trans.Commit();
                    }
                }
                else
                {
                    TaskDialog.Show("AWESBox", "Es wurden keine Standard-Materialelemente gefunden.");
                }

            }
            catch (Exception msg)
            {
                TaskDialog.Show("Warnung", msg.Message);
            }

            return Result.Succeeded;
        }
    }
}
