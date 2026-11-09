using System.Text.Json;
using BIMassist.Ava.Core;

internal static class Program
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Test("Mixed LV sibling units attach, save, load, export and edit without changing independent quantities", MixedSiblingRoundtrip);
        Test("Every different LV label is only a warning, including known scale differences", DifferentLabelsWarn);
        Test("Constants freely declare arbitrary nonempty result labels", () => ArbitraryResultLabels("constant"));
        Test("Numeric formulas freely declare arbitrary nonempty result labels", () => ArbitraryResultLabels("numeric formula"));
        Test("Bound formulas freely declare arbitrary nonempty result labels", () => ArbitraryResultLabels("bound formula"));
        Test("Blank result labels and invalid typed parameter or formula inputs still reject transactionally", InvalidDeclarationsReject);
        Test("Checklist explicitly separates each quantity declaration from LV metadata without inferred conversion", IndependentChecklist);
        Console.WriteLine($"{passed} passed; {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void MixedSiblingRoundtrip()
    {
        var template = CatalogTemplate();
        var editor = new TemplateEditor();
        editor.ImportJson(TemplateJson.Serialize(template));
        var group = new MappingRule { BgkKey = "09-25", TypeNumber = "0100", Positions = [
            new() { LvItemId = "piece", Quantity = Constant(1, "St") },
            new() { LvItemId = "lump", Quantity = Constant(2, "St") },
            new() { LvItemId = "month", Quantity = Constant(3.25m, "m") },
            new() { LvItemId = "business", Quantity = Constant(4.5m, "psch") }
        ] };
        editor.UpsertMapping(group);
        Check(editor.Document.Mappings.Single().Positions.Count == 4, "not all LV positions attached to the same BGK/type");
        NoErrors(editor.Document);
        var snapshots = JsonSerializer.Serialize(editor.Document.LvCatalog);
        var directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new Exception("TMPDIR required"), "ava-independent-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var saved = Path.Combine(directory, "saved.json");
            var exported = Path.Combine(directory, "exported.json");
            var expected = TemplateJson.Serialize(editor.Document);
            editor.SaveAs(saved);
            Check(!editor.IsDirty && editor.FilePath == saved, "save state incorrect");
            Check(TemplateJson.Serialize(TemplateJson.Load(saved)) == expected, "Save/Load changed units, amounts or identities");
            editor.Export(exported);
            Check(!editor.IsDirty && editor.FilePath == saved, "export changed active state");
            Check(TemplateJson.Serialize(TemplateJson.Load(exported)) == expected, "export changed prescriptions");
            var reopened = new TemplateEditor();
            reopened.Open(saved);
            var edit = TemplateJson.Copy(reopened.Document.Mappings.Single());
            var siblingBefore = edit.Positions.Skip(1).Select(p => JsonSerializer.Serialize(p)).ToArray();
            var firstId = edit.Positions[0].Id;
            // Reproduce editing an existing St quantity while selecting an LV whose metadata says psch.
            edit.Positions[0].LvItemId = "lump";
            reopened.UpsertMapping(edit);
            reopened.SaveAs(saved);
            reopened.Open(saved);
            var positions = reopened.Document.Mappings.Single().Positions;
            Check(positions[0].Id == firstId && positions[0].LvItemId == "lump" && positions[0].Quantity is { ConstantValue: 1, Unit: "St" }, "existing St-to-psch edit could not save unchanged");
            Check(positions.Skip(1).Select(p => JsonSerializer.Serialize(p)).SequenceEqual(siblingBefore), "editing one child changed a sibling");
            edit = TemplateJson.Copy(reopened.Document.Mappings.Single());
            var untouched = edit.Positions.Where((_, i) => i != 2).Select(p => JsonSerializer.Serialize(p)).ToArray();
            edit.Positions[2].Quantity!.ConstantValue = 9.75m;
            reopened.UpsertMapping(edit);
            reopened.SaveAs(saved);
            reopened.Export(exported);
            var final = TemplateJson.Load(exported);
            NoErrors(final);
            Check(final.Mappings[0].Positions[2].Quantity is { ConstantValue: 9.75m, Unit: "m" }, "sibling quantity edit was lost or converted");
            Check(final.Mappings[0].Positions.Where((_, i) => i != 2).Select(p => JsonSerializer.Serialize(p)).SequenceEqual(untouched), "sibling edit changed other children");
            Check(JsonSerializer.Serialize(final.LvCatalog) == snapshots && final.SchemaVersion == 3 && final.ProfileId == template.ProfileId, "metadata/schema/profile changed");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void DifferentLabelsWarn()
    {
        foreach (var (quantityUnit, lvUnit) in new[] { ("St", "psch"), ("m", "St"), ("cm", "m"), ("m²", "m2"), ("St", "Mt"), ("psch", "Servicepaket / Quartal") })
        {
            var template = CatalogTemplate();
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = lvUnit };
            template.Mappings.Add(new() { BgkKey = "09-25", TypeNumber = "0100", Positions = [new() { LvItemId = "piece", Quantity = Constant(7, quantityUnit) }] });
            var before = TemplateJson.Serialize(template);
            var diagnostics = TemplateValidator.Validate(template);
            NoErrors(template);
            var warning = diagnostics.Single(d => d.Code == "LV_UNIT");
            Check(warning.Severity == DiagnosticSeverity.Warning && warning.Path.Contains(template.Mappings[0].Positions[0].Id.ToString()), "LV difference must be a position-scoped warning");
            Check(warning.Message.Contains(quantityUnit) && warning.Message.Contains(lvUnit) && warning.Message.Contains("unabhängig") && warning.Message.Contains("keine automatische Umrechnung", StringComparison.OrdinalIgnoreCase), "warning must explain independent declarations and no automatic conversion");
            TemplateEditor.EnsureValid(template);
            Check(TemplateJson.Serialize(template) == before, "validation converted, relabeled or mutated data");
        }
    }

    private static void ArbitraryResultLabels(string mode)
    {
        foreach (var unit in new[] { "Mt", "Servicepaket / Quartal", "Arbeits-Tag", "€ je Einsatz", "mT", " Mt " })
        foreach (bool equal in new[] { true, false })
        {
            var template = CatalogTemplate();
            template.LvCatalog.Entries[0] = template.LvCatalog.Entries[0] with { Unit = equal ? unit : "psch" };
            var guid = Guid.NewGuid();
            template.ParameterCatalog.Entries.Add(new(guid, "ExplicitLength", "LENGTH", "group"));
            var quantity = mode == "constant" ? Constant(1.25m, unit) : new QuantitySource {
                Kind = QuantityKind.Formula, Source = null, Unit = unit,
                Expression = mode == "numeric formula" ? "1.25 + 2,5" : "Length / 100", Variables = mode == "numeric formula" ? [] : [
                    new() { Name = "Length", Source = new() { SharedGuid = guid, Name = "ExplicitLength", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "cm" }
                ]
            };
            var group = new MappingRule { BgkKey = "09-25", TypeNumber = "0100", Positions = [new() { LvItemId = "piece", Quantity = quantity }] };
            var editor = new TemplateEditor();
            editor.ImportJson(TemplateJson.Serialize(template));
            editor.UpsertMapping(group);
            NoErrors(editor.Document);
            Check(TemplateValidator.Validate(editor.Document).Count(d => d.Code == "LV_UNIT") == (equal ? 0 : 1), "equal arbitrary labels must not warn; different labels may only warn");
            var expected = TemplateJson.Serialize(editor.Document);
            var copy = TemplateJson.Deserialize(expected);
            TemplateEditor.EnsureValid(copy);
            Check(TemplateJson.Serialize(copy) == expected, "result label, source/input unit, or amount changed");
            var q = copy.Mappings[0].Positions[0].Quantity!;
            Check(q.Unit == unit && (q.Kind != QuantityKind.Constant || q.ConstantValue == 1.25m), "declared result was converted or normalized");
            if (mode == "bound formula")
                Check(q.Variables.Single().Source.SharedGuid == guid && q.Variables[0].Unit == "cm", "independent result unit changed typed input");
            if (mode == "numeric formula")
                Check(QuantityFormula.Evaluate(q.Expression, new Dictionary<string, decimal>()) == 3.75m, "numeric formula output was affected by label");
        }
    }

    private static void IndependentChecklist()
    {
        var template = CatalogTemplate();
        var group = new MappingRule { BgkKey = "09-25", TypeNumber = "0100", Positions = [
            new() { LvItemId = "lump", Quantity = Constant(2, "St") },
            new() { LvItemId = "month", Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                Unit = "Leistungspaket", Expression = "Length * 2", Variables = [new() { Name = "Length",
                    Source = new() { Kind = ParameterKind.LocalFamily, Name = "Length", Spec = "LENGTH" }, Unit = "m" }] } },
            new() { LvItemId = "business", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily,
                Name = "Count", Spec = "INTEGER" }, Unit = "St" } }
        ] };
        template.Mappings.Add(group); TemplateEditor.EnsureValid(template);
        var checks = ChecklistBuilder.Build(template).Where(c => c.Kind == "Menge").ToArray();
        Check(checks.Length == 3, "Missing independent quantity checklist entries");
        foreach (var (position, index) in group.Positions.Select((p, i) => (p, i)))
        {
            var check = checks[index];
            var lv = template.LvCatalog.Entries.Single(l => l.Id == position.LvItemId);
            Check(check.AppliesTo.Contains(position.Id.ToString()), "Checklist quantities share a group-only context");
            Check(check.Requirement.Contains(position.Quantity!.Unit) && check.Requirement.Contains(lv.Unit), "Checklist drops quantity or catalog declaration");
            Check(check.Requirement.Contains("unabhängig") && check.Requirement.Contains("LV-Katalogeinheit") &&
                check.Requirement.Contains("keine automatische Umrechnung", StringComparison.OrdinalIgnoreCase), "Checklist implies LV conversion rather than independent declarations");
        }
        Check(checks[1].Requirement.Contains("Length") && checks[1].Requirement.Contains("Eingabeeinheit m"), "Formula input identity or read unit lost");
    }

    private static void InvalidDeclarationsReject()
    {
        var template = CatalogTemplate();
        var editor = new TemplateEditor();
        editor.ImportJson(TemplateJson.Serialize(template));
        var before = TemplateJson.Serialize(editor.Document);
        var document = editor.Document;
        foreach (var kind in new[] { QuantityKind.Constant, QuantityKind.Formula })
        foreach (var unit in new[] { "", " ", "\t\r\n" })
        {
            var quantity = kind == QuantityKind.Constant ? Constant(1, unit) : new QuantitySource { Kind = kind, Source = null, Expression = "1.5", Unit = unit };
            RejectQuantity(quantity, "QUANTITY_UNIT");
        }
        foreach (var unit in new[] { "St", "psch", "Mt", "Servicepaket / Quartal" })
        {
            RejectQuantity(Constant(0, unit), "QUANTITY_VALUE");
            RejectQuantity(Constant(-1, unit), "QUANTITY_VALUE");
        }
        RejectQuantity(Constant(1.5m, "St"), "QUANTITY_VALUE");
        RejectQuantity(new() { Kind = (QuantityKind)99, Source = null, Unit = "St" }, "QUANTITY_MODE", upsert: false);
        RejectQuantity(new() { Source = null, Unit = "m" }, "QUANTITY_MODE");
        foreach (var unit in new[] { "", "St", "Mt", "Business" })
            RejectQuantity(new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Length", Spec = "LENGTH" }, Unit = unit }, "QUANTITY_UNIT");
        foreach (var input in new[] { "", "St", "Mt", "Business" })
            RejectQuantity(new() { Kind = QuantityKind.Formula, Source = null, Unit = "m", Expression = "Length", Variables = [new() {
                Name = "Length", Source = new() { Kind = ParameterKind.LocalFamily, Name = "Length", Spec = "LENGTH" }, Unit = input
            }] }, "FORMULA");
        foreach (var expression in new[] { "Length", "1 ** 2", "Math.Abs(-1)", "", " " })
            RejectQuantity(new() { Kind = QuantityKind.Formula, Source = null, Unit = "m", Expression = expression }, "FORMULA");
        RejectQuantity(new() { Source = new() { SharedGuid = Guid.NewGuid(), Name = "Length", Spec = "LENGTH" }, Unit = "m" }, "PARAMETER_REFERENCE");
        RejectQuantity(new() { Source = new() { Kind = ParameterKind.LocalFamily, SharedGuid = Guid.NewGuid(), Name = "Length", Spec = "LENGTH" }, Unit = "m" }, "PARAMETER_SOURCE");
        Check(ReferenceEquals(document, editor.Document) && TemplateJson.Serialize(editor.Document) == before && editor.IsDirty && editor.FilePath == null, "rejected declarations mutated editor state");

        void RejectQuantity(QuantitySource quantity, string code, bool upsert = true)
        {
            var group = new MappingRule { BgkKey = "09-25", TypeNumber = "0100", Positions = [new() { LvItemId = "lump", Quantity = quantity }] };
            var candidate = TemplateJson.Copy(template);
            candidate.Mappings.Add(group);
            Check(TemplateValidator.Validate(candidate).Any(d => d.Code == code && d.Severity == DiagnosticSeverity.Error), "missing blocking " + code);
            Reject(() => TemplateEditor.EnsureValid(candidate));
            if (upsert) Reject(() => editor.UpsertMapping(group));
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (System.IO.InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException");
    }

    private static AvaTemplate CatalogTemplate()
    {
        var template = new AvaTemplate { ReviewScope = "Alle Rohre, unabhängig von Kennzeichnungen" };
        template.BgkCatalog.Entries.Add(new("09-25", "Rohr", "", [new("0100", "Typ")]));
        template.LvCatalog.Entries.AddRange([
            new("piece", "boq", ["01"], "01", "Stück", "", "St"),
            new("lump", "boq", ["02"], "02", "Pauschale", "", "psch"),
            new("month", "boq", ["03"], "03", "Monat", "", "Mt"),
            new("business", "boq", ["04"], "04", "Geschäftseinheit", "", "Servicepaket / Quartal")
        ]);
        return template;
    }
    private static QuantitySource Constant(decimal value, string unit) => new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = value, Unit = unit };
    private static void NoErrors(AvaTemplate template)
    {
        var errors = TemplateValidator.Validate(template).Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Check(errors.Length == 0, string.Join("; ", errors.Select(d => d.ToString())));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
    }
}
