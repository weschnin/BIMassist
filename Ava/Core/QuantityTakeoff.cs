using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BIMassist.Ava.Core;

public sealed record TakeoffValue(decimal? Number = null, string? Text = null, bool? Boolean = null);
public sealed record TakeoffElement(string Id, Func<ParameterSource, string, TakeoffValue> Read, string SourceIdentity = "")
{
    public string ElementUniqueId { get; init; } = "";
    public string FamilyName { get; init; } = "";
    public string TypeName { get; init; } = "";
}
public sealed record TakeoffIssue(string ElementId, string Message);
public sealed record TakeoffSkip(string ElementId, string Message);
public sealed record TakeoffLine(string LvItemId, decimal Value, string Unit, string Explanation, string ElementId = "",
    string? FormulaExpression = null, string? CalculationApproach = null,
    string FormulaWithParameters = "", string FormulaWithValues = "", string ElementUniqueId = "",
    string FamilyName = "", string TypeName = "");
/// <summary>Rows are ordered by input element, mapping, then position; Errors must block the entire export.
/// Unmapped counts marked elements without an exact BGK/type rule, not elements whose conditions are false.</summary>
public sealed record TakeoffResult(IReadOnlyList<TakeoffLine> Rows, IReadOnlyList<TakeoffIssue> Errors,
    int Examined, int Unmarked, int Unmapped, IReadOnlyList<TakeoffSkip> Skipped);

