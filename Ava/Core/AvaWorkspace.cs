using System.IO;
using System.Text;
using System.Text.Json;

namespace BIMassist.Ava.Core;

/// <summary>Per-user editing workspace; stores only a path, never a copy of or changes to the template.</summary>
public sealed class AvaWorkspace
{
    private readonly string settingsPath;
    private string? settingsError;
    public string? LastTemplatePath { get; private set; }
    internal bool HasRememberedTemplate => LastTemplatePath != null || settingsError != null;
    // Explicit host/test override also isolates existing parameterless-window tests from user settings.
    internal static string DefaultSettingsPath => Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS") is { Length: > 0 } path
        ? path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMassist", "ava-workspace.json");

    public AvaWorkspace(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? DefaultSettingsPath;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(this.settingsPath));
            LastTemplatePath = json.RootElement.GetProperty(nameof(LastTemplatePath)).GetString();
            if (string.IsNullOrWhiteSpace(LastTemplatePath)) throw new InvalidDataException("Der letzte Vorlagenpfad fehlt.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (IsFileError(ex) || ex is JsonException or KeyNotFoundException)
        {
            LastTemplatePath = null;
            settingsError = "Arbeitsbereich-Einstellungen konnten nicht gelesen werden: " + ex.Message;
        }
    }

    // Validate before any editor/export write, not merely after writing in Remember.
    internal void EnsureTemplatePath(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(settingsPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AVA-Vorlage und Arbeitsbereich-Einstellungen dürfen nicht dieselbe Datei sein.");
    }

    public void Remember(string path)
    {
        EnsureTemplatePath(path);
        string fullPath = Path.GetFullPath(path);
        string fullSettingsPath = Path.GetFullPath(settingsPath);
        TemplateEditor.EnsureValid(TemplateJson.Load(fullPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullSettingsPath)!);
        string temporary = fullSettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { LastTemplatePath = fullPath }), new UTF8Encoding(false));
            if (File.Exists(fullSettingsPath)) File.Replace(temporary, fullSettingsPath, null);
            else File.Move(temporary, fullSettingsPath);
            LastTemplatePath = fullPath;
            settingsError = null;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Loads and semantically validates the last editing template without ever modifying it.</summary>
    public AvaTemplate LoadLast()
    {
        try
        {
            if (settingsError != null) throw new InvalidDataException(settingsError);
            if (string.IsNullOrWhiteSpace(LastTemplatePath)) throw new InvalidDataException("Noch keine AVA-Vorlage gespeichert oder geöffnet.");
            if (!Path.IsPathFullyQualified(LastTemplatePath)) throw new InvalidDataException("Der gespeicherte Vorlagenpfad ist nicht absolut.");
            var template = TemplateJson.Load(LastTemplatePath);
            TemplateEditor.EnsureValid(template);
            return template;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            throw new InvalidDataException($"Letzte AVA-Vorlage konnte nicht geladen werden{(LastTemplatePath == null ? "" : ": " + LastTemplatePath)}. Öffnen oder speichern Sie eine gültige JSON-Vorlage im AVA-Vorlageneditor. {ex.Message}", ex);
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException;
}
