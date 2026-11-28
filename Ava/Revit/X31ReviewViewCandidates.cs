using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BIMassist.Ava.Revit;

/// <summary>Read-only snapshot of model-instance candidates in the active view's graphics scope.
/// Bounding-box intersection is conservative; it does not prove that geometry is drawn or unoccluded.</summary>
public static class X31ReviewViewCandidates
{
    public static IReadOnlyList<ElementId> Find(Document document, View view, IEnumerable<long> includedIds) =>
        Find(document, view, includedIds, CreateCropFilter(view));

    // The overload allows the collector/crop policy to be exercised with API doubles.
    // In production the filter is always built from the actual view crop.
    public static IReadOnlyList<ElementId> Find(Document document, View view, IEnumerable<long> includedIds,
        Func<Element, bool> inCrop)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(includedIds);
        ArgumentNullException.ThrowIfNull(inCrop);
        if (view.IsTemplate || !FilteredElementCollector.IsViewValidForElementIteration(document, view.Id))
            throw new InvalidOperationException("Die aktive Projektansicht unterstützt keine elementbasierte Auswahl. Bitte eine gültige Modellansicht aktivieren.");

        var included = includedIds.ToHashSet();
        var temporaryMode = view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
        using var collector = new FilteredElementCollector(document, view.Id);
        return collector.WhereElementIsNotElementType()
            .Where(element => AvaPhysicalModelCandidates.IsCandidate(element)
                && !included.Contains(element.Id.Value)
                && !element.IsHidden(view)
                && !view.GetCategoryHidden(element.Category!.Id)
                && (!temporaryMode || view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, element.Id))
                && inCrop(element))
            .Select(element => element.Id).Distinct().ToArray();
    }

    // Inclusive intersection: retain touching and partly cropped bounding boxes.
    public static bool OverlapsRectangle(double cropMinX, double cropMinY, double cropMaxX, double cropMaxY,
        double elementMinX, double elementMinY, double elementMaxX, double elementMaxY) =>
        elementMaxX >= cropMinX && elementMinX <= cropMaxX
        && elementMaxY >= cropMinY && elementMinY <= cropMaxY;

    public static bool OverlapsBox(double cropMinX, double cropMinY, double cropMinZ,
        double cropMaxX, double cropMaxY, double cropMaxZ,
        double elementMinX, double elementMinY, double elementMinZ,
        double elementMaxX, double elementMaxY, double elementMaxZ) =>
        elementMaxX >= cropMinX && elementMinX <= cropMaxX
        && elementMaxY >= cropMinY && elementMinY <= cropMaxY
        && elementMaxZ >= cropMinZ && elementMinZ <= cropMaxZ;

    private static Func<Element, bool> CreateCropFilter(View view)
    {
#if AVA_X31_API_DOUBLES
        // The linked API doubles omit the host's crop geometry; the policy is
        // exercised via the injected filter. Production builds always use Revit.
        return _ => true;
#else
        if (view is View3D view3D)
        {
            if (!view3D.IsSectionBoxActive) return _ => true;
            var sectionBox = view3D.GetSectionBox();
            var sectionInverse = sectionBox.Transform.Inverse;
            return element =>
            {
                var bounds = GetBoundsInCoordinates(element, view, sectionInverse);
                return OverlapsBox(sectionBox.Min.X, sectionBox.Min.Y, sectionBox.Min.Z,
                    sectionBox.Max.X, sectionBox.Max.Y, sectionBox.Max.Z,
                    bounds.MinX, bounds.MinY, bounds.MinZ, bounds.MaxX, bounds.MaxY, bounds.MaxZ);
            };
        }
        if (!view.CropBoxActive) return _ => true;
        using (var shape = view.GetCropRegionShapeManager())
        {
            if (shape.ShapeSet || shape.NumberOfSplitRegions != 1)
                throw new InvalidOperationException("Nicht rechteckige oder geteilte Zuschneidebereiche werden für die X31-Differenzprüfung nicht unterstützt.");
        }
        var crop = view.CropBox ?? throw new InvalidOperationException("Der Zuschneidebereich der Ansicht ist nicht lesbar.");
        var inverse = crop.Transform.Inverse;
        return element =>
        {
            var bounds = GetBoundsInCoordinates(element, view, inverse);
            return OverlapsRectangle(crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y,
                bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        };
#endif
    }

#if !AVA_X31_API_DOUBLES
    private readonly record struct CoordinateBounds(double MinX, double MinY, double MinZ,
        double MaxX, double MaxY, double MaxZ);

    private static CoordinateBounds GetBoundsInCoordinates(Element element, View view, Transform inverse)
    {
        var box = element.get_BoundingBox(view) ?? throw new InvalidOperationException(
            $"Die Begrenzung von Element {element.Id.Value} ist nicht lesbar; die Differenzprüfung wurde abgebrochen.");
        var lowX = double.PositiveInfinity;
        var lowY = double.PositiveInfinity;
        var lowZ = double.PositiveInfinity;
        var highX = double.NegativeInfinity;
        var highY = double.NegativeInfinity;
        var highZ = double.NegativeInfinity;
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
        {
            var point = inverse.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z)));
            lowX = Math.Min(lowX, point.X);
            lowY = Math.Min(lowY, point.Y);
            lowZ = Math.Min(lowZ, point.Z);
            highX = Math.Max(highX, point.X);
            highY = Math.Max(highY, point.Y);
            highZ = Math.Max(highZ, point.Z);
        }
        return new CoordinateBounds(lowX, lowY, lowZ, highX, highY, highZ);
    }
#endif
}
