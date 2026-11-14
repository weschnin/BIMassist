using System.IO;
using Autodesk.Revit.DB;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Revit;

/// <summary>Read-only adapter, called synchronously inside the external command's Revit API context.</summary>
public static class RevitQuantityReader
{
    private const int MaxCandidateDiagnostics = 6;
    private const int MaxCandidateDiagnosticLength = 220;

    private sealed record CandidateIdentityInfo(string Key, string Description);

    public static IEnumerable<TakeoffElement> Elements(Document document, AvaTemplate template,
        Func<Element, ParameterSource, IReadOnlyList<AvaParameterCandidate>, string?>? chooseAmbiguous = null)
    {
        foreach (var element in ModelInstances(document))
        {
            var captured = element;
            yield return new TakeoffElement(element.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (source, unit) => Read(document, captured, source, unit,
                    ReferenceEquals(source, template.BgkIdentification) || ReferenceEquals(source, template.TypeIdentification),
                    chooseAmbiguous == null ? null : candidates => chooseAmbiguous(captured, source, candidates)),
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

    public static IReadOnlyDictionary<string, string> ReadFamilyAndTypeLabels(Document document, IEnumerable<string> elementIds)
    {
        var requested = elementIds.ToHashSet(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var instance in ModelInstances(document))
        {
            var id = instance.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!requested.Contains(id)) continue;
            var type = document.GetElement(instance.GetTypeId()) as ElementType;
            var typeName = string.IsNullOrWhiteSpace(type?.Name) ? instance.Name : type.Name;
            if (string.IsNullOrWhiteSpace(typeName))
                throw new InvalidDataException($"Element {id}: Typname konnte für die Gruppierung nicht ermittelt werden.");
            var familyName = type is FamilySymbol symbol ? symbol.Family?.Name : null;
            familyName = string.IsNullOrWhiteSpace(familyName) ? instance.Category?.Name : familyName;
            if (string.IsNullOrWhiteSpace(familyName))
                throw new InvalidDataException($"Element {id}: Familien- oder Kategoriename konnte für die Gruppierung nicht ermittelt werden.");
            static string EscapeGroupPart(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("—", "\\—", StringComparison.Ordinal);
            labels.Add(id, EscapeGroupPart(familyName) + " — " + EscapeGroupPart(typeName));
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

    public static TakeoffValue Read(Document document, Element instance, ParameterSource source, string unit, bool allowMissingText = false,
        Func<IReadOnlyList<AvaParameterCandidate>, string?>? chooseAmbiguous = null)
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
        List<(Parameter Parameter, string Identity, string Description)>? nameCandidates = null;
        if (owner != null)
        {
            switch (source.Kind)
            {
                case ParameterKind.SharedGuid:
                    parameter = owner.get_Parameter(source.SharedGuid);
                    if (parameter != null && (!parameter.IsShared || parameter.GUID != source.SharedGuid || parameter.Definition.Name != source.Name))
                        throw Invalid(source, "Gemeinsame Parameteridentität stimmt nicht überein.");
                    break;
                case ParameterKind.LocalFamily: // Legacy templates: parameter names now resolve across family, system-family, and shared sources.
                case ParameterKind.ByName:
                    // Exact name lookup across available family, system-family, shared, and Revit
                    // parameters. Scope remains explicit; duplicates are compared before choosing.
                    IList<Parameter> named;
                    try { named = owner.GetParameters(source.Name); }
                    catch (Exception ex) when (IsCandidateDiagnosticException(ex))
                    { throw Invalid(source, "Gleichnamige Parameter konnten im gewählten Geltungsbereich nicht ermittelt werden: " + FormatCandidateException(ex)); }

                    var matches = new List<(Parameter Parameter, string Identity, string Description)>();
                    var inspectionFailures = new List<string>();
                    for (var index = 0; index < named.Count; index++)
                    {
                        var candidate = named[index];
                        CandidateIdentityInfo identity;
                        try { identity = GetCandidateIdentity(candidate); }
                        catch (Exception ex) when (IsCandidateDiagnosticException(ex))
                        {
                            inspectionFailures.Add(DescribeCandidateInspectionFailure(candidate, index, null,
                                "Identität nicht lesbar", ex));
                            continue;
                        }
                        try
                        {
                            if (candidate.Definition.GetDataType().Equals(spec))
                                matches.Add((candidate, identity.Key, identity.Description));
                        }
                        catch (Exception ex) when (IsCandidateDiagnosticException(ex))
                        {
                            inspectionFailures.Add(DescribeCandidateInspectionFailure(candidate, index, identity.Description,
                                "Spezifikation nicht lesbar", ex));
                        }
                    }
                    if (inspectionFailures.Count > 0)
                    {
                        foreach (var match in matches)
                            inspectionFailures.Add(DescribeCandidateValue(match.Parameter, match.Description, matches.IndexOf(match), source, unit));
                        var lookupFailure = named.Count > 1
                            ? "Mehrere gleichnamige Parameter konnten nicht zuverlässig geprüft werden"
                            : "Der gleichnamige Parameter konnte nicht zuverlässig geprüft werden";
                        throw Invalid(source, lookupFailure + "; mindestens ein Kandidat ist nicht prüfbar. Treffer: "
                            + FormatCandidateDiagnostics(inspectionFailures));
                    }
                    if (matches.Count == 0 && named.Count > 0)
                        throw Invalid(source, "Parametername gefunden, aber die Spezifikation im Modell weicht von der Vorlage ab.");
                    nameCandidates = matches;
                    parameter = matches.Count == 1 ? matches[0].Parameter : null;
                    break;
                case ParameterKind.BuiltIn:
                    if (!BuiltInParameterCatalog.IsValid(source.BuiltInId, typeof(BuiltInParameter))
                        || !Enum.TryParse<BuiltInParameter>(source.BuiltInId, out var builtIn))
                        throw Invalid(source, "Unbekannte BuiltIn-ID.");
                    parameter = owner.get_Parameter(builtIn);
                    break;
                default: throw Invalid(source, "Unbekannte Quellenart.");
            }
        }
        if (nameCandidates is { Count: > 1 })
        {
            var options = new List<AvaParameterCandidate>();
            var candidateDiagnostics = new List<string>();
            for (var index = 0; index < nameCandidates.Count; index++)
            {
                var candidate = nameCandidates[index];
                try
                {
                    var value = ReadParameterValue(candidate.Parameter, source, unit);
                    var option = new AvaParameterCandidate(candidate.Identity,
                        BoundCandidateDiagnostic(candidate.Description + "; Auswahlwert: " + FormatCandidateValue(value, unit)), value);
                    options.Add(option);
                    candidateDiagnostics.Add(option.Description);
                }
                catch (Exception ex) when (IsCandidateDiagnosticException(ex))
                {
                    candidateDiagnostics.Add(DescribeCandidateReadFailure(candidate.Parameter, candidate.Description, ex));
                }
            }
            var candidateSummary = FormatCandidateDiagnostics(candidateDiagnostics);
            if (options.Count != nameCandidates.Count)
                throw Invalid(source, "Mehrere gleichnamige Parameter konnten nicht zuverlässig verglichen werden; mindestens ein Kandidat ist nicht lesbar. "
                    + "Treffer: " + candidateSummary);
            if (options.All(option => option.Value == options[0].Value))
                return options[0].Value;
            else
            {
                var choice = chooseAmbiguous?.Invoke(options);
                int? selected = choice == null ? null : options.FindIndex(option => string.Equals(option.Identity, choice, StringComparison.Ordinal));
                if (selected is not >= 0)
                    throw Invalid(source, "Mehrere gleichnamige Parameter haben unterschiedliche Werte. Benutzerentscheidung fehlt oder wurde abgebrochen. "
                        + "Treffer: " + candidateSummary);
                return options[selected.Value].Value;
            }
        }
        if (parameter == null)
        {
            if (allowMissingText && source.Spec == "TEXT" && unit.Length == 0) return new(Text: "");
            throw MissingSource(document, instance, source, unit, false);
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
            throw MissingSource(document, instance, source, unit, true);
        }
        return ReadParameterValue(parameter, source, unit);
    }

    private static InvalidDataException MissingSource(Document document, Element instance, ParameterSource source,
        string unit, bool present)
    {
        var reason = present ? "Parameter ist vorhanden, enthält aber keinen Wert."
            : "Parameter ist im gewählten Geltungsbereich nicht vorhanden.";
        var findings = new List<string>();
        foreach (var (owner, scope) in new[] { (instance, "Instanz"), (document.GetElement(instance.GetTypeId()), "Typ") })
        {
            if (owner == null) { findings.Add(scope + ": kein Revit-Typ vorhanden"); continue; }
            IList<Parameter> candidates;
            try { candidates = owner.GetParameters(source.Name); }
            catch (Exception ex) when (IsCandidateDiagnosticException(ex))
            { findings.Add(scope + ": Parameterliste nicht lesbar: " + FormatCandidateException(ex)); continue; }
            if (candidates.Count == 0) { findings.Add(scope + ": kein gleichnamiger Parameter"); continue; }
            foreach (var (candidate, index) in candidates.Select((candidate, index) => (candidate, index)).Take(MaxCandidateDiagnostics))
                findings.Add(scope + ": " + DescribeCandidateValue(candidate, null, index, source, unit));
            if (candidates.Count > MaxCandidateDiagnostics) findings.Add(scope + ": weitere " + (candidates.Count - MaxCandidateDiagnostics) + " gleichnamige Parameter");
        }
        return Invalid(source, reason + " Keine automatische Typ-/Instanz-Ausweichsuche."
            + "\nModellquellen (nur Diagnose, keine Ersatzwerte):\n" + string.Join("\n", findings)
            + "\nQuelle und Geltungsbereich in der AVA-Vorlage prüfen; einen tatsächlich erforderlichen Wert im Modell pflegen.");
    }

    private static TakeoffValue ReadParameterValue(Parameter parameter, ParameterSource source, string unit)
    {
        if (!parameter.Definition.GetDataType().Equals(source.Spec switch
        {
            "TEXT" => SpecTypeId.String.Text, "YESNO" => SpecTypeId.Boolean.YesNo,
            "INTEGER" => SpecTypeId.Int.Integer, "NUMBER" => SpecTypeId.Number,
            "LENGTH" => SpecTypeId.Length, "AREA" => SpecTypeId.Area, "VOLUME" => SpecTypeId.Volume,
            "ANGLE" => SpecTypeId.Angle, "MASS_DENSITY" => SpecTypeId.MassDensity,
            _ => throw Invalid(source, "Nicht unterstützte Parameterspezifikation.")
        })) throw Invalid(source, "Parameterspezifikation im Modell weicht von der Vorlage ab.");
        if (source.Spec == "TEXT")
        {
            if (unit.Length != 0 || parameter.StorageType != StorageType.String) throw Invalid(source, "TEXT erwartet eine Zeichenfolge ohne Einheit.");
            if (!parameter.HasValue) throw Invalid(source, "Parameter enthält keinen Wert.");
            return new(Text: parameter.AsString() ?? "");
        }
        if (source.Spec == "YESNO")
        {
            if (unit.Length != 0 || parameter.StorageType != StorageType.Integer) throw Invalid(source, "YESNO erwartet ganzzahligen Speicher ohne Einheit.");
            if (!parameter.HasValue) throw Invalid(source, "Parameter enthält keinen Wert.");
            if (parameter.AsInteger() is not (0 or 1)) throw Invalid(source, "YESNO erwartet 0 oder 1 ohne Einheit.");
            return new(Boolean: parameter.AsInteger() == 1);
        }
        if (!UnitCatalog.Allows(source.Spec, unit)) throw Invalid(source, "Leseneinheit passt nicht zur Parameterspezifikation.");
        if (parameter.StorageType != (source.Spec == "INTEGER" ? StorageType.Integer : StorageType.Double))
            throw Invalid(source, "Numerischer Parameter hat nicht den erwarteten Speicher.");
        if (!parameter.HasValue) throw Invalid(source, "Parameter enthält keinen Wert.");
        if (source.Spec == "INTEGER") return new(Number: parameter.AsInteger());
        double value = parameter.AsDouble();
        if (!double.IsFinite(value)) throw Invalid(source, "Parameterwert ist nicht endlich.");
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

    private static string FormatCandidateValue(TakeoffValue value, string unit)
    {
        if (value.Number is { } number) return number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) + (unit.Length == 0 ? "" : " " + unit);
        if (value.Boolean is { } boolean) return boolean ? "Ja" : "Nein";
        return "„" + (value.Text ?? "") + "“";
    }

    private static CandidateIdentityInfo GetCandidateIdentity(Parameter parameter)
    {
        var id = parameter.Id.Value;
        if (parameter.IsShared)
        {
            var guid = parameter.GUID;
            return new("Shared:" + guid.ToString("D"), "Gemeinsam GUID " + guid + " (ID " + id + ")");
        }
        if (id < 0 && id >= int.MinValue && Enum.GetName(typeof(BuiltInParameter), (int)id) is string builtIn)
            return new("BuiltIn:" + builtIn, "BuiltIn " + builtIn + " (ID " + id + ")");
        return new("Id:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Parameter-ID " + id);
    }

    private static string DescribeCandidate(Parameter parameter, string spec)
    {
        var identity = GetCandidateIdentity(parameter).Description;
        string value = parameter.HasValue ? "Wert vorhanden" : "kein Wert";
        if (parameter.HasValue && spec == "VOLUME" && parameter.StorageType == StorageType.Double
            && parameter.Definition.GetDataType().Equals(SpecTypeId.Volume))
        {
            var raw = parameter.AsDouble();
            if (!double.IsFinite(raw)) value = "Wert nicht endlich";
            else
            {
                try
                {
                    var cubicMetres = UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.CubicMeters);
                    value = double.IsFinite(cubicMetres)
                        ? cubicMetres.ToString("G15", System.Globalization.CultureInfo.InvariantCulture) + " m3"
                        : "Wert nach Umrechnung nicht endlich";
                }
                catch (ArgumentException) { value = "Wert nicht umrechenbar"; }
            }
        }
        return $"{identity} ({value})";
    }

    private static string DescribeCandidateInspectionFailure(Parameter candidate, int index, string? identity,
        string stage, Exception exception)
    {
        var label = identity ?? SafeCandidateIdentity(candidate, index);
        return BoundCandidateDiagnostic(label + " (" + SafeCandidateValueState(candidate) + "); "
            + stage + ": " + FormatCandidateException(exception));
    }

    private static string DescribeCandidateReadFailure(Parameter candidate, string identity, Exception exception) =>
        BoundCandidateDiagnostic(identity + " (" + SafeCandidateValueState(candidate) + "); Auswahlwert nicht lesbar: "
            + FormatCandidateException(exception));

    private static string DescribeCandidateValue(Parameter candidate, string? identity, int index,
        ParameterSource source, string unit)
    {
        var label = identity ?? SafeCandidateIdentity(candidate, index);
        var state = SafeCandidateValueState(candidate);
        try
        {
            var value = ReadParameterValue(candidate, source, unit);
            return BoundCandidateDiagnostic(label + " (" + state + "); Auswahlwert: " + FormatCandidateValue(value, unit));
        }
        catch (Exception ex) when (IsCandidateDiagnosticException(ex))
        {
            return BoundCandidateDiagnostic(label + " (" + state + "); Auswahlwert nicht lesbar: " + FormatCandidateException(ex));
        }
    }

    private static string SafeCandidateIdentity(Parameter candidate, int index)
    {
        try { return GetCandidateIdentity(candidate).Description; }
        catch (Exception ex) when (IsCandidateDiagnosticException(ex))
        { return $"Kandidat {index + 1} (Identität nicht lesbar: {FormatCandidateException(ex)})"; }
    }

    private static string SafeCandidateValueState(Parameter candidate)
    {
        try { return candidate.HasValue ? "Wert vorhanden" : "kein Wert"; }
        catch (Exception ex) when (IsCandidateDiagnosticException(ex))
        { return "Wertzustand nicht lesbar: " + FormatCandidateException(ex); }
    }

    private static bool IsCandidateDiagnosticException(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private static string FormatCandidateException(Exception exception)
    {
        string message;
        try { message = exception.Message; }
        catch (Exception ex) when (IsCandidateDiagnosticException(ex))
        { message = "Fehlertext nicht lesbar (" + ex.GetType().Name + ")"; }
        message = new string(message.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        var summary = exception.GetType().Name + (message.Length == 0 ? "" : ": " + message);
        return BoundCandidateDiagnostic(summary);
    }


    private static string BoundCandidateDiagnostic(string value) => value.Length <= MaxCandidateDiagnosticLength
        ? value : value[..(MaxCandidateDiagnosticLength - 3)] + "...";

    private static string FormatCandidateDiagnostics(IReadOnlyList<string> diagnostics) =>
        string.Join("; ", diagnostics.Take(MaxCandidateDiagnostics).Select(BoundCandidateDiagnostic))
        + (diagnostics.Count > MaxCandidateDiagnostics ? $"; weitere {diagnostics.Count - MaxCandidateDiagnostics} Treffer" : "");

    private static InvalidDataException Invalid(ParameterSource source, string message) => new(source.Name + " (" + source.Scope + "): " + message);
}
