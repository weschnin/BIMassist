using BIMassist.Ava.Core;

internal static class Program
{
    private static int passed, failed;
    private static void Main()
    {
        Test("settings round-trip exact presentation state and empty defaults", SettingsRoundTrip);
        Test("settings reject corrupt schemas, bounds, duplicate keys and nonfinite widths", SettingsValidation);
        DisplayTests.Run();
        XlsxTests.Run();
        Console.WriteLine($"{passed} passed; {failed} failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }

    private static void SettingsRoundTrip()
    {
        var expected = new AvaQuantityViewSettings
        {
            Columns = [new("Result", 1, true, 125.5), new("RevitElementId", 0, false, 201)],
            Sorts = [new("Result", true), new("RevitElementId", false)],
            Grouping = "Geschoss (Instanz): C:/literal is not a file path", VisibleOnly = true, X31UseRevitElementId = true
        };
        var restored = AvaQuantityViewSettingsCodec.Deserialize(AvaQuantityViewSettingsCodec.Serialize(expected));
        Equal(1, restored.SchemaVersion);
        Equal(true, expected.Columns.SequenceEqual(restored.Columns));
        Equal(true, expected.Sorts.SequenceEqual(restored.Sorts));
        Equal(expected.Grouping, restored.Grouping);
        Equal(true, restored.VisibleOnly);
        Equal(true, restored.X31UseRevitElementId);
        var defaults = AvaQuantityViewSettingsCodec.Deserialize("{}");
        Equal(1, defaults.SchemaVersion);
        Equal(0, defaults.Columns.Count);
        Equal(0, defaults.Sorts.Count);
        Equal("", defaults.Grouping);
        Equal(false, defaults.VisibleOnly);
        Equal(false, defaults.X31UseRevitElementId);
    }

    private static void SettingsValidation()
    {
        foreach (var invalid in new AvaQuantityViewSettings[]
        {
            new() { SchemaVersion = 0 }, new() { SchemaVersion = 2 },
            new() { Columns = null! }, new() { Sorts = null! }, new() { Grouping = null! },
            new() { Grouping = new string('x', 4097) },
            new() { Columns = [null!] }, new() { Sorts = [null!] },
            new() { Columns = [new("", 0, true, 80)] },
            new() { Columns = [new("a", -1, true, 80)] },
            new() { Columns = [new("a", 256, true, 80)] },
            new() { Columns = [new(new string('k', 257), 0, true, 80)] },
            new() { Columns = [new("a", 0, true, 80), new("a", 1, false, 80)] },
            new() { Columns = [new("a", 0, true, 80), new("b", 0, true, 80)] },
            new() { Sorts = [new("a", true), new("a", false)] },
            new() { Sorts = [new(" ", false)] },
            new() { Columns = Enumerable.Range(0, 257).Select(i => new AvaQuantityColumnSetting("k" + i, i, true, 80)).ToArray() },
            new() { Sorts = Enumerable.Range(0, 257).Select(i => new AvaQuantitySortSetting("k" + i, false)).ToArray() }
        }) Reject<InvalidDataException>(() => AvaQuantityViewSettingsCodec.Serialize(invalid));
        foreach (var width in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1, 10001 })
            Reject<InvalidDataException>(() => AvaQuantityViewSettingsCodec.Serialize(new() { Columns = [new("a", 0, true, width)] }));
        foreach (var json in new[] { "null", "{", "[]", "{\"SchemaVersion\":9}", "{\"Columns\":null}",
            "{\"Sorts\":[{\"Key\":\"a\"},{\"Key\":\"a\"}]}", "{\"Grouping\":null}",
            "{\"Columns\":[{\"Key\":\"a\",\"DisplayIndex\":0,\"Width\":\"NaN\"}]}",
            new string(' ', 1024 * 1024 + 1) })
            Reject<InvalidDataException>(() => AvaQuantityViewSettingsCodec.Deserialize(json));
        // Missing optional fields and fields from a future UI do not change schema-1 defaults.
        Equal(0, AvaQuantityViewSettingsCodec.Deserialize("{\"LegacyColumnPicker\":true}").Columns.Count);
    }

    internal static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected '{expected}'; actual '{actual}'");
    }
    internal static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
