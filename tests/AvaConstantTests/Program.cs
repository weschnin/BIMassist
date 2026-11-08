using System.IO;
using System.Text.Json.Nodes;
using BIMassist.Ava.Core;

internal static class Program
{
    private static int passed;
    private static int Main(string[] args)
    {
        try
        {
            Test("Schema 1 parameter quantities migrate without changing identity or data", () =>
            {
                var original = ValidTemplate();
                var legacy = Legacy(original);
                var migrated = TemplateJson.Deserialize(legacy.ToJsonString());
                Check(migrated.SchemaVersion == 3, "schema 1 was not migrated to 3");
                Check(migrated.ProfileId == original.ProfileId && migrated.Mappings[0].Id == original.Mappings[0].Id, "migration changed identity");
                var current = JsonNode.Parse(TemplateJson.Serialize(migrated))!;
                var quantity = Quantity(current);
                Check(quantity["Kind"]!.GetValue<string>() == "Parameter" && quantity.ContainsKey("ConstantValue") && quantity["ConstantValue"] == null, "migration did not add explicit parameter mode");
                Check(JsonNode.DeepEquals(legacy, Legacy(migrated)), "legacy data changed");
            });
            Test("Fixed one piece validates and roundtrips with no quantity parameter", () =>
            {
                var p = ValidTemplate();
                p.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" };
                Check(!TemplateValidator.Validate(p).Any(d => d.Severity == DiagnosticSeverity.Error), "fixed one piece rejected");
                var copy = TemplateJson.Deserialize(TemplateJson.Serialize(p));
                var quantity = copy.Mappings[0].Positions[0].Quantity!;
                Check(quantity.Kind == QuantityKind.Constant && quantity.Source == null && quantity.ConstantValue == 1 && quantity.Unit == "St", "constant changed on roundtrip");
                Check(TemplateJson.Copy(p).Mappings[0].Positions[0].Quantity!.ConstantValue == 1, "editor copy lost constant");
            });
            Test("Quantity modes reject invalid values sources units and mixed states", () =>
            {
                var cases = new (QuantitySource Quantity, string Code)[]
                {
                    (new() { Kind = QuantityKind.Constant, Source = null, Unit = "St" }, "QUANTITY_VALUE"),
                    (new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 0, Unit = "St" }, "QUANTITY_VALUE"),
                    (new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = -1, Unit = "St" }, "QUANTITY_VALUE"),
                    (new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1.5m, Unit = "St" }, "QUANTITY_VALUE"),
                    (new() { Kind = QuantityKind.Constant, ConstantValue = 1, Unit = "St" }, "QUANTITY_MODE"),
                    (new() { Source = null, Unit = "St" }, "QUANTITY_MODE"),
                    (new() { Source = ValidTemplate().Mappings[0].Positions[0].Quantity!.Source, ConstantValue = 1, Unit = "St" }, "QUANTITY_MODE"),
                    (new() { Kind = (QuantityKind)99, Unit = "St" }, "QUANTITY_MODE"),
                    (new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "unsupported" }, "QUANTITY_UNIT"),
                    (new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "m" }, "LV_UNIT")
                };
                foreach (var item in cases)
                {
                    var p = ValidTemplate(); p.Mappings[0].Positions[0].Quantity = item.Quantity;
                    Check(TemplateValidator.Validate(p).Any(d => d.Code == item.Code && d.Severity == DiagnosticSeverity.Error), "missing " + item.Code);
                    Reject(() => TemplateEditor.EnsureValid(p));
                }
                foreach (var unit in new[] { "m", "m2", "m²", "m3", "m³", "psch", "1", "kg/m3" })
                {
                    var p = ValidTemplate(); p.LvCatalog.Entries[0] = p.LvCatalog.Entries[0] with { Unit = unit };
                    p.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1.25m, Unit = unit };
                    Check(!TemplateValidator.Validate(p).Any(d => d.Severity == DiagnosticSeverity.Error), "valid fractional " + unit + " rejected");
                }
            });
            Test("Schema 3 requires explicit unambiguous quantity fields", () =>
            {
                foreach (var mode in new[] { "Parameter", "Constant" })
                {
                    foreach (var field in new[] { "Kind", "Source", "ConstantValue", "Unit", "Expression", "Variables" })
                    {
                        var node = Current(mode); Quantity(node).Remove(field);
                        Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                    }
                    foreach (var field in new[] { "Kind", "Unit", "Expression", "Variables", mode == "Parameter" ? "Source" : "ConstantValue" })
                    {
                        var node = Current(mode); Quantity(node)[field] = null;
                        Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                    }
                    var mixed = Current(mode);
                    if (mode == "Parameter") Quantity(mixed)["ConstantValue"] = 1;
                    else Quantity(mixed)["Source"] = Quantity(Current("Parameter"))["Source"]!.DeepClone();
                    Reject(() => TemplateJson.Deserialize(mixed.ToJsonString()));
                }
                foreach (var token in new[] { "constant", " Constant", "Constant ", "Parameter, Constant", "0", "Unknown", "" })
                {
                    var node = Current("Parameter"); Quantity(node)["Kind"] = token;
                    Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                }
                var numeric = Current("Parameter"); Quantity(numeric)["Kind"] = 0;
                Reject(() => TemplateJson.Deserialize(numeric.ToJsonString()));
            });
            Test("Constant description and checklist retain DN sources without quantity reads", () =>
            {
                var p = TemplateJson.Deserialize(Current("Constant").ToJsonString());
                p.Mappings[0].Conditions.Add(new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "DN", Spec = "LENGTH", Scope = ParameterScope.Instance }, Operand = OperandKind.Numeric, Number = 100, Unit = "mm" });
                Check(p.Mappings[0].Positions[0].Quantity!.ToString() == "Konstante 1 St je passendem Element", "constant description missing");
                Check(p.Mappings[0].ToString().Contains("Konstante 1 St je passendem Element"), "mapping description missing");
                var checks = ChecklistBuilder.Build(p);
                var quantity = checks.Single(c => c.Kind == "Menge");
                Check(quantity.Requirement.Contains("Konstante 1 St je passendem Element") && quantity.Requirement.Contains("kein Mengenparameter") && !quantity.Requirement.Contains("Lesen:"), "constant requires parameter read");
                Check(quantity.AppliesTo.Contains("nur bei erfüllten UND-Bedingungen"), "constant is not branch-specific");
                Check(checks.Any(c => c.Kind == "Bedingung" && c.Requirement.Contains("DN") && c.Requirement.Contains("Quelle:") && c.Requirement.Contains("nicht auswertbar")), "DN reading requirement lost");
                Check(checks.Any(c => c.Requirement.Contains("nicht implementiert")), "checklist implies evaluation");
                var parameter = ValidTemplate().Mappings[0].Positions[0].Quantity!;
                Check(parameter.ToString() == parameter.Source!.ToString() + " | St", "parameter description lost");
                parameter.Source = null;
                Check(new MappingRule { Positions = [new() { Quantity = parameter }], Conditions = null! }.ToString().Contains("nicht definiert"), "incomplete mapping description unsafe");
            });
            Test("Invalid schema tokens and stale serializer versions are rejected", () =>
            {
                foreach (var token in new JsonNode?[] { JsonValue.Create("2"), JsonValue.Create(true), JsonValue.Create(2.5m), JsonValue.Create(4), new JsonObject(), null })
                {
                    var node = Current("Constant"); node["SchemaVersion"] = token;
                    Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                }
                var stale = ValidTemplate(); stale.SchemaVersion = 1;
                Reject(() => TemplateJson.Serialize(stale));
            });
            Test("Legacy and constant imports reject malformed nested fields without changing editor", () =>
            {
                var editor = new TemplateEditor(); editor.ImportJson(Current("Constant").ToJsonString());
                var before = TemplateJson.Serialize(editor.Document); var document = editor.Document;
                var bad = new List<string>();
                var legacy = Legacy(ValidTemplate()); Quantity(legacy)["Kind"] = "Constant"; bad.Add(legacy.ToJsonString());
                legacy = Legacy(ValidTemplate()); Quantity(legacy).Remove("Unit"); bad.Add(legacy.ToJsonString());
                legacy = Legacy(ValidTemplate()); legacy["Mappings"]![0]!["Conditions"] = null; bad.Add(legacy.ToJsonString());
                var node = Current("Constant"); node["Mappings"]!.AsArray().Add((JsonNode?)null); bad.Add(node.ToJsonString());
                node = Current("Constant"); node["BgkIdentification"] = null; bad.Add(node.ToJsonString());
                bad.Add(Current("Constant").ToJsonString().Replace("\"ConstantValue\":1", "\"ConstantValue\":1,\"ConstantValue\":2"));
                foreach (var candidate in bad) {
                    Reject(() => editor.ImportJson(candidate));
                    Check(ReferenceEquals(document, editor.Document) && before == TemplateJson.Serialize(editor.Document) && editor.IsDirty && editor.FilePath == null, "bad import changed state");
                }
            });
            if (args.Length > 0) Test("Delivered Losflansch template loads validates and exports unchanged", () =>
            {
                var p = TemplateJson.Load(args[0]); TemplateEditor.EnsureValid(p);
                Check(p.SchemaVersion == 3 && p.Mappings.Count == 13, "wrong deliverable count/schema");
                var expected = new[] { 15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250 };
                Check(p.Mappings.Select(m => m.Conditions.Single().Number).SequenceEqual(expected.Select(x => (decimal)x)), "DN sequence changed");
                foreach (var rule in p.Mappings) {
                    Check(rule.BgkKey == "44-40" && rule.TypeNumber == "110" && rule.Positions.Single().Quantity is { Kind: QuantityKind.Constant, Source: null, ConstantValue: 1, Unit: "St" }, "wrong rule/quantity");
                    var dn = rule.Conditions.Single();
                    Check(dn.Source.Name == "AVA_DN1" && dn.Unit == "mm" && dn.Source.Scope == ParameterScope.Type && dn.Operator == ConditionOperator.Equal, "wrong DN condition");
                    var lv = p.LvCatalog.Entries.Single(l => l.Id == rule.Positions.Single().LvItemId);
                    Check(lv.Text == $"Losflansch Typ 02 DN {dn.Number} PN 10 Form A, Edelstahl 1.4404/1.4571" && lv.Unit == "St", "wrong LV");
                }
                Check(ChecklistBuilder.Build(p).Count(c => c.Kind == "Menge" && c.Requirement.Contains("kein Mengenparameter")) == 13, "constant checklist incomplete");
                var editor = new TemplateEditor(); editor.Open(args[0]);
                var temp = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "ava-constant-roundtrip-" + Guid.NewGuid() + ".json");
                try { editor.Export(temp); Check(TemplateJson.Serialize(TemplateJson.Load(temp)) == TemplateJson.Serialize(p), "export changed template"); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                foreach (var finding in TemplateValidator.Validate(p)) Console.WriteLine(finding);
            });
            Console.WriteLine($"PASS {passed} tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static AvaTemplate ValidTemplate()
    {
        var p = new AvaTemplate { ReviewScope = "Rohrzubehör einschließlich unmarkierter Elemente" };
        p.BgkCatalog.Entries.Add(new("09-25", "Losflansch", "", [new("0100", "DN 100")]));
        p.LvCatalog.Entries.Add(new("lv", "boq", ["01", "0010"], "01.0010", "Losflansch DN 100", "", "St"));
        p.Mappings.Add(new() { BgkKey = "09-25", TypeNumber = "0100", Positions = [new() { LvItemId = "lv", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "AVA_Anzahl", Spec = "NUMBER", Scope = ParameterScope.Instance }, Unit = "St" } }] });
        return p;
    }
    private static JsonNode Current(string mode)
    {
        var node = JsonNode.Parse(TemplateJson.Serialize(ValidTemplate()))!;
        if (mode == "Constant") { Quantity(node)["Kind"] = mode; Quantity(node)["Source"] = null; Quantity(node)["ConstantValue"] = 1; }
        return node;
    }
    private static JsonObject Quantity(JsonNode node) => (node["Mappings"]![0]!["Positions"] is JsonArray positions ? positions[0]!["Quantity"] : node["Mappings"]![0]!["Quantity"])!.AsObject();
    private static JsonNode Legacy(AvaTemplate template)
    {
        var node = JsonNode.Parse(TemplateJson.Serialize(template))!;
        node["SchemaVersion"] = 1;
        foreach (var rule in node["Mappings"]!.AsArray())
        {
            var position = rule!["Positions"]![0]!;
            rule["LvItemId"] = position["LvItemId"]!.DeepClone(); rule["Quantity"] = position["Quantity"]?.DeepClone(); rule.AsObject().Remove("Positions");
            if (rule["Quantity"] is JsonObject quantity) { quantity.Remove("Kind"); quantity.Remove("ConstantValue"); quantity.Remove("Expression"); quantity.Remove("Variables"); }
        }
        return node;
    }
    private static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action test) { try { test(); } catch (InvalidDataException) { return; } throw new Exception("Expected InvalidDataException"); }
}
