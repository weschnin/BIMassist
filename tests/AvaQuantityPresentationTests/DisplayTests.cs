using BIMassist.Ava.Core;
using static Program;

internal static class DisplayTests
{
    internal static void Run()
    {
        Test("legacy symbolic formulas remove only binding descriptions and source provenance", LegacySymbols);
        Test("substituted formula tokens round independently and preserve units identifiers operators and separators", NumericTokens);
        Test("decimal presentation uses max-three away-from-zero without altering source", Rounding);
    }
    private static void LegacySymbols()
    {
        var source = "Volumen | VOLUME | Instance | 86dc340a-cfda-4cfe-a55a-c1612ce602b8";
        Equal("sqrt([Volumen]/L)*L", AvaQuantityDisplay.FormulaWithParameters("sqrt([Volumen]/L)*L [Volumen ← " + source + " (m3); L ← Länge | LENGTH | Type | Parametername (Familie/Systemfamilie/gemeinsam) (m)]"));
        Equal("[L] + [H]", AvaQuantityDisplay.FormulaWithParameters("[L] + [H] [L ← Länge [Netto] | LENGTH | Instance | CURVE_ELEM_LENGTH (m); H ← Höhe | LENGTH | Type | Parametername (Familie/Systemfamilie/gemeinsam) (m)]"));
        Equal("Volumen", AvaQuantityDisplay.FormulaWithParameters(source + " (m3)"));
        Equal("Volumen", AvaQuantityDisplay.FormulaWithParameters(source));
        Equal("Fläche", AvaQuantityDisplay.FormulaWithParameters("Fläche | AREA | Type | HOST_AREA_COMPUTED (m²)"));
        Equal("2.12345 St", AvaQuantityDisplay.FormulaWithParameters("Konstante 2.12345 St"));
        foreach (var formula in new[] { "[B123] * [m2] + 1.23456", "[Volumen]", "A | B", "A [remark]", "A []", "A [x ← y]", "A [links ← rechts] + B" })
            Equal(formula, AvaQuantityDisplay.FormulaWithParameters(formula));
        Equal("", AvaQuantityDisplay.FormulaWithParameters(""));
    }
    private static void NumericTokens()
    {
        foreach (var (stored, expected) in new[]
        {
            ("1.23456 * -2.3455 = -2.895643", "1.235 * -2.346 = -2.896"),
            ("(1,23456/2,3455)**(0,5) → 0,725498 m²", "(1,235/2,346)**(0,5) → 0,725 m²"),
            ("B123 + m2 + [Fläche 1.23456] + 1.23456m2", "B123 + m2 + [Fläche 1.23456] + 1.235m2"),
            ("1.2345e-2 + 2,3455E+1 m3", "0.012 + 23,455 m3"),
            ("12345.67891 cm³ + .12345 m - ,34567 m", "12345.679 cm³ + 0.123 m - 0,346 m"),
            ("0.00049 + 0.00049 = 0.00098", "0 + 0 = 0.001"),
            ("9.1234567890123456789012345678", "9.123"),
            ("B1234567 + m2 + 1/0", "B1234567 + m2 + 1/0"),
            ("", "")
        }) Equal(expected, AvaQuantityDisplay.FormulaWithValues(stored));
    }
    private static void Rounding()
    {
        foreach (var (input, expected) in new[]
        {
            (1.2345678901234567890123456789m, 1.235m), (1.2345m, 1.235m), (-1.2345m, -1.235m),
            (1.2344m, 1.234m), (-1.2344m, -1.234m), (0.0005m, 0.001m), (-0.0005m, -0.001m),
            (0.0004m, 0m), (123m, 123m), (decimal.MaxValue, decimal.MaxValue)
        }) Equal(expected, AvaQuantityDisplay.Round(input));
    }
}
