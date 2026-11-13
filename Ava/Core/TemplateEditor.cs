using System.IO;

namespace BIMassist.Ava.Core;

/// <summary>File-based editor state. All imported replacements and rule edits are validated before commit.
/// The UI owns discard prompts. Mutable metadata is included in the dirty-state comparison.</summary>
public sealed class TemplateEditor
{
    public AvaTemplate Document { get; private set; } = new();
    public string? FilePath { get; private set; }
    private string baseline;
    public bool IsDirty => TemplateJson.Serialize(Document) != baseline;
    public TemplateEditor() { baseline = TemplateJson.Serialize(Document); }
    public void New() { Document = new(); FilePath = null; baseline = TemplateJson.Serialize(Document); }
    public void Open(string path)
    {
        var candidate = TemplateJson.Load(path); EnsureValid(candidate);
        Document = candidate; FilePath = path; baseline = TemplateJson.Serialize(candidate);
    }
    public void ImportJson(string json)
    {
        var candidate = TemplateJson.Deserialize(json); EnsureValid(candidate);
        Document = candidate; FilePath = null; baseline = "";
    }
    public void SaveAs(string path)
    {
        EnsureValid(Document); TemplateJson.Save(path, Document);
        FilePath = path; baseline = TemplateJson.Serialize(Document);
    }
    public void Export(string path) { EnsureValid(Document); TemplateJson.Save(path, Document); }
    public void ReplaceBgk(Catalog<BgkEntry> catalog) => Change(p => p.BgkCatalog = TemplateJson.Copy(catalog));
    public void ReplaceLv(Catalog<LvEntry> catalog) => Change(p => p.LvCatalog = TemplateJson.Copy(catalog));
    public void ReplaceParameters(Catalog<SharedParameter> catalog) => Change(p => p.ParameterCatalog = TemplateJson.Copy(catalog));
    public void UpsertMapping(MappingRule rule) => Change(p =>
    {
        int index = p.Mappings.FindIndex(m => m.Id == rule.Id);
        if (index < 0) p.Mappings.Add(TemplateJson.Copy(rule)); else p.Mappings[index] = TemplateJson.Copy(rule);
    });
    public void RemoveMapping(Guid id) => Change(p => p.Mappings.RemoveAll(m => m.Id == id));
    public void RemoveMappings(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        if (selected.Count == 0) return;
        Change(p => p.Mappings.RemoveAll(mapping => selected.Contains(mapping.Id)));
    }
    public void SetIdentification(bool bgk, ParameterSource source) => Change(p =>
    {
        if (bgk) p.BgkIdentification = TemplateJson.Copy(source); else p.TypeIdentification = TemplateJson.Copy(source);
    });
    public static void EnsureValid(AvaTemplate candidate)
    {
        var errors = TemplateValidator.Validate(candidate).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Take(20)) + (errors.Count > 20 ? $"\n… insgesamt {errors.Count} Fehler." : ""));
    }
    private void Change(Action<AvaTemplate> change)
    {
        var candidate = TemplateJson.Copy(Document);
        change(candidate); EnsureValid(candidate); Document = candidate;
    }
}
