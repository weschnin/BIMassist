using System.Globalization;

namespace BIMassist.Core
{
    internal sealed class Bgk3DTextSpec
    {
        private Bgk3DTextSpec(string familyName, string typeName, string text,
            string typeComments, string description)
        {
            FamilyName = familyName;
            TypeName = typeName;
            Text = text;
            AssemblyCode = familyName;
            TypeComments = typeComments;
            Description = description;
        }

        internal string FamilyName { get; }
        internal string TypeName { get; }
        internal string Text { get; }
        internal string AssemblyCode { get; }
        internal string TypeComments { get; }
        internal string Description { get; }

        internal static bool TryCreate(string key, string bgkName, string typeNumber, string typeName,
            out Bgk3DTextSpec? spec, out string error)
        {
            spec = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(bgkName) || string.IsNullOrWhiteSpace(typeNumber)
                || string.IsNullOrWhiteSpace(typeName))
            {
                error = "BGK-Nummer, BGK-Bezeichnung, Typnummer und Typbezeichnung müssen ausgefüllt sein.";
                return false;
            }

            string name = $"{key.Trim()} {bgkName.Trim()}";
            string number = typeNumber.Trim();
            string designation = typeName.Trim();
            spec = new Bgk3DTextSpec(name, $"{number} {designation}", $"{name}\n{number} {designation}",
                number, designation);
            return true;
        }

        internal static bool TrySize(string heightText, string depthText, out double heightMm, out double depthMm, out string error)
        {
            heightMm = 0;
            depthMm = 0;
            error = string.Empty;
            if (!ParsePositive(heightText, out heightMm) || !ParsePositive(depthText, out depthMm))
            {
                error = "Texthöhe und Texttiefe müssen positive Zahlen in mm sein.";
                return false;
            }
            return true;
        }

        private static bool ParsePositive(string input, out double value)
        {
            string normalized = input?.Trim().Replace(',', '.') ?? string.Empty;
            return double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
                && !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value <= 10000;
        }
    }
}
