using BIMassist.Ava.Core;

internal static class Program
{
    private static readonly Dictionary<string, decimal> Empty = new(StringComparer.Ordinal);
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Test("decimal literals accept either separator", () =>
        {
            Equal(12.5m, QuantityFormula.Evaluate("12.5", Empty));
            Equal(12.5m, QuantityFormula.Evaluate("12,5", Empty));
            Equal(0.5m, QuantityFormula.Evaluate(".5", Empty));
            Equal(0.5m, QuantityFormula.Evaluate(",5", Empty));
            Equal(12m, QuantityFormula.Evaluate(" 12. ", Empty));
        });
        Test("operators use precedence, parentheses, left associativity and unary signs", () =>
        {
            Equal(14m, QuantityFormula.Evaluate("2 + 3 * 4", Empty));
            Equal(20m, QuantityFormula.Evaluate("(2 + 3) * 4", Empty));
            Equal(2m, QuantityFormula.Evaluate("20 / 5 / 2", Empty));
            Equal(5m, QuantityFormula.Evaluate("10 - 3 - 2", Empty));
            Equal(-5m, QuantityFormula.Evaluate("-(2 + 3)", Empty));
            Equal(-6m, QuantityFormula.Evaluate("2 * -3", Empty));
            Equal(3m, QuantityFormula.Evaluate("1--2", Empty));
            Equal(2m, QuantityFormula.Evaluate("-+-2", Empty));
            Equal(0.3m, QuantityFormula.Evaluate("0.1 + 0,2", Empty));
            Equal(-1m, QuantityFormula.Evaluate("1-2", Empty));
        });
        Test("parameters preserve exact unique first-use names", () =>
        {
            const string expression = "Breite * [Overall Height] + Breite + [Breite] + breite + _x2 + Höhe + 面積2";
            var expected = new[] { "Breite", "Overall Height", "breite", "_x2", "Höhe", "面積2" };
            if (!QuantityFormula.GetParameterNames(expression).SequenceEqual(expected)) throw new Exception("Names or order changed.");
            var values = new Dictionary<string, decimal>(StringComparer.Ordinal)
            {
                ["Breite"] = 2, ["Overall Height"] = 3, ["breite"] = 5,
                ["_x2"] = 7, ["Höhe"] = 11, ["面積2"] = 13
            };
            Equal(46m, QuantityFormula.Evaluate(expression, values));
            Equal(6m, QuantityFormula.Evaluate("[a+b] + [123]", new Dictionary<string, decimal> { ["a+b"] = 2, ["123"] = 4 }));
            if (QuantityFormula.GetParameterNames("1+2").Count != 0) throw new Exception("Literal has parameters.");
        });
        Test("missing values and input dictionary comparer cannot bypass ordinal names", () =>
        {
            Reject(() => QuantityFormula.Evaluate("x", Empty));
            Reject(() => QuantityFormula.Evaluate("X", new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["x"] = 3 }));
            Equal(3m, QuantityFormula.Evaluate("x", new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["x"] = 3, ["unused"] = 9 }));
        });
        Test("validation requires exact one-to-one bindings and parses without evaluation", () =>
        {
            QuantityFormula.Validate("1 / (x - x)", new[] { "x" });
            QuantityFormula.Validate("1/0", Array.Empty<string>());
            QuantityFormula.Validate("x + [x]", new[] { "x" });
            QuantityFormula.Validate("x + X + [Overall Height]", new[] { "X", "Overall Height", "x" });
            Reject(() => QuantityFormula.Validate("x", Array.Empty<string>()));
            Reject(() => QuantityFormula.Validate("x", new[] { "X" }));
            Reject(() => QuantityFormula.Validate("x", new[] { "x", "unused" }));
            Reject(() => QuantityFormula.Validate("1", new[] { "unused" }));
            Reject(() => QuantityFormula.Validate("x", new[] { "x", "x" }));
            foreach (var invalid in new[] { "", " ", " x", "x ", "[x]", "x]", null! })
                Reject(() => QuantityFormula.Validate("x", new[] { "x", invalid }));
        });
        Test("division by zero and decimal overflow are domain errors", () =>
        {
            foreach (var expression in new[] { "1/0", "0/0", "1/(x-x)", "1/-0", "79228162514264337593543950335+1", "79228162514264337593543950335*2", "79228162514264337593543950335/0.1", "-79228162514264337593543950335-1", "79228162514264337593543950336" })
                Reject(() => QuantityFormula.Evaluate(expression, new Dictionary<string, decimal> { ["x"] = 3 }));
            QuantityFormula.Validate("79228162514264337593543950335+1", Array.Empty<string>());
        });
        foreach (var expression in new[]
        {
            "", " ", "()", "(1", "1)", "+", "1+", "*1", "1**2", "1//2", "1 2", "2(3)", "(1)2", "x y",
            ".", ",", "1.2.3", "1,2,3", "1,234.5", "1.234,5", "1 234", "1e3", "0x10", "2^3", "5%2",
            "[]", "[ ]", "[ x]", "[x ]", "[\tx]", "[x\n]", "[x", "x]", "[[x]]", "[x][y]",
            "Math.Abs(-1)", "System.IO.File.Delete(\"x\")", "new Foo()", "x(1)", "1;2", "1/*comment*/", "1//comment",
            "\"1\"", "'1'", "x=1", "x?1:2", "1<2", "1&2", "{1}", "1\\2", "1\0+2", "1$2"
        })
        {
            Test("reject unsupported syntax: " + expression.Replace("\0", "\\0"), () => RejectAll(expression));
        }
        Test("length limit includes whitespace and accepts exactly 2048 characters", () =>
        {
            var atLimit = "1" + new string(' ', 2047);
            Equal(1m, QuantityFormula.Evaluate(atLimit, Empty));
            QuantityFormula.Validate(atLimit, Array.Empty<string>());
            RejectAll(atLimit + " ");
        });
        Test("node limit accepts 256 and rejects 257 including flat expressions", () =>
        {
            var sum = string.Join("+", Enumerable.Repeat("1", 128));
            Equal(128m, QuantityFormula.Evaluate("+" + sum, Empty));
            QuantityFormula.Validate("+" + sum, Array.Empty<string>());
            RejectAll(sum + "+1");
        });
        Test("parenthesis depth accepts 32 and rejects 33", () =>
        {
            Equal(1m, QuantityFormula.Evaluate(new string('(', 32) + "1" + new string(')', 32), Empty));
            RejectAll(new string('(', 33) + "1" + new string(')', 33));
        });
        Test("unary depth accepts 32 and rejects 33", () =>
        {
            Equal(1m, QuantityFormula.Evaluate(new string('-', 32) + "1", Empty));
            RejectAll(new string('-', 33) + "1");
        });
        Test("mixed unary and parentheses share the nesting budget", () =>
        {
            Equal(1m, QuantityFormula.Evaluate(new string('(', 16) + new string('-', 16) + "1" + new string(')', 16), Empty));
            RejectAll(new string('(', 17) + new string('-', 16) + "1" + new string(')', 17));
        });
        Test("null arguments produce domain errors", () =>
        {
            RejectAll(null!);
            Reject(() => QuantityFormula.Validate("1", null!));
            Reject(() => QuantityFormula.Evaluate("1", null!));
        });
        Test("Unicode scalar letters and digits are accepted without name normalization", () =>
        {
            const string supplementary = "\U00010400\U0001D7D9";
            if (!QuantityFormula.GetParameterNames(supplementary + "+[é]+[e\u0301]").SequenceEqual(new[] { supplementary, "é", "e\u0301" }))
                throw new Exception("Unicode names changed.");
            Equal(9m, QuantityFormula.Evaluate(supplementary + "+[é]+[e\u0301]", new Dictionary<string, decimal>
            { [supplementary] = 2, ["é"] = 3, ["e\u0301"] = 4 }));
        });
        Test("literal magnitude beyond decimal maximum is rejected", () =>
        {
            RejectAll("79228162514264337593543950335.1");
            RejectAll("-79228162514264337593543950335,1");
            Equal(decimal.MaxValue, QuantityFormula.Evaluate("79228162514264337593543950335", Empty));
            Equal(decimal.MinValue, QuantityFormula.Evaluate("-79228162514264337593543950335", Empty));
        });
        Test("numeric parsing is culture independent and never treats commas as grouping", () =>
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "de-DE", "fr-FR" })
                {
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    Equal(3.75m, QuantityFormula.Evaluate("1,25 + 2.5", Empty));
                    Equal(1m, QuantityFormula.Evaluate("1,000", Empty));
                    Equal(decimal.MaxValue, QuantityFormula.Evaluate("00079228162514264337593543950335.000", Empty));
                }
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        });
        Test("hostile long inputs fail within the work budgets", () =>
        {
            RejectAll(new string('(', 2048));
            RejectAll(new string('-', 2048));
            RejectAll(string.Join("+", Enumerable.Repeat("1", 1024)));
            RejectAll(new string('9', 2048));
            foreach (var invalid in new[] { "\uD800", "x\uDFFF", "e\u0301", "[x]y", "1\u202F234" }) RejectAll(invalid);
        });
        Test("deterministic malformed-input fuzzing exposes only domain errors", () =>
        {
            var random = new Random(20261108);
            const string alphabet = "0123456789.,+-*/()[]_abc é\t\n;^%\"=\0";
            for (var i = 0; i < 2000; i++)
            {
                var expression = new string(Enumerable.Range(0, random.Next(129)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
                IReadOnlyList<string> names;
                try { names = QuantityFormula.GetParameterNames(expression); }
                catch (InvalidDataException) { RejectAll(expression); continue; }
                QuantityFormula.Validate(expression, names);
                try { QuantityFormula.Evaluate(expression, names.ToDictionary(n => n, _ => 1m, StringComparer.Ordinal)); }
                catch (InvalidDataException) { /* Valid syntax can still divide by zero or overflow. */ }
            }
        });
        Console.WriteLine($"{passed} passed; {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
    }

    private static void Equal(decimal expected, decimal actual)
    {
        if (actual != expected) throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void RejectAll(string expression)
    {
        Reject(() => QuantityFormula.GetParameterNames(expression));
        Reject(() => QuantityFormula.Validate(expression, Array.Empty<string>()));
        Reject(() => QuantityFormula.Evaluate(expression, Empty));
    }

    private static void Reject(Action body)
    {
        try { body(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException.");
    }
}
