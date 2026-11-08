namespace BIMassist.Ava.Core;

public enum DiagnosticSeverity { Warning, Error }
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Path, string Message)
{
    public override string ToString() => $"{(Severity == DiagnosticSeverity.Error ? "Fehler" : "Hinweis")} [{Code}] {Path}: {Message}";
}

/// <summary>Unit declarations only; no Revit internal-unit conversion or model evaluation is performed.</summary>
public static class UnitCatalog
{
    private static readonly Dictionary<string, string[]> Units = new(StringComparer.Ordinal)
    {
        ["LENGTH"] = ["mm", "cm", "m", "km"], ["AREA"] = ["mm2", "cm2", "m2", "m²"],
        ["VOLUME"] = ["cm3", "m3", "m³"], ["ANGLE"] = ["deg", "rad"],
        ["NUMBER"] = ["1", "St", "psch"], ["INTEGER"] = ["1", "St", "psch"],
        ["MASS_DENSITY"] = ["kg/m3"]
    };
    public static IReadOnlyList<string> ForSpec(string spec) => Units.TryGetValue(spec, out var units) ? units : [];
    public static bool Allows(string spec, string unit) => ForSpec(spec).Contains(unit, StringComparer.Ordinal);
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
}

public static class TemplateValidator
{
    public static IReadOnlyList<Diagnostic> Validate(AvaTemplate template)
    {
        var result = new List<Diagnostic>();
        void Error(string code, string path, string message) => result.Add(new(DiagnosticSeverity.Error, code, path, message));
        void Warn(string code, string path, string message) => result.Add(new(DiagnosticSeverity.Warning, code, path, message));
        void Unique(IEnumerable<string> values, string path)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values) if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) Error("DUPLICATE_KEY", path, "Leerer oder doppelter Schlüssel: " + value);
        }
        void Source(ParameterSource source, string path)
        {
            if (!Enum.IsDefined(source.Kind) || !Enum.IsDefined(source.Scope)) Error("PARAMETER_SOURCE", path, "Unbekannte Quelle oder Geltung.");
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Spec)) Error("PARAMETER_SOURCE", path, "Name und Spezifikation sind erforderlich.");
            if (source.Kind == ParameterKind.SharedGuid)
            {
                var definition = template.ParameterCatalog.Entries.FirstOrDefault(p => p.Guid == source.SharedGuid);
                if (source.SharedGuid == Guid.Empty || definition == null) Error("PARAMETER_REFERENCE", path, "GUID nicht im Parameterkatalog: " + source.SharedGuid);
                else
                {
                    if (definition.Spec != source.Spec) Error("PARAMETER_SPEC", path, "Spezifikation weicht von der GUID-Definition ab.");
                    if (definition.Name != source.Name) Error("PARAMETER_NAME", path, "Name weicht von der GUID-Definition ab; kein Namens-Fallback.");
                }
                if (source.BuiltInId.Length > 0) Error("PARAMETER_SOURCE", path, "Gemeinsame Quelle darf keine BuiltIn-ID enthalten.");
            }
            else
            {
                if (source.SharedGuid != Guid.Empty) Error("PARAMETER_SOURCE", path, "Lokale / BuiltIn-Quelle darf keine gemeinsame GUID enthalten.");
                if (source.Kind == ParameterKind.BuiltIn)
                {
                    if (source.BuiltInId is not ("ASSEMBLY_CODE" or "ALL_MODEL_TYPE_COMMENTS" or "ALL_MODEL_DESCRIPTION") || source.Scope != ParameterScope.Type || source.Spec != "TEXT")
                        Error("BUILTIN_SOURCE", path, "Unterstützt: ASSEMBLY_CODE, ALL_MODEL_TYPE_COMMENTS, ALL_MODEL_DESCRIPTION, jeweils TEXT am Typ.");
                }
                else if (source.BuiltInId.Length > 0) Error("PARAMETER_SOURCE", path, "Lokale Quelle darf keine BuiltIn-ID enthalten.");
            }
        }
        if (template.SchemaVersion != 3 || template.ProfileId == Guid.Empty) Error("IDENTITY", "Profil", "Schema oder Profil-ID ungültig.");
        if (string.IsNullOrWhiteSpace(template.Name) || string.IsNullOrWhiteSpace(template.Version)) Error("PROFILE_NAME", "Profil", "Name und Version sind erforderlich.");
        if (string.IsNullOrWhiteSpace(template.ReviewScope)) Warn("REVIEW_SCOPE", "Profil", "Unabhängigen Prüfumfang / Kategorien festlegen. BGK-Filter allein finden keine unmarkierten Elemente.");
        Source(template.BgkIdentification, "BGK-Identifikation"); Source(template.TypeIdentification, "Typ-Identifikation");
        if (template.BgkIdentification.Spec != "TEXT" || template.TypeIdentification.Spec != "TEXT") Error("IDENTIFICATION_SPEC", "Identifikation", "BGK und Typnummer benötigen TEXT, damit führende Nullen erhalten bleiben.");
        Unique(template.BgkCatalog.Entries.Select(b => b.Key), "BGK-Katalog");
        foreach (var b in template.BgkCatalog.Entries) Unique(b.Types.Select(t => t.Number), b.Key);
        Unique(template.LvCatalog.Entries.Select(l => l.Id), "LV-Katalog");
        Unique(template.LvCatalog.Entries.Select(l => l.BoqId + "\0" + l.Ordinal), "LV-OZ");
        foreach (var lv in template.LvCatalog.Entries)
            if (lv.NumberParts.Count == 0 || lv.NumberParts.Any(string.IsNullOrWhiteSpace) || string.Join(".", lv.NumberParts) != lv.Ordinal || string.IsNullOrWhiteSpace(lv.BoqId)) Error("LV_SNAPSHOT", lv.Id, "OZ-Teile oder BoQ-ID sind ungültig.");
        Unique(template.ParameterCatalog.Entries.Select(p => p.Guid.ToString()), "Parameterkatalog");
        foreach (var p in template.ParameterCatalog.Entries) if (p.Guid == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Spec)) Error("PARAMETER_DEFINITION", "Parameterkatalog", "Unvollständige Definition.");
        Unique(template.Mappings.Select(m => m.Id.ToString()), "Regeln");
        Unique(template.Mappings.SelectMany(m => m.Positions).Select(p => p.Id.ToString()), "Positionen");
        foreach (var rule in template.Mappings)
        {
            string groupPath = rule.BgkKey + " / " + rule.TypeNumber + " [" + rule.Id + "]";
            string path = groupPath;
            if (rule.Positions.Count == 0) Error("POSITIONS_EMPTY", path, "Mindestens eine LV-Position ist erforderlich.");
            if (rule.Id == Guid.Empty) Error("RULE_ID", path, "Regel-ID fehlt.");
            var bgk = template.BgkCatalog.Entries.FirstOrDefault(b => b.Key == rule.BgkKey);
            if (bgk == null) Error("BGK_REFERENCE", path, "BGK nicht im Katalog.");
            else if (!bgk.Types.Any(t => t.Number == rule.TypeNumber)) Error("TYPE_REFERENCE", path, "Typnummer nicht in dieser BGK.");
            foreach (var condition in rule.Conditions)
            {
                Source(condition.Source, path + " / Bedingung");
                if (!Enum.IsDefined(condition.Operator) || !Enum.IsDefined(condition.Operand)) Error("CONDITION_OPERATOR", path, "Unbekannter Bedingungsoperator / Operand.");
                bool numeric = UnitCatalog.ForSpec(condition.Source.Spec).Count > 0;
                if (numeric && condition.Operand == OperandKind.Numeric)
                {
                    if (!UnitCatalog.Allows(condition.Source.Spec, condition.Unit)) Error("CONDITION_UNIT", path, "Explizite passende Einheit fehlt: " + condition.Source.Spec);
                    if (condition.Source.Spec == "INTEGER" && decimal.Truncate(condition.Number) != condition.Number) Error("CONDITION_TYPE", path, "INTEGER erwartet eine Ganzzahl.");
                }
                else if ((condition.Source.Spec == "TEXT" && condition.Operand == OperandKind.Text) || (condition.Source.Spec == "YESNO" && condition.Operand == OperandKind.Boolean))
                {
                    if (condition.Operator is not (ConditionOperator.Equal or ConditionOperator.NotEqual)) Error("CONDITION_OPERATOR", path, "Text/Ja-Nein unterstützt nur gleich / ungleich.");
                    if (condition.Unit.Length > 0) Error("CONDITION_UNIT", path, "Text/Ja-Nein darf keine Einheit haben.");
                }
                else Error("CONDITION_TYPE", path, "Bedingungstyp / Spezifikation nicht unterstützt oder widersprüchlich: " + condition.Source.Spec);
            }
            foreach (var position in rule.Positions)
            {
                path = groupPath + " / Position " + position.Id;
                if (position.Id == Guid.Empty) Error("POSITION_ID", path, "Positions-ID fehlt.");
                var lv = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == position.LvItemId);
                if (lv == null) Error("LV_REFERENCE", path, "LV-ID nicht im Katalog: " + position.LvItemId);
                if (position.Quantity is { } quantity)
                {
                    if (quantity.Kind != QuantityKind.Formula && (quantity.Expression.Length > 0 || quantity.Variables.Count > 0))
                        Error("QUANTITY_MODE", path, "Nur Formelmengen dürfen Ausdruck und Variablen enthalten.");
                    if (quantity.Kind == QuantityKind.Parameter)
                    {
                        if (quantity.Source is not { } source || quantity.ConstantValue != null)
                            Error("QUANTITY_MODE", path, "Parametermenge benötigt eine Quelle und darf keinen konstanten Wert enthalten.");
                        else
                        {
                            Source(source, path + " / Menge");
                            if (!UnitCatalog.Allows(source.Spec, quantity.Unit)) Error("QUANTITY_UNIT", path, "Mengeneinheit passt nicht zur Parameterspezifikation.");
                        }
                    }
                    else if (quantity.Kind == QuantityKind.Constant)
                    {
                        if (quantity.Source != null) Error("QUANTITY_MODE", path, "Konstante Menge darf keine Parameterquelle enthalten.");
                        if (quantity.ConstantValue is not { } value || value <= 0 || (quantity.Unit == "St" && decimal.Truncate(value) != value))
                            Error("QUANTITY_VALUE", path, "Konstante Menge muss positiv und für St ganzzahlig sein.");
                        if (!UnitCatalog.Compatible(quantity.Unit, quantity.Unit)) Error("QUANTITY_UNIT", path, "Konstante Menge benötigt eine unterstützte Einheit.");
                    }
                    else if (quantity.Kind == QuantityKind.Formula)
                    {
                        if (quantity.Source != null || quantity.ConstantValue != null)
                            Error("QUANTITY_MODE", path, "Formelmenge darf keine direkte Quelle oder Konstante enthalten.");
                        try { QuantityFormula.Validate(quantity.Expression, quantity.Variables.Select(v => v.Name)); }
                        catch (System.IO.InvalidDataException ex) { Error("FORMULA", path, ex.Message); }
                        foreach (var variable in quantity.Variables)
                        {
                            var variablePath = path + " / Formelvariable " + variable.Name;
                            Source(variable.Source, variablePath);
                            if (!UnitCatalog.Allows(variable.Source.Spec, variable.Unit))
                                Error("FORMULA", variablePath, "Numerische Parameterspezifikation und explizite passende Eingabeeinheit erforderlich.");
                        }
                        if (!UnitCatalog.Compatible(quantity.Unit, quantity.Unit)) Error("QUANTITY_UNIT", path, "Formel benötigt eine unterstützte deklarierte Ergebniseinheit.");
                    }
                    else Error("QUANTITY_MODE", path, "Unbekannte Mengenart.");
                    if (lv != null && !UnitCatalog.Compatible(quantity.Unit, lv.Unit)) Error("LV_UNIT", path, "Mengeneinheit " + quantity.Unit + " passt nicht zur unterstützten LV-Einheit " + lv.Unit + ". Keine stille Umrechnung.");
                }
                else Warn("QUANTITY_MISSING", path, "Keine Mengenquelle definiert; Zuordnung ist ein Entwurf.");
            }
        }
        foreach (var group in template.Mappings.GroupBy(m => (m.BgkKey, m.TypeNumber)).Where(g => g.Count() > 1))
            Warn("MULTIPLE_RULES", group.Key.ToString(), "Mehrere Zuordnungen: keine Priorität / Else-Regel. Überlappung und gewünschte Mehrfachleistung fachlich prüfen.");
        if (template.Mappings.Count == 0) Warn("NO_MAPPINGS", "Profil", "Noch keine Zuordnungen.");
        return result;
    }
}

