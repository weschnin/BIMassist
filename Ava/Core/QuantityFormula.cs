using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BIMassist.Ava.Core;

/// <summary>Pure decimal arithmetic in caller-supplied input units; no unit inference.</summary>
public static class QuantityFormula
{
    /// <summary>Returns unique, ordinal parameter names in first-use order after checking syntax.</summary>
    public static IReadOnlyList<string> GetParameterNames(string expression)
    {
        var parser = new Parser(expression);
        parser.Parse();
        return parser.Names.AsReadOnly();
    }

    /// <summary>Checks syntax and exact bindings without performing arithmetic.</summary>
    public static void Validate(string expression, IEnumerable<string> boundNames)
    {
        if (boundNames == null) throw new InvalidDataException("Binding names are required.");
        var required = new HashSet<string>(GetParameterNames(expression), StringComparer.Ordinal);
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in boundNames)
        {
            if (!IsValidName(name)) throw new InvalidDataException("Invalid binding name.");
            if (!bound.Add(name)) throw new InvalidDataException($"Duplicate binding '{name}'.");
            if (!required.Contains(name)) throw new InvalidDataException($"Unused binding '{name}'.");
        }
        foreach (var name in required)
            if (!bound.Contains(name)) throw new InvalidDataException($"Missing binding '{name}'. Add it with 'Parameter hinzufügen…' and bind it to the intended Revit source; the formula name must match exactly.");
    }

    /// <summary>Evaluates decimal arithmetic; negative results are not clamped. Extra values are ignored.</summary>
    public static decimal Evaluate(string expression, IReadOnlyDictionary<string, decimal> values)
    {
        if (values == null) throw new InvalidDataException("Parameter values are required.");
        var instructions = new Parser(expression).Parse();
        // Do not inherit a caller's case-insensitive comparer.
        var exactValues = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var pair in values) exactValues.Add(pair.Key, pair.Value);
        var stack = new Stack<decimal>();
        try
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Operator == '#') { stack.Push(instruction.Number); continue; }
                if (instruction.Operator == '$')
                {
                    if (!exactValues.TryGetValue(instruction.Name!, out var value))
                        throw new InvalidDataException($"Missing value for parameter '{instruction.Name}'.");
                    stack.Push(value);
                    continue;
                }
                var right = stack.Pop();
                if (instruction.Operator == 'P') { stack.Push(right); continue; }
                if (instruction.Operator == 'N') { stack.Push(-right); continue; }
                var left = stack.Pop();
                stack.Push(instruction.Operator switch
                {
                    '+' => left + right,
                    '-' => left - right,
                    '*' => left * right,
                    '/' => left / right,
                    _ => throw new InvalidDataException("Unsupported operator.")
                });
            }
            return stack.Pop();
        }
        catch (DivideByZeroException ex)
        {
            throw new InvalidDataException("Formula division by zero.", ex);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Formula decimal overflow.", ex);
        }
    }

    private static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name == name.Trim() && name.IndexOfAny(new[] { '[', ']' }) < 0;

    private readonly record struct Instruction(char Operator, decimal Number = 0, string? Name = null);

    private sealed class Parser
    {
        private readonly string text;
        private readonly List<Instruction> instructions = new();
        private readonly HashSet<string> nameSet = new(StringComparer.Ordinal);
        internal List<string> Names { get; } = new();
        private int position;
        private int depth;

        internal Parser(string text)
        {
            if (text == null || text.Length > 2048)
                throw new InvalidDataException("Formula must be non-null and at most 2048 characters.");
            this.text = text;
        }

        internal List<Instruction> Parse()
        {
            Expression();
            SkipWhitespace();
            if (position != text.Length)
            {
                var message = position < text.Length && char.IsLetter(text[position])
                    ? "Unexpected text. Declare each parameter name with 'Parameter hinzufügen…'; simple names may be bare and names containing spaces must use [Name] (use 'Name in Formel einfügen')."
                    : "Unexpected token.";
                throw Error(message);
            }
            return instructions;
        }

        private void Expression()
        {
            Term();
            while (Take('+', '-') is var op && op != '\0')
            {
                Term();
                Emit(new Instruction(op));
            }
        }

        private void Term()
        {
            Unary();
            while (Take('*', '/') is var op && op != '\0')
            {
                Unary();
                Emit(new Instruction(op));
            }
        }

        private void Unary()
        {
            var op = Take('+', '-');
            if (op != '\0')
            {
                EnterNesting();
                Unary();
                depth--;
                Emit(new Instruction(op == '+' ? 'P' : 'N'));
            }
            else Primary();
        }

        private void Primary()
        {
            if (Take('(') != '\0')
            {
                EnterNesting();
                Expression();
                depth--;
                if (Take(')') == '\0') throw Error("Missing closing parenthesis.");
                return;
            }
            SkipWhitespace();
            if (position < text.Length && text[position] == '[')
            {
                position++;
                var nameStart = position;
                while (position < text.Length && text[position] != ']') position++;
                if (position == text.Length) throw Error("Missing closing parameter bracket.");
                var name = text.Substring(nameStart, position - nameStart);
                position++;
                if (!IsValidName(name)) throw Error("Invalid bracketed parameter name.");
                Parameter(name);
                return;
            }
            var identifierStart = position;
            if (TakeIdentifierCharacter(first: true))
            {
                while (TakeIdentifierCharacter(first: false)) { }
                Parameter(text.Substring(identifierStart, position - identifierStart));
                return;
            }
            var start = position;
            while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
            if (position < text.Length && (text[position] == '.' || text[position] == ','))
            {
                position++;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
            }
            var literal = text.Substring(start, position - start).Replace(',', '.');
            var separator = literal.IndexOf('.');
            var whole = (separator < 0 ? literal : literal.Substring(0, separator)).TrimStart('0');
            const string maximum = "79228162514264337593543950335";
            // Decimal.TryParse can round MaxValue + a small fraction back to MaxValue.
            // Check the literal's magnitude before allowing decimal precision rounding.
            if (whole.Length > maximum.Length || (whole.Length == maximum.Length &&
                (string.CompareOrdinal(whole, maximum) > 0 || (whole == maximum && separator >= 0 &&
                    literal.Substring(separator + 1).Trim('0').Length > 0))))
                throw Error("Decimal literal overflow.");
            if (position == start || !decimal.TryParse(literal,
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                throw Error("Expected decimal literal.");
            Emit(new Instruction('#', number));
        }

        private bool TakeIdentifierCharacter(bool first)
        {
            if (position >= text.Length || !Rune.TryGetRuneAt(text, position, out var rune)) return false;
            if (rune.Value != '_' && !(first ? Rune.IsLetter(rune) : Rune.IsLetterOrDigit(rune))) return false;
            position += rune.Utf16SequenceLength;
            return true;
        }

        private void EnterNesting()
        {
            if (++depth > 32) throw Error("Formula nesting/unary depth exceeds 32.");
        }

        private void Emit(Instruction instruction)
        {
            // Postfix instructions correspond one-to-one to arithmetic tree nodes.
            if (instructions.Count == 256) throw Error("Formula exceeds 256 nodes.");
            instructions.Add(instruction);
        }

        private void Parameter(string name)
        {
            if (nameSet.Add(name)) Names.Add(name);
            Emit(new Instruction('$', Name: name));
        }

        private char Take(char first, char second = '\0')
        {
            SkipWhitespace();
            if (position < text.Length && (text[position] == first || (second != '\0' && text[position] == second)))
                return text[position++];
            return '\0';
        }

        private void SkipWhitespace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }

        private InvalidDataException Error(string message) => new InvalidDataException($"{message} At position {position}.");
    }
}
