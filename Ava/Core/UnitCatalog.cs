namespace BIMassist.Ava.Core;

/// <summary>Explicit template units; Revit internal-unit conversion happens in the Revit reader.</summary>
public static class UnitCatalog
{
    private static readonly Dictionary<string, string[]> Units = new(StringComparer.Ordinal)
    {
        ["LENGTH"] = ["mm", "cm", "m", "km"], ["AREA"] = ["mm2", "cm2", "m2", "m²"],
        ["VOLUME"] = ["cm3", "m3", "m³"], ["ANGLE"] = ["deg", "rad"],
        ["NUMBER"] = ["1", "St", "psch"], ["INTEGER"] = ["1", "St", "psch"],
        ["MASS_DENSITY"] = ["kg/m3"]
    };
    public static IReadOnlyList<string> AllUnits => Units.Values.SelectMany(units => units).Distinct(StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<string> ForSpec(string spec) => Units.TryGetValue(spec, out var units) ? units : [];
    public static IReadOnlyList<string> ForQuantitySpec(string spec) => ForSpec(spec);
    public static IReadOnlyList<string> ForFormulaInputSpec(string spec) => spec is "NUMBER" or "INTEGER" ? ["1"] : ForSpec(spec);
    public static bool AllowsFormulaInput(string spec, string unit) => ForFormulaInputSpec(spec).Contains(unit, StringComparer.Ordinal);
    public static bool Allows(string spec, string unit) => ForSpec(spec).Contains(unit, StringComparer.Ordinal);
    public static bool AllowsQuantityUnit(string spec, string unit, bool allowCustomUnit) => allowCustomUnit
        ? (spec is "INTEGER" or "NUMBER") && !string.IsNullOrWhiteSpace(unit)
        : Allows(spec, unit);
    public static bool Compatible(string sourceUnit, string targetUnit)
    {
        // Only explicit, published UI spellings are accepted; source text itself is never rewritten.
        string Dimension(string unit) => unit switch
        {
            "mm" or "cm" or "m" or "km" => "length",
            "mm2" or "cm2" or "m2" or "m²" => "area",
            "cm3" or "m3" or "m³" => "volume",
            "deg" or "rad" => "angle",
            "St" => "count", "psch" => "lump", "1" => "number", "kg/m3" => "density", _ => ""
        };
        var dimension = Dimension(sourceUnit);
        return dimension.Length > 0 && dimension == Dimension(targetUnit);
    }
    public static decimal Convert(decimal value, string sourceUnit, string targetUnit)
    {
        if (sourceUnit == targetUnit) return value;
        if (!Compatible(sourceUnit, targetUnit))
            throw new System.IO.InvalidDataException($"Einheit '{sourceUnit}' ist nach '{targetUnit}' nicht umrechenbar; keine Zahl unter falscher LV-Einheit ausgeben.");
        static decimal ToBase(string unit) => unit switch
        {
            "mm" => 0.001m, "cm" => 0.01m, "m" => 1m, "km" => 1000m,
            "mm2" => 0.000001m, "cm2" => 0.0001m, "m2" or "m²" => 1m,
            "cm3" => 0.000001m, "m3" or "m³" => 1m,
            "deg" => 0.0174532925199432957692369077m, "rad" => 1m,
            "kg/m3" or "St" or "psch" or "1" => 1m,
            _ => throw new System.IO.InvalidDataException("Unbekannte Einheit: " + unit)
        };
        try { return checked(value * ToBase(sourceUnit) / ToBase(targetUnit)); }
        catch (OverflowException ex) { throw new System.IO.InvalidDataException("Einheitenumrechnung überschreitet den Zahlenbereich.", ex); }
    }
}
