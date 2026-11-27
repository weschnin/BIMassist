using System.IO;

namespace BIMassist.Ava.Core;

/// <summary>Builds the persisted last-run snapshot from a complete, error-free takeoff result.</summary>
public static class AvaQuantityRunFactory
{
    public static AvaQuantityRun Create(AvaTemplate template, TakeoffResult takeoff, string projectTitle,
        string projectPath, string scopeDescription, Guid runId, DateTimeOffset createdUtc,
        string groupingParameter = "", IReadOnlyDictionary<string, string>? groupingValues = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? groupingSnapshots = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(takeoff);
        if (takeoff.Errors.Count > 0)
            throw new InvalidDataException("Fehlerhafte Mengenergebnisse dürfen nicht als letzter Lauf gespeichert werden.");
        if (takeoff.Rows.Count == 0)
            throw new InvalidDataException("Ein Lauf ohne ermittelte Mengenzeilen kann nicht gespeichert werden.");

        var records = new List<AvaQuantityRecord>(takeoff.Rows.Count);
        for (var index = 0; index < takeoff.Rows.Count; index++)
        {
            var row = takeoff.Rows[index];
            var lv = template.LvCatalog.Entries.SingleOrDefault(entry => entry.Id == row.LvItemId)
                ?? throw new InvalidDataException($"LV-Position '{row.LvItemId}' fehlt beim Erstellen des Mengenermittlungslaufs.");
            var groupingValue = "";
            if (!string.IsNullOrWhiteSpace(groupingParameter)
                && (groupingValues == null || !groupingValues.TryGetValue(row.ElementId, out groupingValue!)))
                throw new InvalidDataException($"Gruppierungswert für Revit-Element {row.ElementId} fehlt.");
            records.Add(new AvaQuantityRecord(Guid.NewGuid(), AvaQuantityRunCodec.CreateBimassistId(index + 1),
                row.LvItemId, lv.Ordinal, lv.Text, lv.Unit, row.ElementId,
                row.FormulaWithParameters, row.FormulaWithValues, groupingParameter, groupingValue, row.FamilyName, row.TypeName, row.Value)
            {
                GroupingValues = groupingSnapshots?.ToDictionary(snapshot => snapshot.Key,
                    snapshot => snapshot.Value.TryGetValue(row.ElementId, out var value) ? value : "", StringComparer.Ordinal)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal)
            });
        }

        var run = new AvaQuantityRun(AvaQuantityRunCodec.CurrentSchemaVersion, runId, createdUtc,
            projectTitle, projectPath, scopeDescription, TemplateJson.Serialize(template), records.AsReadOnly());
        _ = AvaQuantityRunCodec.Serialize(run);
        return run;
    }
}
