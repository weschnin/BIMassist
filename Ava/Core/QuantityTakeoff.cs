using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BIMassist.Ava.Core;

public sealed record TakeoffValue(decimal? Number = null, string? Text = null, bool? Boolean = null);
public sealed record TakeoffElement(string Id, Func<ParameterSource, string, TakeoffValue> Read, string SourceIdentity = "");
public sealed record TakeoffIssue(string ElementId, string Message);
public sealed record TakeoffLine(string LvItemId, decimal Value, string Unit, string Explanation, string ElementId = "");
/// <summary>Rows are ordered by input element, mapping, then position; Errors must block the entire export.
/// Unmapped counts marked elements without an exact BGK/type rule, not elements whose conditions are false.</summary>
public sealed record TakeoffResult(IReadOnlyList<TakeoffLine> Rows, IReadOnlyList<TakeoffIssue> Errors,
    int Examined, int Unmarked, int Unmapped);

/// <summary>Read-only, decimal takeoff. Any Errors must block export of the entire result.</summary>
public static class QuantityTakeoff
{
    public static TakeoffResult Calculate(AvaTemplate template, IEnumerable<TakeoffElement> elements)
    {
        TemplateEditor.EnsureValid(template);
        if (template.Mappings.Count == 0) throw new InvalidDataException("At least one mapping is required for takeoff.");
        var rows = new List<TakeoffLine>();
        var errors = new List<TakeoffIssue>();
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
            if (string.IsNullOrWhiteSpace(bgk) || string.IsNullOrWhiteSpace(type)) { unmarked++; continue; }
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
            if (rules.Count == 0) { unmapped++; continue; }
            foreach (var rule in rules)
            {
                bool applies = true;
                foreach (var condition in rule.Conditions)
                {
                    // Evaluate every input even after false: a missing later input is an error, not false.
                    try { applies &= Matches(condition, Read(condition.Source, condition.Unit)); }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                    {
                        errors.Add(new(element.Id, $"Element {element.Id}; {RuleContext(template, rule)}; Condition {condition}:\n{ex.Message}"));
                        applies = false;
                    }
                }
                if (!applies) continue;
                // Matching rules and sibling positions are intentionally independent and additive.
                foreach (var position in rule.Positions)
                {
                    try { rows.Add(CalculateLine(element.Id, element.SourceIdentity, rule.Id, position, Read)); }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                    {
                        errors.Add(new(element.Id, $"Element {element.Id}; {RuleContext(template, rule, position)}; Position {position.Id}:\n{ex.Message}"));
                    }
                }
            }
        }
        return new(rows.AsReadOnly(), errors.AsReadOnly(), examined, unmarked, unmapped);
    }

    private static string RuleContext(AvaTemplate template, MappingRule rule, MappingPosition? position = null)
    {
        var ids = (position == null ? rule.Positions : new[] { position }.AsEnumerable())
            .Select(p => p.LvItemId).Distinct(StringComparer.Ordinal).ToList();
        var ordinals = ids.Take(6).Select(id => template.LvCatalog.Entries.Single(lv => lv.Id == id).Ordinal);
        return $"BGK {rule.BgkKey} / Typ {rule.TypeNumber}; LV {string.Join(", ", ordinals)}"
            + (ids.Count > 6 ? $" (+{ids.Count - 6} weitere)" : "") + $"; Rule {rule.Id}";
    }

    private static TakeoffLine CalculateLine(string elementId, string sourceIdentity, Guid ruleId, MappingPosition position,
        Func<ParameterSource, string, TakeoffValue> read)
    {
        var quantity = position.Quantity ?? throw new InvalidDataException("Quantity is not defined.");
        decimal value;
        string expression;
        if (quantity.Kind == QuantityKind.Formula)
        {
            var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var inputs = new List<string>();
            foreach (var variable in quantity.Variables)
            {
                var input = Numeric(read(variable.Source, variable.Unit), variable.Source);
                values.Add(variable.Name, input);
                inputs.Add($"{variable.Name}={input.ToString(CultureInfo.InvariantCulture)} {variable.Unit} [Quelle: {variable.Source}]");
            }
            value = QuantityFormula.Evaluate(quantity.Expression, values);
            expression = quantity.Expression + " (" + string.Join("; ", inputs) + ")";
        }
        else
        {
            value = quantity.Kind == QuantityKind.Constant ? quantity.ConstantValue!.Value
                : Numeric(read(quantity.Source!, quantity.Unit), quantity.Source!);
            expression = quantity.Kind == QuantityKind.Constant ? "constant" : quantity.Source!.ToString();
        }
        return new(position.LvItemId, value, quantity.Unit,
            $"Element {elementId}; " + (string.IsNullOrEmpty(sourceIdentity) ? "" : sourceIdentity + "; ") +
            $"Rule {ruleId}; Position {position.Id}; {expression} = {value.ToString(CultureInfo.InvariantCulture)} {quantity.Unit}", elementId);
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
            OperandKind.Numeric => Numeric(actual, condition.Source).CompareTo(condition.Number),
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
