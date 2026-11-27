using System.IO;

namespace BIMassist.Ava.Core;

/// <summary>Serializes only the measured records from the persisted last run.</summary>
public static class AvaQuantityRunExport
{
    public static string SerializeX31(AvaQuantityRun run, bool useRevitElementId)
    {
        var template = Prepare(run);
        return X31Writer.SerializeMeasured(template, run.Records, useRevitElementId);
    }

    public static void SaveX31(string path, AvaQuantityRun run, bool useRevitElementId)
    {
        var template = Prepare(run);
        X31Writer.SaveMeasured(path, template, run.Records, useRevitElementId);
    }

    public static string SerializeX82(AvaQuantityRun run)
    {
        var template = Prepare(run);
        return X82QuantityWriter.Serialize(template, ToTakeoffRows(run));
    }

    public static void SaveX82(string path, AvaQuantityRun run)
    {
        var template = Prepare(run);
        X82QuantityWriter.Save(path, template, ToTakeoffRows(run));
    }

    private static IReadOnlyList<QuantityTakeoffRow> ToTakeoffRows(AvaQuantityRun run) => run.Records
        .Select(record => new QuantityTakeoffRow(record.LvItemId, record.Result, record.Unit, "",
            record.RevitElementId))
        .ToArray();

    private static AvaTemplate Prepare(AvaQuantityRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _ = AvaQuantityRunCodec.Serialize(run);
        if (run.Records.Count == 0)
            throw new InvalidDataException("Der gespeicherte Lauf enthält keine berechneten Mengen für einen Export.");
        var template = TemplateJson.Deserialize(run.TemplateJson);
        TemplateEditor.EnsureValid(template);
        return template;
    }
}
