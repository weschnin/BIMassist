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
        Func<Element, ParameterSource, IReadOnlyList<AvaParameterCandidate>, string?>? chooseAmbiguous = null,
        ElementId? viewId = null)
    {
        foreach (var element in ModelInstances(document, viewId))
        {
            var captured = element;
            var type = document.GetElement(element.GetTypeId()) as ElementType;
            var typeName = string.IsNullOrWhiteSpace(type?.Name) ? element.Name : type.Name;
            var familyName = type is FamilySymbol symbol ? symbol.Family?.Name : null;
            familyName = string.IsNullOrWhiteSpace(familyName) ? element.Category?.Name : familyName;
            yield return new TakeoffElement(element.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (source, unit) => Read(document, captured, source, unit,
                    ReferenceEquals(source, template.BgkIdentification) || ReferenceEquals(source, template.TypeIdentification),
                    chooseAmbiguous == null ? null : candidates => chooseAmbiguous(captured, source, candidates)),
                $"Dokument: {(string.IsNullOrWhiteSpace(document.PathName) ? document.Title : document.PathName)}; UniqueId: {element.UniqueId}")
            {
                ElementUniqueId = element.UniqueId,
                FamilyName = familyName ?? "",
                TypeName = typeName ?? ""
            };
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

    /// <summary>Captures grouping values only for the measured host instances, without modifying the model.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadGroupingSnapshots(
        Document document, IEnumerable<string> elementIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(elementIds);
        var requested = elementIds.ToHashSet(StringComparer.Ordinal);
        var snapshots = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (requested.Count == 0) return snapshots;
        // Collect once, not once per offered parameter. Parameter discovery is measured-scope only.
        var measured = ModelInstances(document)
            .Select(instance => (Id: instance.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), Instance: instance))
            .Where(item => requested.Contains(item.Id))
            .Select(item => (item.Id, item.Instance, Type: document.GetElement(item.Instance.GetTypeId())))
            .ToArray();
        var missing = requested.Except(measured.Select(item => item.Id), StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        if (missing != null)
            throw new InvalidDataException($"Modellelement {missing} konnte für die Gruppierung nicht gefunden werden.");
        var options = new HashSet<GroupingParameterOption>();
        foreach (var item in measured)
        {
            AddSnapshotTextParameters(item.Instance, ParameterScope.Instance, options);
            if (item.Type != null) AddSnapshotTextParameters(item.Type, ParameterScope.Type, options);
        }
        foreach (var option in options.OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ThenBy(option => option.Scope))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var compatible = true;
            foreach (var item in measured)
            {
                var owner = option.Scope == ParameterScope.Type ? item.Type : item.Instance;
                if (!TryReadSnapshotValue(owner, option.Name, out var value))
                {
                    compatible = false;
                    break;
                }
                values.Add(item.Id, value);
            }
            // An option is usable only when every present parameter has the exact unambiguous text contract.
            if (compatible)
                snapshots.Add(option.DisplayLabel, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(values));
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(snapshots);
    }

    private static void AddSnapshotTextParameters(Element owner, ParameterScope scope, HashSet<GroupingParameterOption> options)
    {
        foreach (Parameter parameter in owner.Parameters)
        {
            try
            {
                var option = new GroupingParameterOption(parameter.Definition.Name, scope);
                if (!string.IsNullOrWhiteSpace(option.Name) && IsSnapshotText(option.DisplayLabel, 1024)
                    && parameter.StorageType == StorageType.String && parameter.Definition.GetDataType().Equals(SpecTypeId.String.Text))
                    options.Add(option);
            }
            catch (Exception ex) when (IsCandidateDiagnosticException(ex))
            {
                // Uninspectable parameters cannot create grouping options. A same-name option discovered
                // on another measured element is checked against this owner before it is offered.
            }
        }
    }

    private static bool TryReadSnapshotValue(Element? owner, string name, out string value)
    {
        value = "";
        if (owner == null) return true;
        try
        {
            var matches = owner.GetParameters(name);
            if (matches.Count == 0) return true;
            if (matches.Count != 1) return false;
            var parameter = matches[0];
            if (parameter.StorageType != StorageType.String || !parameter.Definition.GetDataType().Equals(SpecTypeId.String.Text))
                return false;
            if (!parameter.HasValue) return true;
            var text = parameter.AsString() ?? "";
            if (!IsSnapshotText(text, 32768)) return false;
            value = string.IsNullOrWhiteSpace(text) ? "" : text;
            return true;
        }
        catch (Exception ex) when (IsCandidateDiagnosticException(ex)) { return false; }
    }

    private static bool IsSnapshotText(string text, int maxLength) => text.Length <= maxLength
        && !text.Any(character => char.IsControl(character) || char.IsSurrogate(character)
            || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);

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

    private static IEnumerable<Element> ModelInstances(Document document, ElementId? viewId = null)
    {
        View? activeView = null;
        if (viewId != null)
        {
            if (document.GetElement(viewId) is not View view)
                throw new InvalidDataException("Die aktive Revit-Ansicht ist ungültig oder nicht verfügbar; sichtbare Elemente können nicht ermittelt werden.");
            if (view.IsTemplate)
                throw new InvalidDataException("Die aktive Revit-Ansicht ist eine Ansichtsvorlage; sichtbare Elemente können nicht ermittelt werden.");
            if (!FilteredElementCollector.IsViewValidForElementIteration(document, viewId))
                throw new InvalidDataException("Die aktive Revit-Ansicht unterstützt keine elementbasierte Mengenermittlung. Wählen Sie eine unterstützte Modellansicht oder deaktivieren Sie ‚nur sichtbare Elemente‘.");
            activeView = view;
        }
        using var collector = viewId == null
            ? new FilteredElementCollector(document)
            : new FilteredElementCollector(document, viewId);
        var elements = collector.WhereElementIsNotElementType()
            .Where(element => element is not (RevitLinkInstance or ImportInstance { IsLinked: true })
                && element.Category?.CategoryType == CategoryType.Model).ToArray();
        if (activeView != null)
        {
            elements = elements.Where(element => !element.IsHidden(activeView)
                && element.Category != null && !activeView.GetCategoryHidden(element.Category.Id)).ToArray();
            if (activeView.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                elements = elements.Where(element => activeView.IsElementVisibleInTemporaryViewMode(
                    TemporaryViewMode.TemporaryHideIsolate, element.Id)).ToArray();
        }
        return elements;
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
                            if (MatchesSpec(candidate, source.Spec, spec))
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
                            inspectionFailures.Add(DescribeCandidateValue(document, match.Parameter, match.Description, matches.IndexOf(match), source, unit));
                        var lookupFailure = named.Count > 1
                            ? "Mehrere gleichnamige Parameter konnten nicht zuverlässig geprüft werden"
                            : "Der gleichnamige Parameter konnte nicht zuverlässig geprüft werden";
                        throw Invalid(source, lookupFailure + "; mindestens ein Kandidat ist nicht prüfbar. Treffer: "
                            + FormatCandidateDiagnostics(inspectionFailures));
                    }
                    if (matches.Count == 0 && named.Count > 0)
                        throw Invalid(source, "Parametername gefunden, aber die Spezifikation im Modell weicht von der Vorlage ab. "
                            + "Modellspezifikation(en): " + ActualSpecs(named));
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
                    var value = ReadParameterValue(document, candidate.Parameter, source, unit);
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
        if (!MatchesSpec(parameter, source.Spec, spec))
            throw Invalid(source, "Parameterspezifikation im Modell weicht von der Vorlage ab. Modellspezifikation: " + ActualSpecs([parameter]));
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
        return ReadParameterValue(document, parameter, source, unit);
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
                findings.Add(scope + ": " + DescribeCandidateValue(document, candidate, null, index, source, unit));
            if (candidates.Count > MaxCandidateDiagnostics) findings.Add(scope + ": weitere " + (candidates.Count - MaxCandidateDiagnostics) + " gleichnamige Parameter");
        }
        return Invalid(source, reason + " Keine automatische Typ-/Instanz-Ausweichsuche."
            + "\nModellquellen (nur Diagnose, keine Ersatzwerte):\n" + string.Join("\n", findings)
            + "\nQuelle und Geltungsbereich in der AVA-Vorlage prüfen; einen tatsächlich erforderlichen Wert im Modell pflegen.");
    }

    private static string ActualSpecs(IEnumerable<Parameter> candidates)
    {
        var list = candidates.ToList();
        var specs = list.Take(MaxCandidateDiagnostics).Select(parameter =>
        {
            try { return BoundCandidateDiagnostic(parameter.Definition.GetDataType().TypeId); }
            catch (Exception ex) when (IsCandidateDiagnosticException(ex)) { return "nicht lesbar: " + FormatCandidateException(ex); }
        });
        return string.Join(", ", specs) + (list.Count > MaxCandidateDiagnostics
            ? $" (weitere {list.Count - MaxCandidateDiagnostics} gleichnamige Parameter)" : "");
    }

    private static bool MatchesSpec(Parameter parameter, string sourceSpec, ForgeTypeId expected)
    {
        var actual = parameter.Definition.GetDataType();
        // Piping diameter/size are physical lengths in Revit, but their discipline-specific
        // specs differ from generic Length. No other spec is implicitly reinterpreted.
        return actual.Equals(expected) || (sourceSpec == "LENGTH"
            && (actual.Equals(SpecTypeId.PipeSize) || actual.Equals(SpecTypeId.PipeDimension)));
    }

    private static TakeoffValue ReadParameterValue(Document document, Parameter parameter, ParameterSource source, string unit)
    {
        var expected = source.Spec switch
        {
            "TEXT" => SpecTypeId.String.Text, "YESNO" => SpecTypeId.Boolean.YesNo,
            "INTEGER" => SpecTypeId.Int.Integer, "NUMBER" => SpecTypeId.Number,
            "LENGTH" => SpecTypeId.Length, "AREA" => SpecTypeId.Area, "VOLUME" => SpecTypeId.Volume,
            "ANGLE" => SpecTypeId.Angle, "MASS_DENSITY" => SpecTypeId.MassDensity,
            _ => throw Invalid(source, "Nicht unterstützte Parameterspezifikation.")
        };
        if (!MatchesSpec(parameter, source.Spec, expected))
            throw Invalid(source, "Parameterspezifikation im Modell weicht von der Vorlage ab. Modellspezifikation: " + ActualSpecs([parameter]));
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
            var actualSpec = parameter.Definition.GetDataType();
            ForgeTypeId projectUnit;
            try { projectUnit = document.GetUnits().GetFormatOptions(actualSpec).GetUnitTypeId(); }
            catch (Exception ex) when (IsCandidateDiagnosticException(ex))
            { throw Invalid(source, "Projekteinheit für die tatsächliche Revit-Spezifikation " + actualSpec.TypeId + " konnte nicht gelesen werden: " + FormatCandidateException(ex)); }
            var targetUnit = TargetUnitId(unit);
            if (!UnitUtils.IsValidUnit(actualSpec, projectUnit))
                throw Invalid(source, "Die Projekteinstellung liefert keine gültige Einheit für die tatsächliche Revit-Spezifikation " + actualSpec.TypeId + ".");
            if (!UnitUtils.IsValidUnit(actualSpec, targetUnit))
                throw Invalid(source, $"Zieleinheit '{unit}' passt nicht zur tatsächlichen Revit-Spezifikation {actualSpec.TypeId}.");

            // Revit stores measurable doubles in internal units. First interpret them in the
            // active project's configured unit for the actual spec, then convert that value
            // to the exact unit requested by this condition/quantity. Display rounding is not applied.
            var projectValue = UnitUtils.ConvertFromInternalUnits(value, projectUnit);
            value = UnitUtils.Convert(projectValue, projectUnit, targetUnit);
            // Revit 2026 has no kilometer unit identifier; convert through metres.
            if (unit == "km") value /= 1000d;
        }
        if (!double.IsFinite(value)) throw Invalid(source, "Parameterwert ist nicht endlich.");
        try { return new(Number: checked((decimal)value)); }
        catch (OverflowException ex) { throw new InvalidDataException(source.Name + ": Parameterwert überschreitet den Dezimalbereich.", ex); }
    }

    private static ForgeTypeId TargetUnitId(string unit) => unit switch
    {
        "mm" => UnitTypeId.Millimeters, "cm" => UnitTypeId.Centimeters, "m" or "km" => UnitTypeId.Meters,
        "mm2" => UnitTypeId.SquareMillimeters, "cm2" => UnitTypeId.SquareCentimeters, "m2" or "m²" => UnitTypeId.SquareMeters,
        "cm3" => UnitTypeId.CubicCentimeters, "m3" or "m³" => UnitTypeId.CubicMeters,
        "deg" => UnitTypeId.Degrees, "rad" => UnitTypeId.Radians, "kg/m3" => UnitTypeId.KilogramsPerCubicMeter,
        _ => throw new InvalidDataException("Leseneinheit wird nicht unterstützt: " + unit)
    };

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

    private static string DescribeCandidateValue(Document document, Parameter candidate, string? identity, int index,
        ParameterSource source, string unit)
    {
        var label = identity ?? SafeCandidateIdentity(candidate, index);
        var state = SafeCandidateValueState(candidate);
        try
        {
            var value = ReadParameterValue(document, candidate, source, unit);
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
