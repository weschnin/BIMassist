using Autodesk.Revit.DB;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;

internal static class GroupingSnapshotTests
{
    public static void Run()
    {
        CapturesMeasuredValuesOnly();
        Console.WriteLine("PASS grouping snapshots capture independent instance/type values, absences and measured-only scope");
        OmitsUnsafeOptions();
        Console.WriteLine("PASS grouping snapshots omit whole ambiguous, incompatible or unreadable options without scope fallback");
        try
        {
            Read(new Document(), "404");
            throw new Exception("Missing measured model element was silently ignored");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is System.IO.InvalidDataException) { }
        Console.WriteLine("PASS grouping snapshots reject missing measured element identities");
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Read(Document document, params string[] ids)
    {
        var method = typeof(RevitQuantityReader).GetMethod("ReadGroupingSnapshots")
            ?? throw new Exception("Missing ReadGroupingSnapshots API");
        Check(method.ReturnType == typeof(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>), "Wrong snapshot return contract");
        return (IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>)method.Invoke(null, [document, ids])!;
    }

    private static void CapturesMeasuredValuesOnly()
    {
        var document = new Document();
        var first = new FamilyInstance { Id = new(1), TypeId = new(11) };
        var second = new FamilyInstance { Id = new(2), TypeId = new(12) };
        var third = new FamilyInstance { Id = new(3), TypeId = new(13) };
        var typeA = new FamilySymbol { Id = new(11) };
        var typeB = new FamilySymbol { Id = new(12) };
        var unmeasured = new FamilyInstance { Id = new(4), TypeId = new(11) };
        document.Elements.AddRange([first, second, third, typeA, typeB, unmeasured]);
        first.Parameters.Add(new("Gewerk", SpecTypeId.String.Text, "Rohbau", StorageType.String));
        second.Parameters.Add(new("Gewerk", SpecTypeId.String.Text, "Ausbau", StorageType.String));
        first.Parameters.Add(new("Baubereich", SpecTypeId.String.Text, "Instance area", StorageType.String));
        typeA.Parameters.Add(new("Baubereich", SpecTypeId.String.Text, "EG", StorageType.String));
        typeB.Parameters.Add(new("Baubereich", SpecTypeId.String.Text, "OG", StorageType.String));
        first.Parameters.Add(new("Empty", SpecTypeId.String.Text, null, StorageType.String));
        second.Parameters.Add(new("Empty", SpecTypeId.String.Text, "   ", StorageType.String));
        first.Parameters.Add(new("NoValue", SpecTypeId.String.Text, "not present", StorageType.String) { HasValue = false });
        first.Parameters.Add(new("Number", SpecTypeId.Number, 2d, StorageType.Double));
        unmeasured.Parameters.Add(new("OnlyUnmeasured", SpecTypeId.String.Text, "ignored", StorageType.String));
        unmeasured.Parameters.Add(new("Gewerk", SpecTypeId.Number, 2d, StorageType.Double));
        var snapshots = Read(document, "1", "2", "3", "1");
        Check(snapshots["Gewerk (Instanz)"]["1"] == "Rohbau" && snapshots["Gewerk (Instanz)"]["2"] == "Ausbau", "Instance values were reused across elements");
        Check(snapshots["Baubereich (Typ)"]["1"] == "EG" && snapshots["Baubereich (Typ)"]["2"] == "OG", "Type values were reused across elements");
        Check(snapshots["Baubereich (Instanz)"]["1"] == "Instance area", "Instance/type scopes collapsed");
        Check(snapshots["Baubereich (Instanz)"]["2"] == "" && snapshots["Baubereich (Typ)"]["3"] == "", "Missing scope borrowed a value");
        Check(snapshots["Gewerk (Instanz)"]["3"] == "", "Missing parameter was fabricated");
        Check(snapshots["Empty (Instanz)"].Values.All(v => v == "") && snapshots["NoValue (Instanz)"].Values.All(v => v == ""), "Empty or valueless text was fabricated");
        Check(!snapshots.ContainsKey("Number (Instanz)") && !snapshots.ContainsKey("OnlyUnmeasured (Instanz)"), "Unmeasured or numeric parameter became a grouping option");
        Check(snapshots.Values.All(values => values.Count == 3 && !values.ContainsKey("4")), "Unmeasured IDs leaked into snapshots");
        Check(document.CollectionPasses == 1, "Reader rescanned the model for each grouping parameter");
        first.Parameters.Clear(); typeA.Parameters.Clear();
        Check(snapshots["Gewerk (Instanz)"]["1"] == "Rohbau" && snapshots["Baubereich (Typ)"]["1"] == "EG", "Snapshot retains live model values");
        Check(Read(document).Count == 0, "Empty measured scope offered model grouping parameters");
    }

    private static void OmitsUnsafeOptions()
    {
        var failures = new List<string>();
        foreach (var scope in Enum.GetValues<ParameterScope>())
        foreach (var scenario in new[] { "duplicate", "mixed duplicate", "wrong spec", "wrong storage", "valueless wrong spec",
            "line break", "format", "oversized value", "unreadable value", "unreadable state", "unreadable spec", "invalid name" })
        {
            var document = new Document();
            var first = new FamilyInstance { Id = new(1), TypeId = new(11) };
            var second = new FamilyInstance { Id = new(2), TypeId = new(12) };
            var typeA = new FamilySymbol { Id = new(11) };
            var typeB = new FamilySymbol { Id = new(12) };
            document.Elements.AddRange([first, second, typeA, typeB]);
            var ownerA = scope == ParameterScope.Instance ? (Element)first : typeA;
            var ownerB = scope == ParameterScope.Instance ? (Element)second : typeB;
            var name = scenario == "invalid name" ? "Bad\nName" : "Unsafe";
            ownerA.Parameters.Add(new(name, SpecTypeId.String.Text, "good", StorageType.String));
            first.Parameters.Add(new("Safe", SpecTypeId.String.Text, "safe", StorageType.String));
            // Opposite scope is deliberately usable; never borrow it to rescue the unsafe option.
            (scope == ParameterScope.Instance ? (Element)typeB : second).Parameters.Add(new(name, SpecTypeId.String.Text, "wrong fallback", StorageType.String));
            Parameter Text(string value) => new(name, SpecTypeId.String.Text, value, StorageType.String);
            switch (scenario)
            {
                case "duplicate": ownerB.Parameters.AddRange([Text("same"), Text("same")]); break;
                case "mixed duplicate": ownerB.Parameters.AddRange([Text("good"), new(name, SpecTypeId.Number, 2d, StorageType.Double)]); break;
                case "wrong spec": ownerB.Parameters.Add(new(name, SpecTypeId.Number, "misleading", StorageType.String)); break;
                case "wrong storage": ownerB.Parameters.Add(new(name, SpecTypeId.String.Text, 2d, StorageType.Double)); break;
                case "valueless wrong spec": ownerB.Parameters.Add(new(name, SpecTypeId.Number, null, StorageType.String)); break;
                case "line break": ownerB.Parameters.Add(Text("bad\nlabel")); break;
                case "format": ownerB.Parameters.Add(Text("bad\u200blabel")); break;
                case "oversized value": ownerB.Parameters.Add(Text(new string('X', 32769))); break;
                case "unreadable value": ownerB.Parameters.Add(new(name, SpecTypeId.String.Text, "bad", StorageType.String) { ReadError = new InvalidOperationException("read failure") }); break;
                case "unreadable state": ownerB.Parameters.Add(new(name, SpecTypeId.String.Text, "bad", StorageType.String) { ValueStateError = new InvalidOperationException("state failure") }); break;
                case "unreadable spec":
                    var parameter = Text("bad"); parameter.Definition.DataTypeError = new InvalidOperationException("spec failure"); ownerB.Parameters.Add(parameter); break;
                case "invalid name": ownerB.Parameters.Add(Text("good")); break;
            }
            try
            {
                var snapshots = Read(document, "1", "2");
                Check(!snapshots.ContainsKey(new GroupingParameterOption(name, scope).DisplayLabel), "Unsafe option was offered");
                Check(snapshots["Safe (Instanz)"]["1"] == "safe", "Independent safe option was lost");
            }
            catch (Exception ex) { failures.Add(scope + "/" + scenario + ": " + ex.GetBaseException().Message); }
        }
        Check(failures.Count == 0, string.Join("; ", failures));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
