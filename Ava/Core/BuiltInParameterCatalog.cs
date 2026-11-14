namespace BIMassist.Ava.Core;

/// <summary>Runtime-discovered Revit BuiltInParameter identifiers with metadata for the known constrained sources.</summary>
internal static class BuiltInParameterCatalog
{
    private static readonly string[] FallbackNames =
        ["ASSEMBLY_CODE", "ALL_MODEL_TYPE_COMMENTS", "ALL_MODEL_DESCRIPTION", "HOST_VOLUME_COMPUTED"];

    internal static readonly string[] Specifications =
        ["TEXT", "YESNO", "NUMBER", "INTEGER", "LENGTH", "AREA", "VOLUME", "ANGLE", "MASS_DENSITY"];

    internal static IReadOnlyList<string> GetNames(Type? enumType = null)
    {
        enumType ??= FindEnumType();
        if (enumType?.IsEnum != true) return FallbackNames;
        return Enum.GetNames(enumType)
            .Where(name => name is not ("INVALID" or "NUM_BUILTIN_PARAMETERS"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool IsValid(string identifier, Type? enumType = null)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier is "INVALID" or "NUM_BUILTIN_PARAMETERS") return false;
        enumType ??= FindEnumType();
        if (enumType?.IsEnum != true) return FallbackNames.Contains(identifier, StringComparer.Ordinal);
        return Enum.GetNames(enumType).Contains(identifier, StringComparer.Ordinal);
    }

    internal static bool TryGetKnownPolicy(string identifier, out string spec, out ParameterScope scope)
    {
        (spec, scope) = identifier switch
        {
            "ASSEMBLY_CODE" or "ALL_MODEL_TYPE_COMMENTS" or "ALL_MODEL_DESCRIPTION" => ("TEXT", ParameterScope.Type),
            "HOST_VOLUME_COMPUTED" => ("VOLUME", ParameterScope.Instance),
            _ => ("", ParameterScope.Instance)
        };
        return spec.Length > 0;
    }

    private static Type? FindEnumType()
    {
        const string fullName = "Autodesk.Revit.DB.BuiltInParameter";
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "RevitAPI")?.GetType(fullName, throwOnError: false);
        return loaded ?? Type.GetType(fullName + ", RevitAPI", throwOnError: false);
    }
}
