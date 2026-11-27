using BIMassist.Ava.Core;
using System.Text.Json.Nodes;

internal static class GroupingSnapshotTests
{
    public static void FactoryCopiesIndependentSnapshots()
    {
        var property = typeof(AvaQuantityRecord).GetProperty("GroupingValues")
            ?? throw new Exception("Missing non-positional GroupingValues snapshot property");
        Equal(typeof(IReadOnlyDictionary<string, string>), property.PropertyType);
        var factory = typeof(AvaQuantityRunFactory).GetMethod("Create")!;
        Equal("groupingSnapshots", factory.GetParameters().Last().Name!);
        var template = new AvaTemplate
        {
            LvCatalog = new(new("fixture", "test", ""), [new("lv1", "boq", ["01"], "01", "Item", "", "St")])
        };
        var takeoff = new TakeoffResult(
            [new TakeoffLine("lv1", 1m, "St", "", "123"), new TakeoffLine("lv1", 2m, "St", "", "456")],
            [], 2, 0, 0, []);
        var trade = new Dictionary<string, string> { ["123"] = "Rohbau", ["456"] = "Ausbau" };
        var area = new Dictionary<string, string> { ["123"] = "EG" };
        var snapshots = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["Gewerk (Instanz)"] = trade, ["Baubereich (Typ)"] = area
        };
        var run = (AvaQuantityRun)factory.Invoke(null, [template, takeoff, "Project", "", "all", Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            "Gewerk (Instanz)", trade, snapshots])!;
        IReadOnlyDictionary<string, string> Values(AvaQuantityRecord record) => (IReadOnlyDictionary<string, string>)property.GetValue(record)!;
        Equal("Rohbau", Values(run.Records[0])["Gewerk (Instanz)"]);
        Equal("Ausbau", Values(run.Records[1])["Gewerk (Instanz)"]);
        Equal("EG", Values(run.Records[0])["Baubereich (Typ)"]);
        Equal("", Values(run.Records[1])["Baubereich (Typ)"]);
        Equal("Gewerk (Instanz)", run.Records[0].GroupingParameter);
        Equal("Rohbau", run.Records[0].GroupingValue);
        trade["123"] = "changed"; area.Clear(); snapshots.Clear();
        Equal("Rohbau", Values(run.Records[0])["Gewerk (Instanz)"]);
        Equal("EG", Values(run.Records[0])["Baubereich (Typ)"]);
        if (Values(run.Records[0]) is IDictionary<string, string> dictionary)
        {
            try { dictionary["Gewerk (Instanz)"] = "mutated"; throw new Exception("Snapshot remains mutable"); }
            catch (NotSupportedException) { }
        }
        var loaded = AvaQuantityRunCodec.FromStorageChunks(AvaQuantityRunCodec.ToStorageChunks(run));
        foreach (var index in Enumerable.Range(0, run.Records.Count))
            foreach (var entry in Values(run.Records[index])) Equal(entry.Value, Values(loaded.Records[index])[entry.Key]);
        var legacy = JsonNode.Parse(AvaQuantityRunCodec.Serialize(run))!;
        foreach (var record in legacy["Records"]!.AsArray()) record!.AsObject().Remove("GroupingValues");
        var oldRun = AvaQuantityRunCodec.Deserialize(legacy.ToJsonString());
        Equal(0, Values(oldRun.Records[0]).Count);
        Equal("Gewerk (Instanz)", oldRun.Records[0].GroupingParameter);
        Equal("Rohbau", oldRun.Records[0].GroupingValue);
    }

    public static void CodecRejectsInvalidSnapshots()
    {
        var record = new AvaQuantityRecord(Guid.NewGuid(), "B0000001", "lv1", "01", "Item", "St", "123", "", "", "", "", "", "", 1m);
        var run = new AvaQuantityRun(1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, "", "", "all", "{}", [record]);
        var json = JsonNode.Parse(AvaQuantityRunCodec.Serialize(run))!;
        foreach (var invalid in new JsonNode?[]
        {
            null,
            new JsonObject { ["Gewerk (Instanz)"] = null },
            new JsonObject { [""] = "Rohbau" },
            new JsonObject { [" \t"] = "Rohbau" },
            new JsonObject { ["Gewerk\n(Instanz)"] = "Rohbau" },
            new JsonObject { ["Gewerk (Instanz)"] = "Rohbau\nEG" },
            new JsonObject { [new string('K', 1025)] = "Rohbau" },
            new JsonObject { ["Gewerk (Instanz)"] = new string('V', 32769) }
        })
        {
            json["Records"]![0]!["GroupingValues"] = invalid;
            Reject(() => AvaQuantityRunCodec.Deserialize(json.ToJsonString()));
        }
        Reject(() => AvaQuantityRunCodec.Serialize(run with { Records = [record with { GroupingValues = null! }] }));
        Reject(() => AvaQuantityRunCodec.Serialize(run with { Records = [record with
        {
            GroupingValues = new Dictionary<string, string> { ["Gewerk (Instanz)"] = null! }
        }] }));
        var input = new Dictionary<string, string> { ["Gewerk (Instanz)"] = "Rohbau" };
        var frozen = record with { GroupingValues = input };
        input.Clear();
        Equal("Rohbau", frozen.GroupingValues["Gewerk (Instanz)"]);
        var blank = record with { GroupingValues = new Dictionary<string, string> { ["Gewerk (Instanz)"] = "" } };
        Equal("", AvaQuantityRunCodec.Deserialize(AvaQuantityRunCodec.Serialize(run with { Records = [blank] }))
            .Records[0].GroupingValues["Gewerk (Instanz)"]);
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException for invalid grouping snapshot");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}");
    }
}
