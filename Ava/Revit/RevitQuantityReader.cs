using System.IO;
using Autodesk.Revit.DB;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

/// <summary>Read-only adapter, called synchronously inside the external command's Revit API context.</summary>
public static class RevitQuantityReader
{
    public static IEnumerable<TakeoffElement> Elements(Document document, AvaTemplate template)
    {
        foreach (var element in ModelInstances(document))
        {
            var captured = element;
            yield return new TakeoffElement(element.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (source, unit) => Read(document, captured, source, unit,
                    ReferenceEquals(source, template.BgkIdentification) || ReferenceEquals(source, template.TypeIdentification)),
                $"Dokument: {(string.IsNullOrWhiteSpace(document.PathName) ? document.Title : document.PathName)}; UniqueId: {element.UniqueId}");
        }
    }

    public static IReadOnlyList<GroupingParameterOption> GetGroupingParameterOptions(Document document)
    {
        var options = new HashSet<GroupingParameterOption>();
        foreach (var instance in ModelInstances(document))
        {
            AddTextParameters(instance, ParameterScope.Instance, options);
            var type = document.GetElement(instance.GetTypeId());
            if (type != null) AddTextParameters(type, ParameterScope.Type, options);
        }
        return options.OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Scope).ToArray();
    }

    public static IReadOnlyDictionary<string, string> ReadGroupingLabels(Document document,
        IEnumerable<string> elementIds, GroupingParameterOption option)
    {
        if (option == null || string.IsNullOrWhiteSpace(option.Name)) throw new InvalidDataException("Gruppierungsparameter fehlt.");
        var requested = elementIds.ToHashSet(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (requested.Count == 0) return labels;
        foreach (var instance in ModelInstances(document))
        {
            var id = instance.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!requested.Contains(id)) continue;
            var owner = option.Scope == ParameterScope.Type ? document.GetElement(instance.GetTypeId()) : instance;
            if (owner == null) throw new InvalidDataException($"Element {id}: der Typ für Gruppierungsparameter '{option.Name}' fehlt.");
            var matches = owner.GetParameters(option.Name);
            if (matches.Count != 1)
                throw new InvalidDataException($"Element {id}: Gruppierungsparameter '{option.Name}' ist im gewählten Geltungsbereich {(matches.Count == 0 ? "nicht vorhanden" : "mehrdeutig")}.");
            var parameter = matches[0];
            if (parameter.StorageType != StorageType.String || !parameter.Definition.GetDataType().Equals(SpecTypeId.String.Text))
                throw new InvalidDataException($"Element {id}: Gruppierungsparameter '{option.Name}' ist kein Textparameter.");
            if (!parameter.HasValue || string.IsNullOrWhiteSpace(parameter.AsString()))
                throw new InvalidDataException($"Element {id}: Gruppierungsparameter '{option.Name}' enthält keine Bezeichnung.");
            var label = parameter.AsString()!;
            if (label.Any(char.IsControl) || label.Any(char.IsSurrogate) ||
                label.Any(c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
                throw new InvalidDataException($"Element {id}: Gruppierungsparameter '{option.Name}' enthält keine einzeilige Bezeichnung.");
            labels.Add(id, label);
        }
        var missing = requested.Except(labels.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        if (missing != null) throw new InvalidDataException($"Modellelement {missing} konnte für die Gruppierung nicht gefunden werden.");
        return labels;
    }

    private static IEnumerable<Element> ModelInstances(Document document)
    {
        using var collector = new FilteredElementCollector(document);
        return collector.WhereElementIsNotElementType()
            .Where(element => element is not (RevitLinkInstance or ImportInstance { IsLinked: true })
                && element.Category?.CategoryType == CategoryType.Model).ToArray();
    }

    private static void AddTextParameters(Element owner, ParameterScope scope, HashSet<GroupingParameterOption> options)
    {
        foreach (Parameter parameter in owner.Parameters)
            if (parameter.StorageType == StorageType.String && parameter.Definition.GetDataType().Equals(SpecTypeId.String.Text)
                && !string.IsNullOrWhiteSpace(parameter.Definition.Name))
                options.Add(new(parameter.Definition.Name, scope));
    }

    public static TakeoffValue Read(Document document, Element instance, ParameterSource source, string unit, bool allowMissingText = false)
    {
        var owner = source.Scope == ParameterScope.Type ? document.GetElement(instance.GetTypeId()) : instance;
        Parameter? parameter = null;
        var spec = source.Spec switch
        {
            "TEXT" => SpecTypeId.String.Text, "YESNO" => SpecTypeId.Boolean.YesNo,
            "INTEGER" => SpecTypeId.Int.Integer, "NUMBER" => SpecTypeId.Number,
            "LENGTH" => SpecTypeId.Length, "AREA" => SpecTypeId.Area, "VOLUME" => SpecTypeId.Volume,
            "ANGLE" => SpecTypeId.Angle, "MASS_DENSITY" => SpecTypeId.MassDensity,
            _ => throw Invalid(source, "Nicht unterstützte Parameterspezifikation.")
        };
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
                    var local = owner.GetParameters(source.Name).Where(p => !p.IsShared && p.Id.Value > 0).ToList();
                    if (local.Count > 1) throw Invalid(source, "Mehrere gleichnamige lokale Parameter; keine eindeutige Quelle.");
                    parameter = local.SingleOrDefault();
                    break;
                case ParameterKind.ByName:
                    // Explicit language-dependent name lookup across built-in, shared and local
                    // parameters. On the selected owner, never choose an arbitrary duplicate.
                    var named = owner.GetParameters(source.Name);
                    var matches = named.Where(p => p.Definition.GetDataType().Equals(spec)).ToList();
                    if (matches.Count > 1) throw Invalid(source, "Mehrere Parameter mit diesem Namen und dieser Spezifikation im gewählten Geltungsbereich; keine eindeutige Quelle. Bitte GUID oder BuiltIn-Kennung verwenden.");
                    if (matches.Count == 0 && named.Count > 0)
                        throw Invalid(source, "Parametername gefunden, aber die Spezifikation im Modell weicht von der Vorlage ab.");
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
            throw Invalid(source, "Parameter fehlt oder enthält keinen Wert im gewählten Geltungsbereich; keine automatische Typ-/Instanz-Ausweichsuche.");
        }
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
            throw Invalid(source, "Parameter fehlt oder enthält keinen Wert im gewählten Geltungsbereich; keine automatische Typ-/Instanz-Ausweichsuche.");
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
