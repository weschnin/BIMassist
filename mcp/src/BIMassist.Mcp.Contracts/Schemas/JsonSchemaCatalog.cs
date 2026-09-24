using System.Reflection;

namespace BIMassist.Mcp.Contracts.Schemas;

public static class JsonSchemaCatalog
{
    private const string ResourcePrefix = "BIMassist.Mcp.Contracts.Schemas.";

    public static IReadOnlyList<string> Names { get; } =
    [
        "apply-change-plan.schema.json",
        "bridge-request.schema.json",
        "bridge-response.schema.json",
        "change-plan.schema.json"
    ];

    public static string Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Names.Contains(name, StringComparer.Ordinal))
            throw new KeyNotFoundException($"Unknown contract schema '{name}'.");

        Assembly assembly = typeof(JsonSchemaCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"Embedded contract schema '{name}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
