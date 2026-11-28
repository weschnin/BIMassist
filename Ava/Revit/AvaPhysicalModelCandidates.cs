using Autodesk.Revit.DB;

namespace BIMassist.Ava.Revit;

/// <summary>Shared, read-only eligibility for takeoff and unconsidered-element selection.
/// CategoryType.Model alone also includes cameras, materials, settings and other resources.
/// Identification parameters and their values never determine physical eligibility.</summary>
public static class AvaPhysicalModelCandidates
{
    public static bool IsCandidate(Element element)
    {
        // HVAC grouping zones and drawing/sketch curves are not physical components even
        // when their categories support subcategories/material quantities. CurveElement
        // intentionally does NOT include MEPCurve (pipes/ducts), nor Room/Space.
        if (element is ElementType or RevitLinkInstance or ImportInstance { IsLinked: true }
            or Autodesk.Revit.DB.Mechanical.Zone or CurveElement
            || element.ViewSpecific || element.Category is not { CategoryType: CategoryType.Model } category)
            return false;

        // Keep placed loadable families, direct shapes and nonlinked CAD copies even when
        // their category has no material takeoff/subcategory support. Do not demand geometry
        // or a valid type ID: an unmarked/empty placed instance still needs diagnostics.
        return element is FamilyInstance or DirectShape or ImportInstance
            || category.CanAddSubcategory || category.HasMaterialQuantities;
    }
}