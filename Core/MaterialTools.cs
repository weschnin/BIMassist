using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Fenster;

namespace BIMassist.Core
{
    internal static class MaterialTools
    {
        internal static ElementId GetMaterialId(UIDocument uiDoc)
        {
            Document doc = uiDoc.Document;

            // WPF-Fenster anzeigen
            Materialliste selectionWindow = new Materialliste(doc);
            bool? result = selectionWindow.ShowDialog();

            if (result == true && selectionWindow.SelectedMaterialId != null)
            {
                return selectionWindow.SelectedMaterialId;
            }

            return null;
        }
        internal static void DeleteUnusedMaterials(Document doc, IList<Reference> selectedElementRef)
        {
            Document family_doc = null;

            foreach (Reference reference in selectedElementRef)
            {
                Element selectedElement = doc.GetElement(reference);
                if (selectedElement is FamilyInstance)
                {
                    //Fammanage = family_doc?.FamilyManager;
                    FamilyInstance familyInstance = selectedElement as FamilyInstance;
                    Family family = familyInstance.Symbol.Family;
                    family_doc = doc.EditFamily(family);

                    // Sammle alle Materialien im Familiendokument
                    List<Material> allMaterials = new FilteredElementCollector(family_doc)
                                                  .OfClass(typeof(Material))
                                                  .Cast<Material>()
                                                  .ToList();

                    // Identifiziere verwendete Materialien im ausgewählten Element
                    HashSet<ElementId> usedMaterialIds = new HashSet<ElementId>();

                    Options geomOptions = new Options();
                    GeometryElement geomElement = selectedElement.get_Geometry(geomOptions);

                    if (geomElement != null)
                    {
                        foreach (GeometryObject geomObj in geomElement)
                        {
                            if (geomObj is Solid solid)
                            {
                                foreach (Face face in solid.Faces)
                                {
                                    ElementId materialId = face.MaterialElementId;
                                    if (materialId != ElementId.InvalidElementId)
                                    {
                                        usedMaterialIds.Add(materialId);
                                    }
                                }
                            }
                        }
                    }

                    // Nicht verwendete Materialien im Projekt identifizieren
                    List<Material> unusedMaterials = allMaterials
                                                      .Where(m => !usedMaterialIds.Contains(m.Id))
                                                      .ToList();

                    if (unusedMaterials.Count == 0)
                    {
                        TaskDialog.Show("Information", "Keine nicht verwendeten Materialien gefunden.");
                        return;
                    }

                    // Lösche nicht verwendete Materialien
                    using (Transaction trans = new Transaction(family_doc, "Lösche nicht verwendete Materialien"))
                    {
                        trans.Start();
                        foreach (Material unusedMaterial in unusedMaterials)
                        {
                            family_doc.Delete(unusedMaterial.Id);
                        }
                        trans.Commit();
                    }
                }
            }
        }
    }
}
