using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BIMassist.Ava.Core;

/// <summary>Per-user opt-in choices for resolving duplicate exact-name parameters.</summary>
public sealed class AvaParameterChoiceStore
{
    private readonly string path;
    private Dictionary<string, string> choices;

    public AvaParameterChoiceStore(string? path = null)
    {
        this.path = path ?? (Environment.GetEnvironmentVariable("BIMASSIST_AVA_PARAMETER_CHOICES") is { Length: > 0 } overridePath
            ? overridePath : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIMassist", "ava-parameter-choices.json"));
        choices = Load(this.path);
    }

    public static string Key(string documentIdentity, Guid templateProfileId, string typeUniqueId, ParameterSource source)
    {
        var raw = string.Join("\n", documentIdentity, templateProfileId.ToString("D"), typeUniqueId,
            source.Kind, source.Scope, source.Spec, source.Name);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public string? Find(string key) => choices.TryGetValue(key, out var identity) ? identity : null;

    public string? FindAvailable(string key, IReadOnlyCollection<AvaParameterCandidate> candidates)
    {
        var identity = Find(key);
        return identity != null && candidates.Any(candidate => string.Equals(candidate.Identity, identity, StringComparison.Ordinal))
            ? identity : null;
    }

    public void Remember(string key, string identity)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(identity))
            throw new InvalidDataException("Parameterauswahl kann nicht ohne Schlüssel und Identität gespeichert werden.");
        var updated = new Dictionary<string, string>(choices, StringComparer.Ordinal) { [key] = identity };
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
            choices = updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Dictionary<string, string> Load(string file)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            return loaded == null ? new(StringComparer.Ordinal) : new(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
