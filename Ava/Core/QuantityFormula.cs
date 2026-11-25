using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BIMassist.Ava.Core;

/// <summary>Pure decimal arithmetic; typed takeoff also checks dimensions and normalizes physical inputs.</summary>
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
                if (instruction.Operator == 'R') { stack.Push(SquareRoot(right)); continue; }
                var left = stack.Pop();
                stack.Push(instruction.Operator switch
                {
                    '+' => left + right,
                    '-' => left - right,
                    '*' => left * right,
                    '/' => left / right,
                    '^' => Power(left, right),
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

    private readonly record struct Dimension(decimal Length = 0, decimal Angle = 0, decimal Density = 0, decimal Count = 0, decimal Lump = 0)
    {
        public static Dimension operator +(Dimension a, Dimension b) =>
            new(a.Length + b.Length, a.Angle + b.Angle, a.Density + b.Density,
                a.Count + b.Count, a.Lump + b.Lump);
        public static Dimension operator -(Dimension a, Dimension b) =>
            new(a.Length - b.Length, a.Angle - b.Angle, a.Density - b.Density,
                a.Count - b.Count, a.Lump - b.Lump);
        public static Dimension operator *(Dimension a, decimal exponent) =>
            new(a.Length * exponent, a.Angle * exponent, a.Density * exponent,
                a.Count * exponent, a.Lump * exponent);
    }

    private readonly record struct DimensionalValue(decimal Number, Dimension Dimension, bool LiteralOnly = false);

    private static (string? Base, Dimension Dimension) UnitDimension(string unit) => unit switch
    {
        "mm" or "cm" or "m" or "km" => ("m", new(Length: 1)),
        "mm2" or "cm2" or "m2" or "m²" => ("m2", new(Length: 2)),
        "cm3" or "m3" or "m³" => ("m3", new(Length: 3)),
        "deg" or "rad" => ("rad", new(Angle: 1)),
        "kg/m3" => ("kg/m3", new(Density: 1)),
        "St" => (null, new(Count: 1)),
        "psch" => (null, new(Lump: 1)),
        _ => (null, new())
    };

    /// <summary>Inputs are normalized to physical base units before arithmetic. A numeric
    /// literal added to a physical value is interpreted in the declared result unit.</summary>
    public static decimal EvaluateWithUnits(string expression,
        IReadOnlyDictionary<string, (decimal Value, string Unit, string Spec)> inputs, string resultUnit)
    {
        if (inputs == null) throw new InvalidDataException("Formula inputs are required.");
        if (!UnitCatalog.AllUnits.Contains(resultUnit, StringComparer.Ordinal))
            throw new InvalidDataException($"Unbekannte Formelergebniseinheit '{resultUnit}'.");
        var instructions = new Parser(expression).Parse();
        var (resultBase, expectedDimension) = UnitDimension(resultUnit);
        var stack = new Stack<DimensionalValue>();
        try
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Operator == '#') { stack.Push(new(instruction.Number, new(), true)); continue; }
                if (instruction.Operator == '$')
                {
                    if (!inputs.TryGetValue(instruction.Name!, out var input))
                        throw new InvalidDataException($"Missing value for parameter '{instruction.Name}'.");
                    if (!UnitCatalog.AllowsFormulaInput(input.Spec, input.Unit))
                        throw new InvalidDataException($"Unbekannte Eingabeeinheit '{input.Unit}' für Formelparameter '{instruction.Name}'.");
                    var (inputBase, dimension) = UnitDimension(input.Unit);
                    var normalized = inputBase == null ? input.Value : UnitCatalog.Convert(input.Value, input.Unit, inputBase);
                    stack.Push(new(normalized, dimension));
                    continue;
                }
                var right = stack.Pop();
                if (instruction.Operator is 'P' or 'N')
                {
                    stack.Push(right with { Number = instruction.Operator == 'N' ? -right.Number : right.Number });
                    continue;
                }
                if (instruction.Operator == 'R')
                {
                    stack.Push(new(SquareRoot(right.Number), right.Dimension * 0.5m, right.LiteralOnly));
                    continue;
                }
                var left = stack.Pop();
                if (instruction.Operator is '+' or '-')
                {
                    if (left.Dimension != right.Dimension)
                    {
                        if (left.LiteralOnly && right.Dimension == expectedDimension && resultBase != null)
                            left = left with { Number = UnitCatalog.Convert(left.Number, resultUnit, resultBase), Dimension = right.Dimension };
                        else if (right.LiteralOnly && left.Dimension == expectedDimension && resultBase != null)
                            right = right with { Number = UnitCatalog.Convert(right.Number, resultUnit, resultBase), Dimension = left.Dimension };
                        else throw new InvalidDataException("Formel-Dimensionen der Addition/Subtraktion stimmen nicht überein.");
                    }
                    stack.Push(new(instruction.Operator == '+' ? left.Number + right.Number : left.Number - right.Number,
                        left.Dimension, left.LiteralOnly && right.LiteralOnly));
                }
                else if (instruction.Operator is '*' or '/')
                {
                    stack.Push(new(instruction.Operator == '*' ? left.Number * right.Number : left.Number / right.Number,
                        instruction.Operator == '*' ? left.Dimension + right.Dimension : left.Dimension - right.Dimension,
                        left.LiteralOnly && right.LiteralOnly));
                }
                else if (instruction.Operator == '^')
                {
                    if (right.Dimension != new Dimension())
                        throw new InvalidDataException("Formel-Exponent muss dimensionslos sein.");
                    stack.Push(new(Power(left.Number, right.Number), left.Dimension * right.Number,
                        left.LiteralOnly && right.LiteralOnly));
                }
                else throw new InvalidDataException("Unsupported formula operator.");
            }
            var output = stack.Pop();
            if (output.Dimension != expectedDimension)
                throw new InvalidDataException($"Formel-Dimension passt nicht zur Ergebniseinheit '{resultUnit}'. NUMBER/INTEGER hat keine verifizierbare physikalische Einheit.");
            return resultBase == null ? output.Number : UnitCatalog.Convert(output.Number, resultBase, resultUnit);
        }
        catch (DivideByZeroException ex) { throw new InvalidDataException("Formula division by zero.", ex); }
        catch (OverflowException ex) { throw new InvalidDataException("Formula decimal overflow.", ex); }
    }

    private static decimal SquareRoot(decimal value)
    {
        if (value < 0) throw new InvalidDataException("Formula square root requires a nonnegative value.");
        if (value == 0) return 0;
        decimal guess = (decimal)Math.Sqrt((double)value);
        // Decimal Newton steps retain exact squares and avoid binary floating-point output.
        for (int i = 0; i < 64; i++)
        {
            var next = (guess + value / guess) / 2;
            if (next == guess) return next;
            guess = next;
        }
        return guess;
    }

    private static decimal Power(decimal basis, decimal exponent)
    {
        if (basis == 0 && exponent <= 0) throw new InvalidDataException("Zero cannot have a nonpositive exponent.");
        if (exponent == 0) return 1;
        if (exponent == 0.5m) return SquareRoot(basis);
        if (exponent == decimal.Truncate(exponent) && exponent >= -96 && exponent <= 96)
        {
            bool reciprocal = exponent < 0;
            int n = (int)Math.Abs(exponent);
            decimal result = 1, factor = basis;
            while (n > 0)
            {
                if ((n & 1) != 0) result *= factor;
                n >>= 1;
                if (n > 0) factor *= factor;
            }
            return reciprocal ? 1 / result : result;
        }
        if (basis < 0) throw new InvalidDataException("Fractional powers of negative values are not supported.");
        var approximate = Math.Pow((double)basis, (double)exponent);
        if (!double.IsFinite(approximate) || approximate > (double)decimal.MaxValue || approximate < (double)decimal.MinValue)
            throw new InvalidDataException("Formula power exceeds the decimal range.");
        return (decimal)approximate;
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
            else PowerExpression();
        }
        private void PowerExpression()
        {
            Primary();
            if (Take('^') != '\0')
            {
                EnterNesting();
                Unary(); // Right-associative and permits signed exponents.
                depth--;
                Emit(new Instruction('^'));
            }
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
                var name = text.Substring(identifierStart, position - identifierStart);
                if (name == "sqrt" && Take('(') != '\0')
                {
                    EnterNesting();
                    Expression();
                    depth--;
                    if (Take(')') == '\0') throw Error("Missing closing square-root parenthesis.");
                    Emit(new Instruction('R'));
                }
                else Parameter(name);
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