/// <summary>Read-only, decimal takeoff. Any Errors must block export of the entire result.</summary>
public static class QuantityTakeoff
{
    private const int MaxSkipDetailLength = 600;
    private const int MaxSkipDetails = 100;
    public static TakeoffResult Calculate(AvaTemplate template, IEnumerable<TakeoffElement> elements)
    {
        TemplateEditor.EnsureValid(template);
        if (template.Mappings.Count == 0) throw new InvalidDataException("At least one mapping is required for takeoff.");
        var rows = new List<TakeoffLine>();
        var errors = new List<TakeoffIssue>();
        var skipped = new List<TakeoffSkip>();
        void Skip(TakeoffElement element, string reason)
        {
            if (skipped.Count < MaxSkipDetails)
            {
                var message = $"Element {element.Id}: {reason}";
                if (message.Length > MaxSkipDetailLength) message = message[..(MaxSkipDetailLength - 1)] + "…";
                skipped.Add(new(element.Id, message));
            }
        }
        var elementIds = new HashSet<string>(StringComparer.Ordinal);
        int examined = 0, unmarked = 0, unmapped = 0;
        foreach (var element in elements)
        {
            if (string.IsNullOrWhiteSpace(element.Id) || !elementIds.Add(element.Id))
                throw new InvalidDataException("Empty or duplicate element identifier: " + element.Id);
            examined++;
            var snapshot = new Dictionary<ReadKey, TakeoffValue>();
            var failedReads = new Dictionary<ReadKey, InvalidDataException>();
            TakeoffValue Read(ParameterSource source, string unit)
            {
                var key = new ReadKey(source.Kind, source.Scope, source.SharedGuid, source.Name, source.Spec, source.BuiltInId, unit);
                if (failedReads.TryGetValue(key, out var failure)) throw failure;
                if (!snapshot.TryGetValue(key, out var value))
                {
                    try { value = element.Read(source, unit); }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                    {
                        failure = new InvalidDataException($"Parameter {source}; requested unit '{unit}': {ex.Message}", ex);
                        failedReads.Add(key, failure);
                        throw failure;
                    }
                    snapshot.Add(key, value);
                }
                return value;
            }
            string? bgk, type;
            try
            {
                bgk = Identification(Read(template.BgkIdentification, ""), template.BgkIdentification);
                type = Identification(Read(template.TypeIdentification, ""), template.TypeIdentification);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                errors.Add(new(element.Id, $"Element {element.Id}; Identification: {ex.Message}"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(bgk) || string.IsNullOrWhiteSpace(type))
            {
                unmarked++; Skip(element, $"BGK '{bgk}' / Typ '{type}' unvollständig (Quelle: {element.SourceIdentity})."); continue;
            }
            var rules = template.Mappings.Where(r => string.Equals(r.BgkKey, bgk, StringComparison.Ordinal)
                && string.Equals(r.TypeNumber, type, StringComparison.Ordinal)).ToList();
            if (rules.Count == 0)
            {
                // BIMassist's BGK assignment stores "<key> <description>" in Revit's
                // Assembly Code field. Resolve only an exact, unique catalog label;
                // do not accept arbitrary prefixes or trim/normalize model values.
                var catalogMatches = template.BgkCatalog.Entries
                    .Where(entry => string.Equals(bgk, entry.Key + " " + entry.Description, StringComparison.Ordinal))
                    .Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Take(2).ToList();
                if (catalogMatches.Count == 1)
                    rules = template.Mappings.Where(r => string.Equals(r.BgkKey, catalogMatches[0], StringComparison.Ordinal)
                        && string.Equals(r.TypeNumber, type, StringComparison.Ordinal)).ToList();
            }
            if (rules.Count == 0) { unmapped++; Skip(element, $"BGK '{bgk}' / Typ '{type}': keine exakte Zuordnung."); continue; }
            var conditionReasons = new List<string>();
            int rowsBefore = rows.Count, errorsBefore = errors.Count;
            foreach (var rule in rules)
            {
                bool conditionFailed = false;
                var matches = new List<bool>(rule.Conditions.Count);
                var failedConditions = new List<string>();
                foreach (var condition in rule.Conditions)
                {
                    // Read every input, even after a match or read failure: missing OR/AND inputs are errors, not false.
                    try
                    {
                        if (condition.AllowCustomUnit && !UnitCatalog.Allows(condition.Source.Spec, condition.Unit))
                            throw new InvalidDataException($"Einheit '{condition.Unit}' für {condition.Source.Spec} in Revit nicht verifizierbar; einen Parameter mit physikalischer Spezifikation verwenden.");
                        var actual = Read(condition.Source, condition.AllowCustomUnit ? "1" : condition.Unit);
                        var matched = Matches(condition, actual);
                        matches.Add(matched);
                        if (!matched) failedConditions.Add($"{condition.Source.Name} {condition.Operator} "
                            + (condition.Operand == OperandKind.Numeric ? condition.Number.ToString(CultureInfo.InvariantCulture) + " " + condition.Unit
                                : condition.Operand == OperandKind.Text ? condition.Text : condition.Boolean.ToString())
                            + "; Modellwert " + (actual.Number is { } number
                                ? number.ToString(CultureInfo.InvariantCulture) + " " + condition.Unit
                                : actual.Text ?? actual.Boolean?.ToString() ?? "leer"));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                    {
                        errors.Add(new(element.Id, $"Element {element.Id}; {RuleContext(template, rule)}; Condition {condition}:\n{ex.Message}"));
                        matches.Add(false);
                        conditionFailed = true;
                    }
                }
                bool applies = !conditionFailed && MatchesConditionExpression(rule.Conditions, matches);
                if (!applies)
                {
                    if (!conditionFailed) conditionReasons.Add($"{RuleContext(template, rule)}: " + string.Join("; ", failedConditions.Take(4)));
                    continue;
                }
                // Matching rules and sibling positions are intentionally independent and additive.
                foreach (var position in rule.Positions)
                {
                    try
                    {
                        var lv = template.LvCatalog.Entries.Single(item => item.Id == position.LvItemId);
                        rows.Add(CalculateLine(element.Id, element.SourceIdentity, rule.Id, position, lv.Unit, Read,
                            element.ElementUniqueId, element.FamilyName, element.TypeName));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                    {
                        errors.Add(new(element.Id, $"Element {element.Id}; {RuleContext(template, rule, position)}; Position {position.Id}:\n{ex.Message}"));
                    }
                }
            }
            if (rows.Count == rowsBefore && errors.Count == errorsBefore && conditionReasons.Count > 0)
                Skip(element, $"BGK '{bgk}' / Typ '{type}' zugeordnet, aber keine Bedingung erfüllt. "
                    + string.Join(" | ", conditionReasons.Take(3)));
        }
        return new(rows.AsReadOnly(), errors.AsReadOnly(), examined, unmarked, unmapped, skipped.AsReadOnly());
    }

    internal static bool MatchesConditionExpression(IReadOnlyList<RuleCondition> conditions, IReadOnlyList<bool> matches)
    {
        if (conditions.Count != matches.Count) throw new ArgumentException("Every condition needs exactly one evaluated result.");
        if (conditions.Count == 0) return true;
        bool anyGroup = false;
        bool currentAndGroup = matches[0];
        for (int index = 1; index < conditions.Count; index++)
        {
            if (conditions[index].JoinWithPrevious == ConditionJoin.Or)
            {
                anyGroup |= currentAndGroup;
                currentAndGroup = matches[index];
            }
            else currentAndGroup &= matches[index];
        }
        return anyGroup | currentAndGroup;
    }

    private static string RuleContext(AvaTemplate template, MappingRule rule, MappingPosition? position = null)
    {
        var ids = (position == null ? rule.Positions : new[] { position }.AsEnumerable())
            .Select(p => p.LvItemId).Distinct(StringComparer.Ordinal).ToList();
        var ordinals = ids.Take(6).Select(id => template.LvCatalog.Entries.Single(lv => lv.Id == id).Ordinal);
        return $"BGK {rule.BgkKey} / Typ {rule.TypeNumber}; LV {string.Join(", ", ordinals)}"
            + (ids.Count > 6 ? $" (+{ids.Count - 6} weitere)" : "") + $"; Rule {rule.Id}";
    }

    private static TakeoffLine CalculateLine(string elementId, string sourceIdentity, Guid ruleId, MappingPosition position, string lvUnit,
        Func<ParameterSource, string, TakeoffValue> read, string elementUniqueId, string familyName, string typeName)
    {
        var quantity = position.Quantity ?? throw new InvalidDataException("Quantity is not defined.");
        decimal value;
        string expression;
        string? formulaExpression = null, calculationApproach = null;
        string formulaWithParameters, formulaWithValues;
        if (quantity.Kind == QuantityKind.Formula)
        {
            var values = new Dictionary<string, (decimal Value, string Unit, string Spec)>(StringComparer.Ordinal);
            var inputs = new List<string>();
            foreach (var variable in quantity.Variables)
            {
                var input = Numeric(read(variable.Source, variable.Unit), variable.Source);
                values.Add(variable.Name, (input, variable.Unit, variable.Source.Spec));
                inputs.Add($"{variable.Name}={input.ToString(CultureInfo.InvariantCulture)} {variable.Unit} [Quelle: {variable.Source}]");
            }
            value = values.Count == 0
                ? QuantityFormula.Evaluate(quantity.Expression, new Dictionary<string, decimal>(StringComparer.Ordinal))
                : QuantityFormula.EvaluateWithUnits(quantity.Expression, values, quantity.Unit);
            expression = quantity.Expression + " (" + string.Join("; ", inputs) + ")";
            formulaExpression = quantity.Expression;
            calculationApproach = QuantityFormula.CreateReb91Approach(quantity.Expression, values, quantity.Unit, lvUnit);
            formulaWithParameters = quantity.Expression + " [" + string.Join("; ", quantity.Variables.Select(variable =>
                $"{variable.Name} ← {variable.Source} ({variable.Unit})")) + "]";
            formulaWithValues = calculationApproach;
        }
        else
        {
            if (quantity.Kind == QuantityKind.Parameter && quantity.AllowCustomUnit
                && quantity.Source!.Spec is not ("NUMBER" or "INTEGER"))
                throw new InvalidDataException("Eine manuell interpretierte Einheit ist nur für direkte NUMBER/INTEGER-Parameter zulässig.");
            value = quantity.Kind == QuantityKind.Constant ? quantity.ConstantValue!.Value
                : Numeric(read(quantity.Source!, quantity.AllowCustomUnit ? "1" : quantity.Unit), quantity.Source!);
            expression = quantity.Kind == QuantityKind.Constant ? "constant" : quantity.Source!.ToString()
                + (quantity.AllowCustomUnit ? "; explizit manuelle Einheit, Rohwert ohne Umrechnung" : "");
            formulaWithParameters = quantity.Kind == QuantityKind.Constant
                ? "Konstante " + quantity.ConstantValue!.Value.ToString(CultureInfo.InvariantCulture) + " " + quantity.Unit
                : quantity.Source!.ToString() + " (" + quantity.Unit + ")";
            formulaWithValues = value.ToString(CultureInfo.InvariantCulture) + " " + quantity.Unit;
        }
        var sourceValue = value;
        if (quantity.AllowCustomUnit)
        {
            if (!string.Equals(quantity.Unit, lvUnit, StringComparison.Ordinal))
                throw new InvalidDataException($"Manuell deklarierte Einheit '{quantity.Unit}' kann nicht in die LV-Einheit '{lvUnit}' umgerechnet werden. Die Zahl bleibt unverändert; Quell- und LV-Einheit müssen exakt übereinstimmen.");
        }
        else value = UnitCatalog.Convert(value, quantity.Unit, lvUnit);
        if (quantity.Kind != QuantityKind.Formula && !string.Equals(quantity.Unit, lvUnit, StringComparison.Ordinal))
            formulaWithValues += " → " + value.ToString(CultureInfo.InvariantCulture) + " " + lvUnit;
        return new(position.LvItemId, value, lvUnit,
            $"Element {elementId}; " + (string.IsNullOrEmpty(sourceIdentity) ? "" : sourceIdentity + "; ") +
            $"Rule {ruleId}; Position {position.Id}; {expression} = {sourceValue.ToString(CultureInfo.InvariantCulture)} {quantity.Unit}"
            + (quantity.AllowCustomUnit ? " (Rohwert ohne Umrechnung; Einheit manuell deklariert)" : "")
            + (quantity.Unit == lvUnit ? "" : $" → {value.ToString(CultureInfo.InvariantCulture)} {lvUnit} (LV-Einheit)"), elementId,
            formulaExpression, calculationApproach, formulaWithParameters, formulaWithValues, elementUniqueId, familyName, typeName);
    }

    private readonly record struct ReadKey(ParameterKind Kind, ParameterScope Scope, Guid SharedGuid,
        string Name, string Spec, string BuiltInId, string Unit);

    private static string? Identification(TakeoffValue value, ParameterSource source)
    {
        if (value.Number != null || value.Boolean != null)
            throw new InvalidDataException($"Identification {source}: expected text.");
        return value.Text;
    }

    private static decimal Numeric(TakeoffValue value, ParameterSource source)
    {
        if (value.Number is not { } number || value.Text != null || value.Boolean != null)
            throw new InvalidDataException($"Parameter {source}: expected a numeric value; missing or mismatched input.");
        if (source.Spec == "INTEGER" && decimal.Truncate(number) != number)
            throw new InvalidDataException($"Parameter {source}: INTEGER requires an integral value.");
        return number;
    }

    private static string Text(TakeoffValue value, ParameterSource source)
    {
        if (value.Text is not { } text || value.Number != null || value.Boolean != null)
            throw new InvalidDataException($"Parameter {source}: expected a text value; missing or mismatched input.");
        return text;
    }

    private static bool Boolean(TakeoffValue value, ParameterSource source)
    {
        if (value.Boolean is not { } boolean || value.Number != null || value.Text != null)
            throw new InvalidDataException($"Parameter {source}: expected a boolean value; missing or mismatched input.");
        return boolean;
    }

    private static bool Matches(RuleCondition condition, TakeoffValue actual)
    {
        int comparison = condition.Operand switch
        {
            OperandKind.Numeric => condition.Operator is ConditionOperator.Equal or ConditionOperator.NotEqual
                ? ConditionNumberComparison.Compare(Numeric(actual, condition.Source), condition.Number, condition.Source.Spec)
                : Numeric(actual, condition.Source).CompareTo(condition.Number),
            OperandKind.Text => string.Compare(Text(actual, condition.Source), condition.Text, StringComparison.Ordinal),
            OperandKind.Boolean => Boolean(actual, condition.Source).CompareTo(condition.Boolean),
            _ => throw new InvalidDataException("Unsupported condition operand.")
        };
        return condition.Operator switch
        {
            ConditionOperator.Equal => comparison == 0,
            ConditionOperator.NotEqual => comparison != 0,
            ConditionOperator.Greater => comparison > 0,
            ConditionOperator.GreaterOrEqual => comparison >= 0,
            ConditionOperator.Less => comparison < 0,
            ConditionOperator.LessOrEqual => comparison <= 0,
            _ => throw new InvalidDataException("Unsupported condition operator.")
        };
    }
}
