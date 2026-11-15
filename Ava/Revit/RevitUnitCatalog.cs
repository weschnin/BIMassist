using Autodesk.Revit.DB;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

/// <summary>Localized Revit unit labels for manual, no-conversion declarations in the AVA editor.</summary>
public static class RevitUnitCatalog
{
    public static IReadOnlyList<string> GetManualUnitChoices()
    {
        var revitUnits = UnitUtils.GetAllUnits()
            .Select(unitId => (Label: LabelUtils.GetLabelForUnit(unitId), Identifier: unitId.TypeId))
            .Where(unit => !string.IsNullOrWhiteSpace(unit.Label))
            .GroupBy(unit => unit.Label, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select(unit => group.Count() > 1 && !string.IsNullOrWhiteSpace(unit.Identifier)
                ? unit.Label + " (" + unit.Identifier + ")" : unit.Label));
        return UnitCatalog.AllUnits.Concat(revitUnits)
            .Where(unit => !string.IsNullOrWhiteSpace(unit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(unit => unit, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
