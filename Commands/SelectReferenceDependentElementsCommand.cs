using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    internal class SelectReferenceDependentElementsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            Autodesk.Revit.DB.View activeView = doc.ActiveView;

            try
            {
                Reference pickedReference = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new ReferenceHostSelectionFilter(),
                    "Wähle eine Ebene, Referenzebene oder Referenzlinie");

                if (pickedReference == null)
                    return Result.Cancelled;

                Element referenceElement = doc.GetElement(pickedReference);
                if (referenceElement == null || !IsSupportedReferenceElement(referenceElement))
                {
                    TaskDialog.Show("BIMassist", "Es wurde keine unterstützte Ebene, Referenzebene oder Referenzlinie gewählt.");
                    return Result.Failed;
                }

                HashSet<ElementId> relatedIds = CollectRelatedElementIds(referenceElement);
                relatedIds.Add(referenceElement.Id);

                List<ElementId> matchingIds = new FilteredElementCollector(doc, activeView.Id)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Where(element => element != null && element.Id != referenceElement.Id)
                    .Where(element => DependsOnReference(element, relatedIds))
                    .Select(element => element.Id)
                    .Distinct()
                    .ToList();

                uidoc.Selection.SetElementIds(matchingIds);

                string referenceDescription = DescribeReferenceElement(referenceElement);
                TaskDialog.Show(
                    "BIMassist",
                    $"{matchingIds.Count} abhängige Elemente in der aktuellen Ansicht ausgewählt.\n\nReferenz: {referenceDescription}");

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BIMassist", "Fehler beim Auswählen referenzabhängiger Elemente:\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static bool DependsOnReference(Element element, HashSet<ElementId> relatedIds)
        {
            if (relatedIds.Contains(element.Id))
                return true;

            if (HasMatchingElementIdParameter(element, relatedIds))
                return true;

            try
            {
                ICollection<ElementId> dependentIds = element.GetDependentElements(null);
                if (dependentIds != null && dependentIds.Any(id => relatedIds.Contains(id)))
                    return true;
            }
            catch
            {
                // Nicht jedes Element unterstützt eine belastbare Abhängigkeitsabfrage.
            }

            return false;
        }

        private static HashSet<ElementId> CollectRelatedElementIds(Element referenceElement)
        {
            HashSet<ElementId> visited = new HashSet<ElementId>();
            Queue<ElementId> queue = new Queue<ElementId>();
            queue.Enqueue(referenceElement.Id);

            while (queue.Count > 0)
            {
                ElementId currentId = queue.Dequeue();
                if (!visited.Add(currentId))
                    continue;

                Element currentElement = referenceElement.Document.GetElement(currentId);
                if (currentElement == null)
                    continue;

                try
                {
                    ICollection<ElementId> dependentIds = currentElement.GetDependentElements(null);
                    if (dependentIds == null)
                        continue;

                    foreach (ElementId dependentId in dependentIds)
                    {
                        if (dependentId != null && dependentId != ElementId.InvalidElementId && !visited.Contains(dependentId))
                            queue.Enqueue(dependentId);
                    }
                }
                catch
                {
                    // Ignorieren; wir ergänzen zusätzlich Parameterauswertungen auf Kandidatenebene.
                }
            }

            return visited;
        }

        private static bool HasMatchingElementIdParameter(Element element, HashSet<ElementId> relatedIds)
        {
            try
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null || parameter.StorageType != StorageType.ElementId)
                        continue;

                    ElementId referencedId = parameter.AsElementId();
                    if (referencedId != null && referencedId != ElementId.InvalidElementId && relatedIds.Contains(referencedId))
                        return true;
                }
            }
            catch
            {
                // Manche Parametercontainer können problematisch sein; dann nur andere Heuristiken nutzen.
            }

            return false;
        }

        private static bool IsSupportedReferenceElement(Element element)
        {
            if (element is Level || element is ReferencePlane)
                return true;

            if (element is CurveElement && element.Document.IsFamilyDocument)
                return true;

            return false;
        }

        private static string DescribeReferenceElement(Element element)
        {
            string typeName = element.GetType().Name;
            string name = string.Empty;

            try
            {
                name = element.Name;
            }
            catch
            {
                name = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(name))
                return $"{typeName} ({element.Id})";

            return $"{typeName} \"{name}\" ({element.Id})";
        }

        private sealed class ReferenceHostSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element element)
            {
                return element != null && IsSupportedReferenceElement(element);
            }

            public bool AllowReference(Reference reference, XYZ position)
            {
                return true;
            }
        }
    }
}
