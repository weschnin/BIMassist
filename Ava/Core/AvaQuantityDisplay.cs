using System.Globalization;
using System.Text.RegularExpressions;

namespace BIMassist.Ava.Core;

/// <summary>Display-only transformations. Never use these strings as machine/X31 formulas.</summary>
public static class AvaQuantityDisplay
{
    private static readonly Regex LegacySource = new(
        @"\A(?<name>.+?) \| [A-Z][A-Z0-9_]* \| (?:Instance|Type) \| (?:[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}|[A-Z][A-Z0-9_]*|Parametername \(Familie/Systemfamilie/gemeinsam\))(?: \([^\r\n]*\))?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Names (including bracketed names and unit suffixes such as m2) are indivisible tokens.
    private static readonly Regex Tokens = new(
        @"\[[^\]]*\]|[\p{L}_][\p{L}\p{N}_]*|(?:\d+(?:[.,]\d+)?|[.,]\d+)(?:[eE][+-]?\d+)?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static decimal Round(decimal value) => decimal.Round(value, 3, MidpointRounding.AwayFromZero);

    public static string FormulaWithParameters(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        for (var suffix = stored.LastIndexOf(" [", StringComparison.Ordinal);
            suffix >= 0 && stored.EndsWith("]", StringComparison.Ordinal);
            suffix = suffix == 0 ? -1 : stored.LastIndexOf(" [", suffix - 1, StringComparison.Ordinal))
        {
            var bindings = stored[(suffix + 2)..^1].Split("; ", StringSplitOptions.None);
            if (bindings.All(binding =>
            {
                var arrow = binding.IndexOf(" ← ", StringComparison.Ordinal);
                return arrow > 0 && binding[..arrow].IndexOfAny(['[', ']']) < 0
                    && LegacySource.IsMatch(binding[(arrow + 3)..]);
            })) return stored[..suffix];
        }
        var source = LegacySource.Match(stored);
        if (source.Success) return source.Groups["name"].Value;
        // Old constants carried a label; leave their literal and unit intact for token formatting.
        if (stored.StartsWith("Konstante ", StringComparison.Ordinal)
            && stored.Length > 10 && (char.IsDigit(stored[10]) || stored[10] is '-' or '+' or '.' or ','))
            return stored[10..];
        return stored;
    }

    public static string FormulaWithValues(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return Tokens.Replace(stored, match =>
        {
            var token = match.Value;
            if (token[0] == '[' || char.IsLetter(token[0]) || token[0] == '_') return token;
            if (!decimal.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return token; // Do not corrupt out-of-range literals in an old saved run.
            var formatted = Round(number).ToString("0.###", CultureInfo.InvariantCulture);
            return token.Contains(',') ? formatted.Replace('.', ',') : formatted;
        });
    }
}
