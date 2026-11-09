using System.IO;
using Autodesk.Revit.DB;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

/// <summary>Read-only adapter, called synchronously inside the external command's Revit API context.</summary>
public static class RevitQuantityReader
{
    public static IEnumerable<TakeoffElement> Elements(Document document, AvaTemplate template)
    {
        using var collector = new FilteredElementCollector(document);
        foreach (var element in collector.WhereElementIsNotElementType())
        {
            if (element is RevitLinkInstance or ImportInstance { IsLinked: true }) continue;
            if (element.Category?.CategoryType != CategoryType.Model) continue;
            var captured = element;
            yield return new TakeoffElement(element.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (source, unit) => Read(document, captured, source, unit,
                    ReferenceEquals(source, template.BgkIdentification) || ReferenceEquals(source, template.TypeIdentification)),
                $"Dokument: {(string.IsNullOrWhiteSpace(document.PathName) ? document.Title : document.PathName)}; UniqueId: {element.UniqueId}");
        }
    }

    public static TakeoffValue Read(Document document, Element instance, ParameterSource source, string unit, bool allowMissingText = false)
    {
        var owner = source.Scope == ParameterScope.Type ? document.GetElement(instance.GetTypeId()) : instance;
        Parameter? parameter = null;
        if (owner != null)
        {
            switch (source.Kind)
            {
                case ParameterKind.SharedGuid:
                    parameter = owner.get_Parameter(source.SharedGuid);
                    if (parameter != null && (!parameter.IsShared || parameter.GUID != source.SharedGuid || parameter.Definition.Name != source.Name))
                        throw Invalid(source, "Gemeinsame Parameteridentität stimmt nicht überein.");
                    break;
                case ParameterKind.LocalFamily:
                    if (owner is not (FamilyInstance or FamilySymbol)) throw Invalid(source, "Lokale Familienquelle ist an diesem Systemelement nicht unterstützt.");
                    var matches = owner.GetParameters(source.Name).Where(p => !p.IsShared && p.Id.Value > 0).ToList();
                    if (matches.Count > 1) throw Invalid(source, "Mehrere gleichnamige lokale Parameter; keine eindeutige Quelle.");
                    parameter = matches.SingleOrDefault();
                    break;
                case ParameterKind.BuiltIn:
                    if (!Enum.TryParse<BuiltInParameter>(source.BuiltInId, out var builtIn) || !Enum.IsDefined(builtIn) || builtIn == BuiltInParameter.INVALID)
                        throw Invalid(source, "Unbekannte BuiltIn-ID.");
                    parameter = owner.get_Parameter(builtIn);
                    break;
                default: throw Invalid(source, "Unbekannte Quellenart.");
            }
        }
        if (parameter == null)
        {
            if (allowMissingText && source.Spec == "TEXT" && unit.Length == 0) return new(Text: "");
            throw Invalid(source, "Parameter fehlt oder enthält keinen Wert; kein Namens-/Geltungs-Fallback.");
        }
        var spec = source.Spec switch
        {
            "TEXT" => SpecTypeId.String.Text, "YESNO" => SpecTypeId.Boolean.YesNo,
            "INTEGER" => SpecTypeId.Int.Integer, "NUMBER" => SpecTypeId.Number,
            "LENGTH" => SpecTypeId.Length, "AREA" => SpecTypeId.Area, "VOLUME" => SpecTypeId.Volume,
            "ANGLE" => SpecTypeId.Angle, "MASS_DENSITY" => SpecTypeId.MassDensity,
            _ => throw Invalid(source, "Nicht unterstützte Parameterspezifikation.")
        };
        if (!parameter.Definition.GetDataType().Equals(spec)) throw Invalid(source, "Parameterspezifikation im Modell weicht von der Vorlage ab.");
        // HasValue is not proof of parameter type: validate existing definitions/storage before classifying absence.
        if (source.Spec == "TEXT")
        {
            if (unit.Length != 0 || parameter.StorageType != StorageType.String) throw Invalid(source, "TEXT erwartet eine Zeichenfolge ohne Einheit.");
        }
        else if (source.Spec == "YESNO")
        {
            if (unit.Length != 0 || parameter.StorageType != StorageType.Integer) throw Invalid(source, "YESNO erwartet ganzzahligen Speicher ohne Einheit.");
        }
        else
        {
            if (!UnitCatalog.Allows(source.Spec, unit)) throw Invalid(source, "Leseneinheit passt nicht zur Parameterspezifikation.");
            if (parameter.StorageType != (source.Spec == "INTEGER" ? StorageType.Integer : StorageType.Double))
                throw Invalid(source, "Numerischer Parameter hat nicht den erwarteten Speicher.");
        }
        if (!parameter.HasValue)
        {
            if (allowMissingText && source.Spec == "TEXT") return new(Text: "");
            throw Invalid(source, "Parameter fehlt oder enthält keinen Wert; kein Namens-/Geltungs-Fallback.");
        }
        if (source.Spec == "TEXT") return new(Text: parameter.AsString() ?? "");
        if (source.Spec == "YESNO")
        {
            if (parameter.AsInteger() is not (0 or 1)) throw Invalid(source, "YESNO erwartet 0 oder 1 ohne Einheit.");
            return new(Boolean: parameter.AsInteger() == 1);
        }
        if (source.Spec == "INTEGER") return new(Number: parameter.AsInteger());
        double value = parameter.AsDouble();
        if (source.Spec != "NUMBER")
        {
            var displayUnit = unit switch
            {
                "mm" => UnitTypeId.Millimeters, "cm" => UnitTypeId.Centimeters, "m" or "km" => UnitTypeId.Meters,
                "mm2" => UnitTypeId.SquareMillimeters, "cm2" => UnitTypeId.SquareCentimeters, "m2" or "m²" => UnitTypeId.SquareMeters,
                "cm3" => UnitTypeId.CubicCentimeters, "m3" or "m³" => UnitTypeId.CubicMeters,
                "deg" => UnitTypeId.Degrees, "rad" => UnitTypeId.Radians, "kg/m3" => UnitTypeId.KilogramsPerCubicMeter,
                _ => throw Invalid(source, "Leseneinheit wird nicht unterstützt.")
            };
            value = UnitUtils.ConvertFromInternalUnits(value, displayUnit);
            // Original Revit 2026 has no UnitTypeId.Kilometers; derive it from metres.
            if (unit == "km") value /= 1000d;
        }
        if (!double.IsFinite(value)) throw Invalid(source, "Parameterwert ist nicht endlich.");
        try { return new(Number: checked((decimal)value)); }
        catch (OverflowException ex) { throw new InvalidDataException(source.Name + ": Parameterwert überschreitet den Dezimalbereich.", ex); }
    }

    private static InvalidDataException Invalid(ParameterSource source, string message) => new(source.Name + " (" + source.Scope + "): " + message);
}