public sealed record ChecklistEntry(string Kind, string AppliesTo, string Requirement);
public static class ChecklistBuilder
{
    public static IReadOnlyList<ChecklistEntry> Build(AvaTemplate template)
    {
        var checks = new List<ChecklistEntry>
        {
            new("Prüfumfang", "Unabhängig von vorhandenen BGK-Werten", "Prüfumfang / Kategorien: " + (string.IsNullOrWhiteSpace(template.ReviewScope) ? "NOCH FESTZULEGEN" : template.ReviewScope) + ". Auch Elemente ohne Kennzeichnung einbeziehen; Beschreibung ist noch kein ausführbarer Modellfilter."),
            new("Identifikation", "Alle Elemente im unabhängigen Prüfumfang", "BGK vorhanden, nicht leer und im Katalog: " + template.BgkIdentification),
            new("Identifikation", "Alle Elemente im unabhängigen Prüfumfang", "Typnummer vorhanden, nicht leer und zur BGK passend: " + template.TypeIdentification),
            new("Lesbarkeit", "Alle erforderlichen Parameter", "GUID bzw. explizite lokale Quelle, Spezifikation und Instanz-/Typ-Geltung prüfen. Formelgesteuert / schreibgeschützt ist zum Lesen zulässig; Definition im TXT beweist keine Modellbindung."),
            new("Abdeckung", "Alle identifizierten Elemente im Prüfumfang", "Fehlende Zuordnung und mehrere passende Regeln getrennt prüfen. Keine automatische Zuordnung aus ähnlichen Nummern; keine Priorität oder implizite Else-Regel.")
        };
        foreach (var rule in template.Mappings)
        {
            var groupContext = $"{rule.BgkKey} / {rule.TypeNumber} (Gruppe {rule.Id})";
            foreach (var condition in rule.Conditions)
                checks.Add(new("Bedingung", groupContext, "UND: " + condition + ". Quelle: " + condition.Source + ". Fehlender / ungültiger Eingang = nicht auswertbar / Fehler, nicht false; kein Fallback."));
            foreach (var position in rule.Positions)
            {
                var lv = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == position.LvItemId);
                var context = $"{groupContext} → {lv?.Ordinal ?? position.LvItemId} (Position {position.Id})";
                checks.Add(new("Zuordnung", context, "LV-ID, OZ und Einheit gegen Snapshot prüfen: " + position.LvItemId + " | " + lv?.Unit));
                var requirement = position.Quantity switch
                {
                    { Kind: QuantityKind.Constant } quantity => quantity + "; fester Wert, kein Mengenparameter erforderlich. Deklarierte Einheit " + quantity.Unit + " → LV " + lv?.Unit + ". Bedingungen benötigen weiterhin ihre Parameterquellen. Modellauswertung, Umrechnung, Rundung und Aggregation sind noch nicht implementiert.",
                    { Kind: QuantityKind.Formula } quantity => "Formel: " + quantity.Expression + "; deklarierte Ergebniseinheit " + quantity.Unit + " → LV " + lv?.Unit + ". Eingänge: " +
                        string.Join("; ", quantity.Variables.Select(v => v.Name + " = Lesen: " + v.Source + "; Eingabeeinheit " + v.Unit)) +
                        ". Werte beziehen sich auf die expliziten Eingabeeinheiten; keine automatische Einheitenumrechnung oder Dimensionsableitung. Fehlende / ungültige Eingänge sind Fehler, kein Fallback. Modellauswertung, Umrechnung, Rundung und Aggregation sind noch nicht implementiert.",
                    { } quantity => "Lesen: " + quantity.Source + "; deklarierte Einheit " + quantity.Unit + " → LV " + lv?.Unit + ". Fehlende / ungültige Werte melden; Null/Negativwerte fachlich prüfen. Umrechnung, Rundung und Aggregation sind noch nicht implementiert.",
                    null => "Menge nicht definiert; Zuordnung ist ein Entwurf."
                };
                checks.Add(new("Menge", context + " — nur bei erfüllten UND-Bedingungen", requirement));
            }
        }
        return checks;
    }
}
