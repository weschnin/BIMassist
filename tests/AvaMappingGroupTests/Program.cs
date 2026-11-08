using System.Text.Json.Nodes;
using BIMassist.Ava.Core;

internal static class Program
{
    static int passed;
    static int Main()
    {
        try
        {
            Test("Schema 3 grouped public model has initialized required fields", () => {
                Check(new AvaTemplate().SchemaVersion == 3, "new templates must use schema 3");
                var ruleType = typeof(MappingRule);
                Check(ruleType.GetProperty("LvItemId") == null && ruleType.GetProperty("Quantity") == null, "legacy single-position fields remain");
                var positions = ruleType.GetProperty("Positions");
                Check(positions != null && positions.GetValue(new MappingRule()) is System.Collections.IList { Count: 0 }, "positions not initialized");
                var position = ruleType.Assembly.GetType("BIMassist.Ava.Core.MappingPosition");
                Check(position is { IsPublic: true, IsSealed: true }, "public sealed MappingPosition missing");
                var variable = ruleType.Assembly.GetType("BIMassist.Ava.Core.FormulaVariable");
                Check(variable is { IsPublic: true, IsSealed: true }, "public sealed FormulaVariable missing");
                Check(Enum.GetNames<QuantityKind>().Contains("Formula"), "formula kind missing");
                foreach (var (type, names) in new[] { (ruleType, new[]{ "Positions" }), (position!, new[]{ "Id", "LvItemId", "Quantity" }), (variable!, new[]{ "Name", "Source", "Unit" }), (typeof(QuantitySource), new[]{ "Expression", "Variables" }) })
                    foreach (var name in names)
                        Check(type.GetProperty(name)!.IsDefined(typeof(System.Text.Json.Serialization.JsonRequiredAttribute), false), type.Name + "." + name + " must be required");
            });
            Test("Two sibling positions retain independent quantities and validate without group conflict", () => {
                var template = ValidTemplate();
                var serialized = TemplateJson.Serialize(template);
                var copy = TemplateJson.Deserialize(serialized);
                Check(copy.Mappings.Single().Positions.Count == 2, "positions lost");
                Check(copy.Mappings[0].Positions[0].Quantity!.Kind == QuantityKind.Constant && copy.Mappings[0].Positions[1].Quantity!.Source!.Name == "Length", "quantities conflated");
                Check(TemplateJson.Serialize(copy) == serialized, "roundtrip changed grouped mapping");
                var diagnostics = TemplateValidator.Validate(copy);
                Check(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), string.Join("; ", diagnostics));
                Check(!diagnostics.Any(d => d.Code == "MULTIPLE_RULES"), "siblings are not conflicting groups");
            });
            Test("Positions require identities references and nonempty lists with transactional upsert", () => {
                var template = ValidTemplate(); var group = template.Mappings[0];
                var editor = new TemplateEditor(); editor.ImportJson(TemplateJson.Serialize(template));
                var before = TemplateJson.Serialize(editor.Document); var document = editor.Document;
                group.Positions[1].Id = group.Positions[0].Id;
                Check(TemplateValidator.Validate(template).Any(d => d.Code == "DUPLICATE_KEY"), "duplicate sibling position ID accepted");
                Reject(() => editor.UpsertMapping(group));
                Check(ReferenceEquals(document, editor.Document) && TemplateJson.Serialize(editor.Document) == before, "invalid upsert changed state");
                group.Positions[1].Id = Guid.Empty;
                Check(TemplateValidator.Validate(template).Any(d => d.Code == "POSITION_ID"), "empty position ID accepted");
                group.Positions[1].Id = Guid.NewGuid(); group.Positions[1].LvItemId = "missing";
                Check(TemplateValidator.Validate(template).Any(d => d.Code == "LV_REFERENCE" && d.Path.Contains(group.Positions[1].Id.ToString())), "position-specific LV reference diagnostic missing");
                group.Positions.Clear();
                Check(TemplateValidator.Validate(template).Any(d => d.Code == "POSITIONS_EMPTY"), "empty group accepted");
                template = ValidTemplate(); var second = TemplateJson.Copy(template.Mappings[0]); second.Id = Guid.NewGuid();
                template.Mappings.Add(second);
                Check(TemplateValidator.Validate(template).Any(d => d.Code == "DUPLICATE_KEY"), "duplicate IDs across groups accepted");
                foreach (var position in second.Positions) position.Id = Guid.NewGuid();
                Check(TemplateValidator.Validate(template).Count(d => d.Code == "MULTIPLE_RULES") == 1, "multiple groups warning lost");
                Check(!TemplateValidator.Validate(template).Any(d => d.Severity == DiagnosticSeverity.Error), "independent groups rejected");
            });
            Test("Genuine schema 1 and 2 migrate deterministically without merging groups or changing snapshots", () => {
                foreach (int schema in new[] { 1, 2 }) {
                    var legacy = Legacy(schema);
                    var migrated = TemplateJson.Deserialize(legacy.ToJsonString());
                    Check(migrated.SchemaVersion == 3, "legacy schema not migrated");
                    Check(migrated.ProfileId.ToString() == legacy["ProfileId"]!.GetValue<string>(), "profile ID changed");
                    Check(migrated.Mappings.Count == 2 && migrated.Mappings.All(m => m.Positions.Count == 1 && m.Positions[0].Id == m.Id), "migration merged rules or generated unstable position IDs");
                    var current = JsonNode.Parse(TemplateJson.Serialize(migrated))!;
                    foreach (string catalog in new[] { "BgkCatalog", "LvCatalog", "ParameterCatalog" }) Check(JsonNode.DeepEquals(legacy[catalog], current[catalog]), "snapshot or source hash changed");
                    Check(TemplateJson.Serialize(TemplateJson.Deserialize(legacy.ToJsonString())) == TemplateJson.Serialize(migrated), "migration not deterministic");
                    foreach (var node in current["Mappings"]!.AsArray()) {
                        var rule = node!.AsObject(); var position = rule["Positions"]![0]!.AsObject();
                        rule["LvItemId"] = position["LvItemId"]!.DeepClone(); rule["Quantity"] = position["Quantity"]?.DeepClone(); rule.Remove("Positions");
                        if (rule["Quantity"] is JsonObject q) { q.Remove("Expression"); q.Remove("Variables"); if (schema == 1) { q.Remove("Kind"); q.Remove("ConstantValue"); } }
                    }
                    current["SchemaVersion"] = schema;
                    Check(JsonNode.DeepEquals(current, legacy), "legacy data changed beyond migration fields");
                }
            });
            Test("Legacy shapes are completely checked before migration can discard or supply fields", () => {
                var failures = new List<string>();
                foreach (int schema in new[] { 1, 2 }) {
                    var baseline = Legacy(schema);
                    var changes = new List<(string Label, Action<JsonNode> Change)> {
                        ("new positions", n => n["Mappings"]![0]!["Positions"] = new JsonArray()),
                        ("new expression", n => n["Mappings"]![0]!["Quantity"]!["Expression"] = "discarded"),
                        ("new variables", n => n["Mappings"]![0]!["Quantity"]!["Variables"] = new JsonArray()),
                        ("missing quantity", n => n["Mappings"]![0]!.AsObject().Remove("Quantity")),
                        ("bad quantity type", n => n["Mappings"]![0]!["Quantity"] = false),
                        ("bad mapping type", n => n["Mappings"]![0] = new JsonArray()),
                        ("new formula mode", n => n["Mappings"]![0]!["Quantity"]!["Kind"] = "Formula")
                    };
                    foreach (var (label, change) in changes) { var candidate = baseline.DeepClone(); change(candidate); MustReject(candidate.ToJsonString(), schema + ": " + label); }
                    foreach (var path in ObjectPaths(baseline)) {
                        var obj = At(baseline, path).AsObject();
                        foreach (var key in obj.Select(p => p.Key).ToArray()) {
                            var missing = baseline.DeepClone(); At(missing, path).AsObject().Remove(key); MustReject(missing.ToJsonString(), schema + " missing " + string.Join('/', path) + "/" + key);
                            bool legalNull = (key == "Quantity" && path.Length == 2) || (key == "ConstantValue" && schema == 2 && obj["Kind"]?.GetValue<string>() == "Parameter") || (key == "Source" && schema == 2 && obj["Kind"]?.GetValue<string>() == "Constant");
                            if (!legalNull) { var nil = baseline.DeepClone(); At(nil, path)[key] = null; MustReject(nil.ToJsonString(), schema + " null " + string.Join('/', path) + "/" + key); }
                        }
                        var unknown = baseline.DeepClone(); At(unknown, path)["Unknown"] = true; MustReject(unknown.ToJsonString(), schema + " unknown " + string.Join('/', path));
                        var first = obj.First(); var fragment = obj.ToJsonString();
                        var duplicate = "{" + System.Text.Json.JsonSerializer.Serialize(first.Key) + ":" + (first.Value?.ToJsonString() ?? "null") + "," + fragment[1..];
                        MustReject(baseline.ToJsonString().Replace(fragment, duplicate), schema + " duplicate " + string.Join('/', path));
                    }
                    var nilQuantity = baseline.DeepClone(); nilQuantity["Mappings"]![0]!["Quantity"] = null;
                    Check(TemplateJson.Deserialize(nilQuantity.ToJsonString()).Mappings[0].Positions[0].Quantity == null, "legal legacy draft quantity rejected");
                }
                Check(failures.Count == 0, string.Join(Environment.NewLine, failures));
                void MustReject(string candidate, string label) { try { Reject(() => TemplateJson.Deserialize(candidate)); } catch (Exception) { failures.Add("Accepted " + label); } }
            });
            Test("Formula quantities bind exact declared numeric sources and input and output units", () => {
                var template = FormulaTemplate();
                var json = TemplateJson.Serialize(template);
                var copy = TemplateJson.Deserialize(json); TemplateEditor.EnsureValid(copy);
                Check(TemplateJson.Serialize(copy) == json, "formula bindings changed on roundtrip");
                var formula = copy.Mappings[0].Positions[1].Quantity!;
                Check(formula.Kind == QuantityKind.Formula && formula.Expression == "Length * Width" && formula.Variables.Count == 2, "formula data missing");
                Check(formula.Variables[0].Source.SharedGuid == template.ParameterCatalog.Entries[0].Guid && formula.Variables[0].Source.Scope == ParameterScope.Instance, "explicit source lost");
                Check(formula.Variables[0].Unit == "cm" && formula.Unit == "m²", "declared units rewritten or inferred");
                Check(UnitCatalog.Allows("AREA", "m²") && UnitCatalog.Allows("VOLUME", "m³"), "published unit aliases unavailable");
                var quantity = template.Mappings[0].Positions[0].Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Count", Spec = "NUMBER", Scope = ParameterScope.Instance }, Unit = "St" };
                Check(quantity.ToString() == quantity.Source!.ToString() + " | St", "parameter description lost declared unit");
            });
            Test("Formula diagnostics reject ambiguous modes bad syntax bindings sources and units without inference", () => {
                var cases = new (string Label, Action<QuantitySource> Change, string Code)[] {
                    ("syntax", q => q.Expression = "Length ** Width", "FORMULA"),
                    ("missing binding", q => q.Variables.RemoveAt(1), "FORMULA"),
                    ("duplicate binding", q => q.Variables.Add(TemplateJson.Copy(q.Variables[0])), "FORMULA"),
                    ("unused binding", q => q.Variables.Add(new() { Name = "Unused", Source = TemplateJson.Copy(q.Variables[0].Source), Unit = "cm" }), "FORMULA"),
                    ("wrong case", q => q.Variables[0].Name = "length", "FORMULA"),
                    ("wrong GUID", q => q.Variables[0].Source.SharedGuid = Guid.NewGuid(), "PARAMETER_REFERENCE"),
                    ("no name fallback", q => q.Variables[0].Source.Name = "Other", "PARAMETER_NAME"),
                    ("wrong spec", q => q.Variables[0].Source.Spec = "NUMBER", "PARAMETER_SPEC"),
                    ("invalid scope", q => q.Variables[0].Source.Scope = (ParameterScope)99, "PARAMETER_SOURCE"),
                    ("nonnumeric", q => { q.Variables[1].Source.Spec = "TEXT"; q.Variables[1].Unit = ""; }, "FORMULA"),
                    ("input unit", q => q.Variables[1].Unit = "St", "FORMULA"),
                    ("output unit", q => q.Unit = "unsupported", "QUANTITY_UNIT"),
                    ("LV unit", q => q.Unit = "m", "LV_UNIT"),
                    ("direct source", q => q.Source = new(), "QUANTITY_MODE"),
                    ("constant", q => q.ConstantValue = 1, "QUANTITY_MODE"),
                    ("parameter expression", q => { q.Kind = QuantityKind.Parameter; q.Source = TemplateJson.Copy(q.Variables[0].Source); q.Variables.Clear(); }, "QUANTITY_MODE"),
                    ("parameter variables", q => { q.Kind = QuantityKind.Parameter; q.Source = TemplateJson.Copy(q.Variables[0].Source); q.Expression = ""; }, "QUANTITY_MODE"),
                    ("constant expression", q => { q.Kind = QuantityKind.Constant; q.ConstantValue = 1; q.Variables.Clear(); }, "QUANTITY_MODE"),
                    ("constant variables", q => { q.Kind = QuantityKind.Constant; q.ConstantValue = 1; q.Expression = ""; }, "QUANTITY_MODE")
                };
                var failures = new List<string>();
                foreach (var (label, change, code) in cases) {
                    var template = FormulaTemplate(); change(template.Mappings[0].Positions[1].Quantity!);
                    if (!TemplateValidator.Validate(template).Any(d => d.Severity == DiagnosticSeverity.Error && d.Code == code)) failures.Add(label + ": missing " + code);
                    try { Reject(() => TemplateEditor.EnsureValid(template)); } catch (Exception) { failures.Add(label + ": EnsureValid accepted"); }
                }
                Check(failures.Count == 0, string.Join(Environment.NewLine, failures));
                var manual = FormulaTemplate(); manual.Mappings[0].Positions[1].Quantity!.Expression = "Length + Width";
                manual.Mappings[0].Positions[1].Quantity!.Unit = "St"; manual.LvCatalog.Entries[1] = manual.LvCatalog.Entries[1] with { Unit = "St" };
                TemplateEditor.EnsureValid(manual); // No expression dimensional analysis or implicit conversion.
            });
            Test("Checklist describes each position independently with group conditions and every formula input", () => {
                var template = FormulaTemplate(); var group = template.Mappings[0];
                group.Conditions.Add(new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "DN", Spec = "LENGTH", Scope = ParameterScope.Type }, Operand = OperandKind.Numeric, Number = 100, Unit = "mm" });
                var checks = ChecklistBuilder.Build(template);
                Check(checks.Count(c => c.Kind == "Bedingung") == 1, "group condition duplicated across sibling positions");
                foreach (var position in group.Positions) {
                    Check(checks.Any(c => c.Kind == "Zuordnung" && c.AppliesTo.Contains(position.Id.ToString())), "position context missing");
                    Check(checks.Count(c => c.Kind == "Menge" && c.AppliesTo.Contains(position.Id.ToString())) == 1, "position quantity missing or duplicated");
                }
                var formula = checks.Single(c => c.Kind == "Menge" && c.AppliesTo.Contains(group.Positions[1].Id.ToString()));
                Check(formula.Requirement.Contains("Length * Width") && formula.Requirement.Contains("m²"), "expression or result unit missing");
                foreach (var variable in group.Positions[1].Quantity!.Variables) {
                    Check(formula.Requirement.Contains(variable.Name) && formula.Requirement.Contains(variable.Source.ToString()) && formula.Requirement.Contains(variable.Unit), "formula input source/unit missing: " + variable.Name);
                }
                Check(formula.Requirement.Contains("nicht implementiert") && formula.Requirement.Contains("keine automatische"), "checklist suggests execution, conversion or dimensional inference");
                group.Positions[1].Quantity = null;
                Check(ChecklistBuilder.Build(template).Any(c => c.Kind == "Menge" && c.AppliesTo.Contains(group.Positions[1].Id.ToString()) && c.Requirement.Contains("nicht definiert")), "draft quantity omitted");
            });
            Test("Schema 3 explicit fields reject null missing unknown duplicate and contradictory quantities without changing import state", () => {
                var template = FormulaTemplate();
                var baseline = JsonNode.Parse(TemplateJson.Serialize(template))!;
                var editor = new TemplateEditor(); editor.ImportJson(baseline.ToJsonString());
                string directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "ava-groups-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
                try {
                    foreach (bool dirty in new[] { false, true }) {
                        editor.SaveAs(Path.Combine(directory, "active.json")); if (dirty) editor.Document.Name = "Unsaved";
                        var before = TemplateJson.Serialize(editor.Document); var document = editor.Document; var activePath = editor.FilePath;
                        foreach (var path in ObjectPaths(baseline)) {
                            var obj = At(baseline, path).AsObject();
                            foreach (var key in obj.Select(p => p.Key).ToArray()) {
                                var missing = baseline.DeepClone(); At(missing, path).AsObject().Remove(key); MustReject(missing.ToJsonString());
                                bool legalNull = (key == "Quantity" && path.Length == 4) || (key == "ConstantValue" && obj["Kind"]?.GetValue<string>() is "Parameter" or "Formula") || (key == "Source" && obj["Kind"]?.GetValue<string>() is "Constant" or "Formula");
                                if (!legalNull) { var nil = baseline.DeepClone(); At(nil, path)[key] = null; MustReject(nil.ToJsonString()); }
                            }
                            var unknown = baseline.DeepClone(); At(unknown, path)["Unknown"] = true; MustReject(unknown.ToJsonString());
                            var first = obj.First(); var fragment = obj.ToJsonString();
                            var duplicate = "{" + System.Text.Json.JsonSerializer.Serialize(first.Key) + ":" + (first.Value?.ToJsonString() ?? "null") + "," + fragment[1..];
                            MustReject(baseline.ToJsonString().Replace(fragment, duplicate));
                        }
                        foreach (var change in new Action<JsonNode>[] {
                            n => n["Mappings"]![0]!["Positions"]!.AsArray().Add((JsonNode?)null),
                            n => n["Mappings"]![0]!["Positions"]![1]!["Quantity"]!["Variables"]!.AsArray().Add((JsonNode?)null),
                            n => n["Mappings"]![0]!["Positions"]![0]!["Quantity"]!["Expression"] = "1",
                            n => n["Mappings"]![0]!["Positions"]![0]!["Quantity"]!["ConstantValue"] = 0,
                            n => n["Mappings"]![0]!["Positions"]![0]!["Quantity"]!["ConstantValue"] = -1,
                            n => n["Mappings"]![0]!["Positions"]![1]!["Quantity"]!["ConstantValue"] = 1,
                            n => n["Mappings"]![0]!["Positions"]![1]!["Quantity"]!["Expression"] = ""
                        }) { var candidate = baseline.DeepClone(); change(candidate); MustReject(candidate.ToJsonString()); }
                        void MustReject(string candidate) {
                            Reject(() => TemplateJson.Deserialize(candidate)); Reject(() => editor.ImportJson(candidate));
                            Check(ReferenceEquals(editor.Document, document) && TemplateJson.Serialize(editor.Document) == before && editor.FilePath == activePath && editor.IsDirty == dirty, "rejected import changed document/path/dirty state");
                        }
                    }
                } finally { Directory.Delete(directory, true); }
                foreach (int schema in new[] { 1, 2 }) {
                    var old = Legacy(schema); old["Mappings"]![0]!["Quantity"] = null;
                    var imported = TemplateJson.Deserialize(old.ToJsonString()); Check(imported.Mappings[0].Positions[0].Quantity == null, "nullable draft lost");
                }
                foreach (decimal value in new[] { 0m, -1m }) { var old = Legacy(2); old["Mappings"]![0]!["Quantity"]!["ConstantValue"] = value; Reject(() => TemplateJson.Deserialize(old.ToJsonString())); }
            });
            Test("Grouped edits validate the whole candidate and preserve saved and dirty state on semantic failure", () => {
                var editor = new TemplateEditor(); editor.ImportJson(TemplateJson.Serialize(FormulaTemplate()));
                string directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "ava-edit-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
                try {
                    foreach (bool dirty in new[] { false, true }) {
                        editor.SaveAs(Path.Combine(directory, "active.json")); if (dirty) editor.Document.Name = "Unsaved metadata";
                        var document = editor.Document; var before = TemplateJson.Serialize(document); var path = editor.FilePath;
                        var invalid = TemplateJson.Copy(document.Mappings[0]); invalid.Positions[1].Quantity!.Expression = "Missing + 1";
                        Reject(() => editor.UpsertMapping(invalid));
                        var invalidTemplate = TemplateJson.Copy(document); invalidTemplate.Mappings = [invalid];
                        Reject(() => editor.ImportJson(TemplateJson.Serialize(invalidTemplate)));
                        invalid = TemplateJson.Copy(document.Mappings[0]); invalid.Id = Guid.NewGuid(); invalid.Positions[0].Id = Guid.NewGuid(); invalid.Positions[1].Id = Guid.NewGuid(); invalid.Positions[1].LvItemId = "unknown";
                        Reject(() => editor.UpsertMapping(invalid));
                        Check(ReferenceEquals(editor.Document, document) && TemplateJson.Serialize(editor.Document) == before && editor.FilePath == path && editor.IsDirty == dirty, "semantic failure changed document/path/dirty state");
                    }
                    var updated = TemplateJson.Copy(editor.Document.Mappings[0]); updated.Positions[0].Quantity!.ConstantValue = 2;
                    editor.UpsertMapping(updated);
                    Check(editor.Document.Mappings.Count == 1 && editor.Document.Mappings[0].Positions.Count == 2 && editor.Document.Mappings[0].Positions[0].Quantity!.ConstantValue == 2, "group update duplicated group or lost sibling");
                    updated.Positions[0].Quantity!.ConstantValue = 9;
                    Check(editor.Document.Mappings[0].Positions[0].Quantity!.ConstantValue == 2, "caller retains mutable reference into saved group");
                    editor.SaveAs(Path.Combine(directory, "edited.json"));
                    var reopened = new TemplateEditor(); reopened.Open(editor.FilePath!);
                    Check(!reopened.IsDirty && TemplateJson.Serialize(reopened.Document) == TemplateJson.Serialize(editor.Document), "group save/open changed state");
                } finally { Directory.Delete(directory, true); }
            });
            Console.WriteLine($"PASS {passed} tests"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static AvaTemplate ValidTemplate()
    {
        var template = new AvaTemplate { ReviewScope = "Rohre einschließlich nicht markierter Elemente" };
        template.BgkCatalog.Entries.Add(new("09-25", "Rohr", "", [new("0100", "Typ")]));
        template.LvCatalog.Entries.Add(new("piece", "boq", ["01"], "01", "Stück", "", "St"));
        template.LvCatalog.Entries.Add(new("length", "boq", ["02"], "02", "Länge", "", "m"));
        template.Mappings.Add(new() { BgkKey = "09-25", TypeNumber = "0100", Positions = [
            new() { LvItemId = "piece", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" } },
            new() { LvItemId = "length", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Length", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" } }
        ] });
        return template;
    }
    static AvaTemplate FormulaTemplate()
    {
        var template = ValidTemplate();
        var guid = Guid.NewGuid(); template.ParameterCatalog.Entries.Add(new(guid, "ExplicitLength", "LENGTH", "group"));
        template.LvCatalog.Entries[1] = template.LvCatalog.Entries[1] with { Unit = "m²" };
        template.Mappings[0].Positions[1].Quantity = new() {
            Kind = QuantityKind.Formula, Source = null, Expression = "Length * Width", Unit = "m²", Variables = [
                new() { Name = "Length", Source = new() { SharedGuid = guid, Name = "ExplicitLength", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "cm" },
                new() { Name = "Width", Source = new() { Kind = ParameterKind.LocalFamily, Name = "ExplicitWidth", Spec = "LENGTH", Scope = ParameterScope.Type }, Unit = "m" }
            ]
        };
        return template;
    }
    static JsonNode Legacy(int schema)
    {
        var template = ValidTemplate();
        template.BgkCatalog = new(new("bg.xml", "raw BGK bytes", new string('a', 64)), template.BgkCatalog.Entries);
        template.LvCatalog = new(new("lv.x82", "raw LV bytes", new string('b', 64)), template.LvCatalog.Entries);
        template.ParameterCatalog = new(new("sp.txt", "raw parameter bytes", new string('c', 64)), []);
        var condition = new RuleCondition { Source = new() { Kind = ParameterKind.LocalFamily, Name = "DN", Spec = "LENGTH", Scope = ParameterScope.Type }, Operand = OperandKind.Numeric, Unit = "mm", Number = 100 };
        template.Mappings[0].Conditions.Add(condition);
        var second = TemplateJson.Copy(template.Mappings[0]); second.Id = Guid.NewGuid();
        template.Mappings[0].Positions = [template.Mappings[0].Positions[schema == 1 ? 1 : 0]];
        second.Positions = [second.Positions[1]]; template.Mappings.Add(second);
        var root = JsonNode.Parse(TemplateJson.Serialize(template))!; root["SchemaVersion"] = schema;
        foreach (var node in root["Mappings"]!.AsArray()) {
            var rule = node!.AsObject(); var position = rule["Positions"]![0]!;
            rule["LvItemId"] = position["LvItemId"]!.DeepClone(); rule["Quantity"] = position["Quantity"]!.DeepClone(); rule.Remove("Positions");
            var quantity = rule["Quantity"]!.AsObject(); quantity.Remove("Expression"); quantity.Remove("Variables");
            if (schema == 1) { quantity.Remove("Kind"); quantity.Remove("ConstantValue"); }
        }
        return root;
    }
    static IEnumerable<string[]> ObjectPaths(JsonNode? node, string[]? path = null)
    {
        path ??= [];
        if (node is JsonObject obj) {
            yield return path;
            foreach (var property in obj) foreach (var child in ObjectPaths(property.Value, [..path, property.Key])) yield return child;
        }
        if (node is JsonArray array) for (int i = 0; i < array.Count; i++) foreach (var child in ObjectPaths(array[i], [..path, i.ToString()])) yield return child;
    }
    static JsonNode At(JsonNode root, string[] path) { foreach (var key in path) root = root is JsonArray ? root[int.Parse(key)]! : root[key]!; return root; }
    static void Reject(Action action) { try { action(); } catch (System.IO.InvalidDataException) { return; } throw new Exception("Expected InvalidDataException"); }
    static void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
