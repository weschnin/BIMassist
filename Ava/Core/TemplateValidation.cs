namespace BIMassist.Ava.Core;

public enum DiagnosticSeverity { Warning, Error }
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Path, string Message)
{
    public override string ToString() => $"{(Severity == DiagnosticSeverity.Error ? "Fehler" : "Hinweis")} [{Code}] {Path}: {Message}";
}

public static class TemplateValidator
{
    public static IReadOnlyList<Diagnostic> Validate(AvaTemplate template) => Validate(template, null);

    internal static IReadOnlyList<Diagnostic> Validate(AvaTemplate template, Type? builtInEnumType)
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
                if (source.SharedGuid != Guid.Empty) Error("PARAMETER_SOURCE", path, "Namens- / BuiltIn-Quelle darf keine gemeinsame GUID enthalten.");
                if (source.Kind == ParameterKind.BuiltIn)
                {
                    if (!BuiltInParameterCatalog.IsValid(source.BuiltInId, builtInEnumType))
                        Error("BUILTIN_SOURCE", path, "Unbekannte BuiltIn-Kennung: " + source.BuiltInId);
                    if (!BuiltInParameterCatalog.Specifications.Contains(source.Spec, StringComparer.Ordinal))
                        Error("BUILTIN_SOURCE", path, "Nicht unterstützte Spezifikation für BuiltIn-Kennung: " + source.Spec);
                    if (BuiltInParameterCatalog.TryGetKnownPolicy(source.BuiltInId, out var expectedSpec, out var expectedScope))
                    {
                        if (source.Scope != expectedScope || source.Spec != expectedSpec)
                            Error("BUILTIN_SOURCE", path, $"{source.BuiltInId} erfordert {expectedSpec} an {expectedScope}.");
                    }
                    else if (BuiltInParameterCatalog.IsValid(source.BuiltInId, builtInEnumType))
                        Warn("BUILTIN_POLICY_UNVERIFIED", path, "Für diese BuiltIn-Kennung sind Spezifikation und Geltung nicht vorab hinterlegt. Die Mengenermittlung prüft die tatsächliche Spezifikation und Verfügbarkeit im gewählten Instanz-/Typbereich; ob dieser Bereich der fachlich beabsichtigten Revit-Geltung entspricht, ist damit nicht bestätigt.");
                }
                else if (source.BuiltInId.Length > 0) Error("PARAMETER_SOURCE", path, "Namensquelle darf keine BuiltIn-ID enthalten.");
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
            for (int conditionIndex = 0; conditionIndex < rule.Conditions.Count; conditionIndex++)
            {
                var condition = rule.Conditions[conditionIndex];
                Source(condition.Source, path + " / Bedingung");
                if (!Enum.IsDefined(condition.Operator) || !Enum.IsDefined(condition.Operand)) Error("CONDITION_OPERATOR", path, "Unbekannter Bedingungsoperator / Operand.");
                if (!Enum.IsDefined(condition.JoinWithPrevious) || conditionIndex == 0 && condition.JoinWithPrevious != ConditionJoin.And)
                    Error("CONDITION_JOIN", path, "Unbekannte Verknüpfung oder erste Bedingung mit einer vorherigen Bedingung verknüpft.");
                bool numeric = UnitCatalog.ForSpec(condition.Source.Spec).Count > 0;
                if (numeric && condition.Operand == OperandKind.Numeric)
                {
                    if (condition.AllowCustomUnit
                        ? !UnitCatalog.AllowsQuantityUnit(condition.Source.Spec, condition.Unit, true)
                        : !UnitCatalog.Allows(condition.Source.Spec, condition.Unit))
                        Error("CONDITION_UNIT", path, "Explizite passende oder manuell deklarierte Einheit fehlt: " + condition.Source.Spec);
                    if (condition.AllowCustomUnit && !UnitCatalog.Allows(condition.Source.Spec, condition.Unit))
                        Warn("CONDITION_UNIT_UNVERIFIABLE", path, "NUMBER/INTEGER hat in Revit keine physikalische Einheit. Diese freie Einheit kann nicht verifiziert werden; eine passende Bedingung blockiert die Mengenermittlung bis zur Klärung der Quelle.");
                    if (condition.Source.Spec == "INTEGER" && decimal.Truncate(condition.Number) != condition.Number) Error("CONDITION_TYPE", path, "INTEGER erwartet eine Ganzzahl.");
                }
                else if ((condition.Source.Spec == "TEXT" && condition.Operand == OperandKind.Text) || (condition.Source.Spec == "YESNO" && condition.Operand == OperandKind.Boolean))
                {
                    if (condition.Operator is not (ConditionOperator.Equal or ConditionOperator.NotEqual)) Error("CONDITION_OPERATOR", path, "Text/Ja-Nein unterstützt nur gleich / ungleich.");
                    if (condition.Unit.Length > 0 || condition.AllowCustomUnit) Error("CONDITION_UNIT", path, "Text/Ja-Nein darf keine Einheit haben.");
                }
                else Error("CONDITION_TYPE", path, "Bedingungstyp / Spezifikation nicht unterstützt oder widersprüchlich: " + condition.Source.Spec);
            }
            foreach (var position in rule.Positions)
            {
                path = groupPath + " / Position " + position.Id;
                if (position.Id == Guid.Empty) Error("POSITION_ID", path, "Positions-ID fehlt.");
                var lv = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == position.LvItemId);
                if (lv == null) Error("LV_REFERENCE", path, "LV-ID nicht im Katalog: " + position.LvItemId);
                else path += " / OZ " + lv.Ordinal + " — " + lv.Text;
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
                            if (!UnitCatalog.AllowsQuantityUnit(source.Spec, quantity.Unit, quantity.AllowCustomUnit))
                                Error("QUANTITY_UNIT", path, quantity.AllowCustomUnit
                                    ? "Freie Einheit nur für NUMBER/INTEGER; Wert wird dabei ohne Umrechnung übernommen."
                                    : $"Mengeneinheit '{quantity.Unit}' passt nicht zur Parameterspezifikation {source.Spec}. Zulässig: {string.Join(", ", UnitCatalog.ForSpec(source.Spec))}. Für eine Formel mit Umrechnung bitte die Quelleneinheit und Ergebniseinheit getrennt festlegen.");

                        }
                    }
                    else if (quantity.Kind == QuantityKind.Constant)
                    {
                        if (quantity.AllowCustomUnit) Error("QUANTITY_MODE", path, "Freie Parameter-Einheit gilt nur für direkte NUMBER/INTEGER-Quellen.");
                        if (quantity.Source != null) Error("QUANTITY_MODE", path, "Konstante Menge darf keine Parameterquelle enthalten.");
                        if (quantity.ConstantValue is not { } value || value <= 0 || (quantity.Unit == "St" && decimal.Truncate(value) != value))
                            Error("QUANTITY_VALUE", path, "Konstante Menge muss positiv und für St ganzzahlig sein.");
                        if (string.IsNullOrWhiteSpace(quantity.Unit)) Error("QUANTITY_UNIT", path, "Konstante Menge benötigt eine nichtleere deklarierte Mengeneinheit.");
                    }
                    else if (quantity.Kind == QuantityKind.Formula)
                    {
                        if (quantity.AllowCustomUnit) Error("QUANTITY_MODE", path, "Freie Parameter-Einheit gilt nicht für Formeln; die Ergebniseinheit wird separat deklariert.");
                        if (quantity.Source != null || quantity.ConstantValue != null)
                            Error("QUANTITY_MODE", path, "Formelmenge darf keine direkte Quelle oder Konstante enthalten.");
                        try { QuantityFormula.Validate(quantity.Expression, quantity.Variables.Select(v => v.Name)); }
                        catch (System.IO.InvalidDataException ex) { Error("FORMULA", path, ex.Message); }
                        foreach (var variable in quantity.Variables)
                        {
                            var variablePath = path + " / Formelvariable " + variable.Name;
                            Source(variable.Source, variablePath);
                            if (!UnitCatalog.AllowsFormulaInput(variable.Source.Spec, variable.Unit))
                                Error("FORMULA", variablePath, "Numerische Parameterspezifikation und explizite passende Eingabeeinheit erforderlich. NUMBER/INTEGER-Formelparameter sind einheitenlos und dürfen nicht als St oder psch deklariert werden.");
                        }
                        if (string.IsNullOrWhiteSpace(quantity.Unit)) Error("QUANTITY_UNIT", path, "Formel benötigt eine nichtleere deklarierte Ergebniseinheit.");
                    }
                    else Error("QUANTITY_MODE", path, "Unbekannte Mengenart.");
                    if (lv != null && !string.Equals(quantity.Unit, lv.Unit, StringComparison.Ordinal))
                        Warn(quantity.AllowCustomUnit ? "LV_UNIT_MANUAL" : "LV_UNIT", path,
                            quantity.AllowCustomUnit
                                ? "Manuell deklarierte Einheit " + quantity.Unit + " weicht von der LV-Einheit " + lv.Unit + " ab. NUMBER/INTEGER-Rohwerte werden nicht umgerechnet; diese Mengenzeile kann so nicht exportiert werden."
                                : "Mengeneinheit " + quantity.Unit + " und LV-Einheit " + lv.Unit + " weichen ab. Kompatible Einheiten werden bei der Mengenermittlung umgerechnet; nicht umrechenbare Einheiten blockieren den Export.");
                }
                else Warn("QUANTITY_MISSING", path, "Für diese LV-Position ist noch keine Mengenart (Parameter, feste Zahl oder Formel) festgelegt; Zuordnung ist ein Entwurf.");
            }
        }
        static bool ProvablyDisjoint(MappingRule first, MappingRule second)
        {
            // OR alternatives can satisfy a rule through a different branch; stay conservative.
            if (first.Conditions.Any(c => c.JoinWithPrevious == ConditionJoin.Or)
                || second.Conditions.Any(c => c.JoinWithPrevious == ConditionJoin.Or)) return false;
            return first.Conditions.Any(a =>
            a.Operator == ConditionOperator.Equal && a.Operand == OperandKind.Numeric && UnitCatalog.Allows(a.Source.Spec, a.Unit)
            && second.Conditions.Any(b => b.Operator == ConditionOperator.Equal && b.Operand == OperandKind.Numeric
                && ConditionNumberComparison.ProvablyDifferent(a.Number, b.Number, a.Source.Spec)
                && a.Unit == b.Unit && a.Source.Kind == b.Source.Kind
                && a.Source.Scope == b.Source.Scope && a.Source.SharedGuid == b.Source.SharedGuid
                && a.Source.Name == b.Source.Name && a.Source.Spec == b.Source.Spec
                && a.Source.BuiltInId == b.Source.BuiltInId));
        }
        foreach (var group in template.Mappings.GroupBy(m => (m.BgkKey, m.TypeNumber)).Where(g => g.Count() > 1))
        {
            var rules = group.ToArray();
            if (rules.Where((rule, index) => rules.Skip(index + 1).Any(other => !ProvablyDisjoint(rule, other))).Any())
                Warn("MULTIPLE_RULES", group.Key.ToString(),
                    "Mehrere Regeln können dasselbe Element erfüllen. Alle passenden Leistungspositionen werden dann addiert; es gibt keine Priorität oder Else-Regel. Bedingungen und gewünschte Mehrfachleistungen prüfen.");
        }
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
            new("Lesbarkeit", "Alle erforderlichen Parameter", "GUID bzw. eindeutigen Namen, Spezifikation und Instanz-/Typ-Geltung prüfen. Formelgesteuert / schreibgeschützt ist zum Lesen zulässig; Definition im TXT beweist keine Modellbindung."),
            new("Abdeckung", "Alle identifizierten Elemente im Prüfumfang", "Fehlende Zuordnung und mehrere passende Regeln getrennt prüfen. Keine automatische Zuordnung aus ähnlichen Nummern; keine Priorität oder implizite Else-Regel.")
        };
        foreach (var rule in template.Mappings)
        {
            var groupContext = $"{rule.BgkKey} / {rule.TypeNumber} (Gruppe {rule.Id})";
            var conditionSemantics = rule.Conditions.Any(condition => condition.JoinWithPrevious == ConditionJoin.Or)
                ? "UND/ODER" : "UND";
            foreach (var condition in rule.Conditions)
                checks.Add(new("Bedingung", groupContext, condition + ". Quelle: " + condition.Source
                    + ". UND bindet stärker als ODER. Fehlender / ungültiger Eingang = Fehler (nicht auswertbar), auch wenn ein anderer Zweig zutrifft."));
            foreach (var position in rule.Positions)
            {
                var lv = template.LvCatalog.Entries.FirstOrDefault(l => l.Id == position.LvItemId);
                var context = $"{groupContext} → {lv?.Ordinal ?? position.LvItemId} (Position {position.Id})";
                checks.Add(new("Zuordnung", context, "LV-ID, OZ und Einheit gegen Snapshot prüfen: " + position.LvItemId + " | " + lv?.Unit));
                var requirement = position.Quantity switch
                {
                    { Kind: QuantityKind.Constant } quantity => quantity + "; fester Wert, kein Mengenparameter erforderlich. Eigene Mengeneinheit " + quantity.Unit + "; LV-Katalogeinheit " + lv?.Unit + ". Kompatible Mengen werden zur LV-Einheit umgerechnet, andere blockiert. Bedingungen benötigen weiterhin ihre Parameterquellen.",
                    { Kind: QuantityKind.Formula } quantity => "Formel: " + quantity.Expression + "; deklarierte Ergebniseinheit " + quantity.Unit + "; LV-Katalogeinheit " + lv?.Unit + ". Eingänge: " +
                        string.Join("; ", quantity.Variables.Select(v => v.Name + " = Lesen: " + v.Source + "; Eingabeeinheit " + v.Unit)) +
                        ". Revit-Eingänge werden in ihren deklarierten Einheiten gelesen; parametrisierte Formeln rechnen physikalische Eingänge auf Basiseinheiten um und prüfen die Ergebnisdimension. Zahlenkonstanten in einer Addition gelten in der Ergebniseinheit; parameterfreie Formeln deklarieren ihre Einheit ausdrücklich. Kompatible Ergebnisse werden in die LV-Einheit umgerechnet, sonst blockiert. Fehlende / ungültige Eingänge sind Fehler, kein Fallback.",
                    { AllowCustomUnit: true } quantity => "Lesen: " + quantity.Source + "; manuell interpretierter Rohwert als " + quantity.Unit + "; nur bei exakt gleicher LV-Katalogeinheit " + lv?.Unit + " und ohne Umrechnung exportierbar. Die Einheit stammt aus der Vorlage, nicht aus Revit. Fehlende / ungültige Werte melden; Null/Negativwerte fachlich prüfen.",
                    { } quantity => "Lesen: " + quantity.Source + "; deklarierte Leseeinheit " + quantity.Unit + "; LV-Katalogeinheit " + lv?.Unit + ". Revit-Spezifikation wird geprüft, interne Revit-Einheit in die Leseeinheit und kompatible Mengeneinheit in die LV-Einheit umgerechnet; sonst blockiert der Export. Null/Negativwerte fachlich prüfen.",
                    null => "Menge nicht definiert; Zuordnung ist ein Entwurf."
                };
                checks.Add(new("Menge", context + " — nur bei erfüllten " + conditionSemantics + "-Bedingungen", requirement));
            }
        }
        return checks;
    }
}
