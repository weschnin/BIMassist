using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Require(Environment.Version.Major == 8, "Smoke must actually run on .NET 8, not roll forward to .NET 10.");
            Require(typeof(Window).Assembly.GetName().Version!.Major == 8, "WPF must actually load version 8.");
            Console.WriteLine($"RUNTIME {RuntimeInformation.FrameworkDescription}; WPF {typeof(Window).Assembly.GetName().Version}");
            Console.WriteLine($"PARSER {typeof(TemplateJson).Assembly.Location}");
            if (args.Length > 0)
            {
                Require(typeof(TemplateJson).Assembly.GetName().Name == "BIMassist", "Expected production DLL, not linked test sources.");
                var expected = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args.Single()));
                var actual = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(TemplateJson).Assembly.Location));
                Require(expected.SequenceEqual(actual), "Loaded DLL differs from the requested production artifact.");
                Console.WriteLine("ARTIFACT SHA256 " + Convert.ToHexString(actual));
            }
            foreach (int schema in new[] { 1, 2, 3 })
            {
                var baseline = Fixture(schema);
                var imported = TemplateJson.Deserialize(baseline.ToJsonString());
                Require(imported.SchemaVersion == 3 && imported.Mappings.Count == 1, $"schema {schema} migration/roundtrip failed");
                foreach (var path in ObjectPaths(baseline))
                {
                    var obj = At(baseline, path).AsObject();
                    foreach (string key in obj.Select(p => p.Key))
                    {
                        var missing = baseline.DeepClone(); At(missing, path).AsObject().Remove(key);
                        Reject(missing.ToJsonString(), $"schema {schema} missing {string.Join('/', path)}/{key}");
                        bool nullable = key == "Quantity" || key == "ConstantValue" ||
                            (key == "Source" && obj["Kind"]?.GetValue<string>() is "Constant" or "Formula");
                        if (!nullable)
                        {
                            var nil = baseline.DeepClone(); At(nil, path)[key] = null;
                            Reject(nil.ToJsonString(), $"schema {schema} null {string.Join('/', path)}/{key}");
                        }
                    }
                    var unknown = baseline.DeepClone(); At(unknown, path)["Unknown"] = true;
                    Reject(unknown.ToJsonString(), $"schema {schema} unknown {string.Join('/', path)}");
                    var first = obj.First(); string fragment = obj.ToJsonString();
                    string duplicate = "{" + JsonSerializer.Serialize(first.Key) + ":" + first.Value!.ToJsonString() + "," + fragment[1..];
                    Reject(baseline.ToJsonString().Replace(fragment, duplicate), $"schema {schema} duplicate {string.Join('/', path)}");
                }
                var draft = baseline.DeepClone();
                var rule = draft["Mappings"]![0]!;
                if (schema == 3) rule["Positions"]![0]!["Quantity"] = null;
                else rule["Quantity"] = null;
                Require(TemplateJson.Deserialize(draft.ToJsonString()).Mappings[0].Positions[0].Quantity == null, "legal null draft rejected");
            }
            // Preserve the .NET 9 option's write/copy guards as well as import validation.
            var invalid = TemplateJson.Deserialize(Fixture(3).ToJsonString()); invalid.Name = null!;
            RejectNullWrite(() => TemplateJson.Serialize(invalid), "non-nullable profile name");
            RejectNullWrite(() => TemplateJson.Copy(invalid), "copy of non-nullable profile name");
            invalid = TemplateJson.Deserialize(Fixture(3).ToJsonString()); invalid.BgkCatalog = invalid.BgkCatalog with { Source = new(null!, "", "") };
            RejectNullWrite(() => TemplateJson.Serialize(invalid), "non-nullable constructor member");
            invalid = TemplateJson.Deserialize(Fixture(3).ToJsonString()); invalid.Mappings[0].Conditions = null!;
            RejectNullWrite(() => TemplateJson.Copy(invalid), "non-nullable nested collection");
            // Optional snapshot metadata must stay optional, including in historical contracts.
            var snapshot = Fixture(3); snapshot["LvCatalog"]!["Source"]!["X82SnapshotBase64"] = "c25hcHNob3Q=";
            Require(TemplateJson.Deserialize(snapshot.ToJsonString()).LvCatalog.Source.X82SnapshotBase64 == "c25hcHNob3Q=", "optional snapshot lost");
            var invalidShape = Fixture(3); invalidShape["Mappings"]![0]!["Positions"]!.AsArray().Add((JsonNode?)null);
            Reject(invalidShape.ToJsonString(), "null position");
            var temporary = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new Exception("TMPDIR required"), "runtime-smoke-" + Guid.NewGuid());
            Directory.CreateDirectory(temporary);
            try
            {
                string file = Path.Combine(temporary, "template.json");
                TemplateJson.Save(file, TemplateJson.Deserialize(Fixture(3).ToJsonString()));
                var roundtrip = TemplateJson.Load(file);
                Require(roundtrip.Mappings[0].Positions.Count == 3, "file roundtrip lost positions");
                var window = new AvaTemplateWindow(new AvaWorkspace(Path.Combine(temporary, "workspace.json")));
                try
                {
                    window.Measure(new Size(1200, 820)); window.Arrange(new Rect(0, 0, 1200, 820)); window.UpdateLayout();
                    Require(window.FindName("EditorTabs") is System.Windows.Controls.TabControl { Items.Count: 5 }, "WPF editor did not initialize");
                    // The production position dialog is intentionally internal; test the real
                    // artifact without widening its API merely for the external smoke harness.
                    var positionType = typeof(AvaTemplateWindow).Assembly.GetType("BIMassist.Ava.Views.PositionEditorWindow", throwOnError: true)!;
                    var position = (Window)Activator.CreateInstance(positionType, roundtrip, roundtrip.Mappings[0].Positions[0])!;
                    try { Require(((MappingPosition)positionType.GetMethod("BuildPosition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(position, null)!).Quantity!.ConstantValue == 1, "WPF quantity edit changed value"); }
                    finally { position.Close(); }
                }
                finally { window.Close(); }
            }
            finally { Directory.Delete(temporary, true); }
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "RevitAPI" or "RevitAPIUI"), "File-only smoke must not load Revit APIs.");
            Console.WriteLine($"PASS {checks} .NET 8 parser/shape/WPF smoke assertions (not Revit runtime acceptance)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static JsonNode Fixture(int schema)
    {
        var source = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "Length", Spec = "LENGTH", Scope = ParameterScope.Instance };
        var template = new AvaTemplate { ReviewScope = "test" };
        template.BgkCatalog.Entries.Add(new("01", "BGK", "", [new("001", "type")]));
        template.LvCatalog.Entries.Add(new("item", "boq", ["01"], "01", "text", "description", "m"));
        template.ParameterCatalog.Entries.Add(new(Guid.NewGuid(), "Length", "LENGTH", "group"));
        template.Mappings.Add(new() { BgkKey = "01", TypeNumber = "001", Conditions = [new() { Source = source, Operand = OperandKind.Numeric, Number = 1, Unit = "m" }], Positions = [
            new() { LvItemId = "item", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" } },
            new() { LvItemId = "item", Quantity = new() { Source = source, Unit = "m" } },
            new() { LvItemId = "item", Quantity = new() { Kind = QuantityKind.Formula, Source = null, Expression = "x * 2", Unit = "m", Variables = [new() { Name = "x", Source = source, Unit = "m" }] } }
        ] });
        var root = JsonNode.Parse(TemplateJson.Serialize(template))!;
        if (schema < 3)
        {
            var rule = root["Mappings"]![0]!; var position = rule["Positions"]![schema == 1 ? 1 : 0]!;
            rule["LvItemId"] = position["LvItemId"]!.DeepClone(); rule["Quantity"] = position["Quantity"]!.DeepClone();
            rule.AsObject().Remove("Positions"); var quantity = rule["Quantity"]!.AsObject();
            quantity.Remove("Expression"); quantity.Remove("Variables");
            if (schema == 1) { quantity.Remove("Kind"); quantity.Remove("ConstantValue"); }
            root["SchemaVersion"] = schema;
        }
        return root;
    }
    private static IEnumerable<string[]> ObjectPaths(JsonNode? node, string[]? path = null)
    {
        path ??= [];
        if (node is JsonObject obj)
        {
            yield return path;
            foreach (var property in obj)
                foreach (var child in ObjectPaths(property.Value, [..path, property.Key])) yield return child;
        }
        if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                foreach (var child in ObjectPaths(array[i], [..path, i.ToString()])) yield return child;
    }
    private static JsonNode At(JsonNode root, string[] path)
    {
        foreach (string key in path) root = root is JsonArray ? root[int.Parse(key)]! : root[key]!;
        return root;
    }
    private static void Reject(string json, string label)
    {
        try { TemplateJson.Deserialize(json); }
        catch (InvalidDataException) { checks++; return; }
        throw new Exception("Accepted " + label);
    }
    private static void RejectNullWrite(Action action, string label)
    {
        try { action(); }
        catch (JsonException) { checks++; return; }
        throw new Exception("Accepted " + label);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
