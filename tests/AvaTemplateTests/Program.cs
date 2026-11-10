using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BIMassist.Ava.Core;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Test("Reader API exists", () => Check(typeof(Program).Assembly.GetType("BIMassist.Ava.Core.CatalogReaders") != null, "CatalogReaders missing"));
            Test("Portable template API exists", () => Check(typeof(Program).Assembly.GetType("BIMassist.Ava.Core.TemplateJson") != null, "TemplateJson missing"));
            Test("JSON roundtrip preserves identity sources and keys", () => {
                var p = new AvaTemplate { Name = "Prüfung", ReviewScope = "Alle Rohre, auch ohne BGK" };
                p.Mappings.Add(new MappingRule { BgkKey = "09-25", TypeNumber = "0100", Positions = [new() { LvItemId = "id", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "AVA_Anzahl", Spec = "NUMBER", Scope = ParameterScope.Instance }, Unit = "St" } }] });
                var q = TemplateJson.Deserialize(TemplateJson.Serialize(p));
                Check(q.ProfileId == p.ProfileId && q.Mappings[0].TypeNumber == "0100", "Identity changed");
                Check(q.Mappings[0].Positions[0].Quantity!.Source!.Kind == ParameterKind.LocalFamily && q.Mappings[0].Positions[0].Quantity!.Source!.Spec == "NUMBER", "local source lost");
                Check(q.BgkIdentification.BuiltInId == "ASSEMBLY_CODE" && q.TypeIdentification.BuiltInId == "ALL_MODEL_TYPE_COMMENTS", "transfer defaults wrong");
            });
            Test("JSON rejects null unsupported schema and unknown properties", () => {
                Reject(() => TemplateJson.Deserialize("null")); Reject(() => TemplateJson.Deserialize("{}"));
                var json = TemplateJson.Serialize(new AvaTemplate());
                Reject(() => TemplateJson.Deserialize(json.Replace("\"SchemaVersion\": 3", "\"SchemaVersion\": 4")));
                var node = JsonNode.Parse(json)!; node["Mappings"] = null; Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                node = JsonNode.Parse(json)!; node["Mystery"] = 1; Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                node = JsonNode.Parse(json)!; node["BgkIdentification"]!["Name"] = null; Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
            });
            Test("JSON never substitutes missing rule fields with implicit defaults", () => {
                var p = ValidTemplate();
                p.Mappings[0].Conditions.Add(new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Dicke", Spec = "LENGTH", Scope = ParameterScope.Instance }, Operand = OperandKind.Numeric, Number = 40, Unit = "cm" });
                foreach (string field in new[] { "Number", "Operator", "Source", "Operand", "Unit" }) {
                    var node = JsonNode.Parse(TemplateJson.Serialize(p))!;
                    node["Mappings"]![0]!["Conditions"]![0]!.AsObject().Remove(field);
                    Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                }
                foreach (string field in new[] { "Id", "Conditions", "Positions" }) {
                    var node = JsonNode.Parse(TemplateJson.Serialize(p))!;
                    node["Mappings"]![0]!.AsObject().Remove(field);
                    Reject(() => TemplateJson.Deserialize(node.ToJsonString()));
                }
                var sourceNode = JsonNode.Parse(TemplateJson.Serialize(p))!;
                sourceNode["Mappings"]![0]!["Conditions"]![0]!["Source"]!.AsObject().Remove("Scope");
                Reject(() => TemplateJson.Deserialize(sourceNode.ToJsonString()));
            });
            Test("JSON requires exact single tokens for all four enums and preserves rejected import state", () => {
                var p = ValidTemplate();
                p.Mappings[0].Conditions.Add(new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Dicke", Spec = "LENGTH", Scope = ParameterScope.Instance }, Operand = OperandKind.Numeric, Operator = ConditionOperator.Greater, Number = 40, Unit = "cm" });
                var json = TemplateJson.Serialize(p);
                var cases = new (string Field, string[] Names, string Compound, Func<JsonNode, JsonObject> Target)[] {
                    ("Kind", Enum.GetNames<ParameterKind>(), "SharedGuid, LocalFamily", n => n["Mappings"]![0]!["Positions"]![0]!["Quantity"]!["Source"]!.AsObject()),
                    ("Scope", Enum.GetNames<ParameterScope>(), "Instance, Type", n => n["Mappings"]![0]!["Positions"]![0]!["Quantity"]!["Source"]!.AsObject()),
                    ("Operator", Enum.GetNames<ConditionOperator>(), "Greater, NotEqual", n => n["Mappings"]![0]!["Conditions"]![0]!.AsObject()),
                    ("Operand", Enum.GetNames<OperandKind>(), "Numeric, Numeric", n => n["Mappings"]![0]!["Conditions"]![0]!.AsObject())
                };
                var editor = new TemplateEditor(); editor.ImportJson(json);
                string directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new Exception("TMPDIR is required"), "ava-enums-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                try {
                    editor.SaveAs(Path.Combine(directory, "active.json"));
                    editor.Document.Name = "Unsaved changes";
                    var document = editor.Document; var before = TemplateJson.Serialize(document);
                    var path = editor.FilePath; var dirty = editor.IsDirty;
                    var failures = new List<string>();
                    foreach (var item in cases) {
                        foreach (var name in item.Names) {
                            var node = JsonNode.Parse(json)!; item.Target(node)[item.Field] = name;
                            var roundtrip = JsonNode.Parse(TemplateJson.Serialize(TemplateJson.Deserialize(node.ToJsonString())))!;
                            Check(item.Target(roundtrip)[item.Field]!.GetValue<string>() == name, item.Field + " exact token changed: " + name);
                        }
                        foreach (var token in new[] { item.Compound, item.Names[0] + ", " + item.Names[0], " " + item.Names[0], item.Names[0] + " ", "\t" + item.Names[0], item.Names[0].ToLowerInvariant(), "0", "1", "", "Unknown" }) {
                            var node = JsonNode.Parse(json)!; item.Target(node)[item.Field] = token;
                            AssertRejected(node.ToJsonString(), item.Field + "=" + JsonSerializer.Serialize(token));
                        }
                        var numeric = JsonNode.Parse(json)!; item.Target(numeric)[item.Field] = 0;
                        AssertRejected(numeric.ToJsonString(), item.Field + "=numeric 0");
                    }
                    Check(failures.Count == 0, string.Join(Environment.NewLine, failures));
                    void AssertRejected(string candidate, string label) {
                        try { Reject(() => TemplateJson.Deserialize(candidate)); } catch (Exception ex) { failures.Add(label + ": " + ex.Message); }
                        try { Reject(() => editor.ImportJson(candidate)); } catch (Exception ex) { failures.Add(label + " import: " + ex.Message); }
                        if (!ReferenceEquals(editor.Document, document) || TemplateJson.Serialize(editor.Document) != before || editor.FilePath != path || editor.IsDirty != dirty)
                            failures.Add(label + ": failed import changed active document/path/dirty state");
                    }
                } finally { Directory.Delete(directory, true); }
            });
            Test("Validation rejects unknown BGK type LV and shared GUID", () => {
                var p = ValidTemplate(); Check(!TemplateValidator.Validate(p).Any(d => d.Severity == DiagnosticSeverity.Error), "valid template rejected");
                p.Mappings[0].BgkKey = "unknown"; p.Mappings[0].Positions[0].LvItemId = "unknown";
                p.Mappings[0].Positions[0].Quantity!.Source!.Kind = ParameterKind.SharedGuid;
                var codes = TemplateValidator.Validate(p).Select(d => d.Code).ToArray();
                Check(codes.Contains("BGK_REFERENCE") && codes.Contains("LV_REFERENCE") && codes.Contains("PARAMETER_REFERENCE"), "unknown references accepted");
                p = ValidTemplate(); p.Mappings[0].TypeNumber = "missing"; Check(TemplateValidator.Validate(p).Any(d => d.Code == "TYPE_REFERENCE"), "unknown type accepted");
            });
            Test("Local NUMBER is not silently replaced by shared INTEGER", () => {
                var p = ValidTemplate();
                p.ParameterCatalog.Entries.Add(new(Guid.NewGuid(), "AVA_Anzahl", "INTEGER", "1"));
                Check(!TemplateValidator.Validate(p).Any(d => d.Severity == DiagnosticSeverity.Error), "explicit local Number rejected");
                p.Mappings[0].Positions[0].Quantity!.Source!.Kind = ParameterKind.SharedGuid;
                p.Mappings[0].Positions[0].Quantity!.Source!.SharedGuid = p.ParameterCatalog.Entries[0].Guid;
                Check(TemplateValidator.Validate(p).Any(d => d.Code == "PARAMETER_SPEC"), "shared spec mismatch accepted");
            });
            Test("Conditions enforce typed operators and explicit units", () => {
                var p = ValidTemplate(); var c = new RuleCondition { Source = new() { Kind = ParameterKind.LocalFamily, Name = "D", Spec = "LENGTH" }, Operand = OperandKind.Numeric, Operator = ConditionOperator.Greater, Number = 5, Unit = "mm" };
                p.Mappings[0].Conditions.Add(c); Check(!TemplateValidator.Validate(p).Any(d => d.Severity == DiagnosticSeverity.Error), "valid numeric condition rejected");
                c.Unit = ""; Check(TemplateValidator.Validate(p).Any(d => d.Code == "CONDITION_UNIT"), "missing unit accepted");
                c.Source.Spec = "YESNO"; c.Operand = OperandKind.Boolean; Check(TemplateValidator.Validate(p).Any(d => d.Code == "CONDITION_OPERATOR"), "ordered Boolean accepted");
                c.Operator = ConditionOperator.Equal; Check(!TemplateValidator.Validate(p).Any(d => d.Severity == DiagnosticSeverity.Error), "valid Boolean rejected");
                c.Source.Spec = "TEXT"; c.Operand = OperandKind.Text; c.Text = "001"; Check(!TemplateValidator.Validate(p).Any(d => d.Severity == DiagnosticSeverity.Error), "valid text rejected");
                c.Source.Spec = "MATERIAL"; Check(TemplateValidator.Validate(p).Any(d => d.Code == "CONDITION_TYPE"), "unsupported material accepted");
            });
            Test("Quantity validates source read units independently of LV metadata", () => {
                var p = ValidTemplate(); p.Mappings[0].Positions[0].Quantity!.Unit = "m";
                Check(TemplateValidator.Validate(p).Any(d => d.Code == "QUANTITY_UNIT" && d.Severity == DiagnosticSeverity.Error), "Number in metres accepted");
                p.Mappings[0].Positions[0].Quantity!.Source!.Spec = "LENGTH";
                var diagnostics = TemplateValidator.Validate(p);
                Check(diagnostics.Any(d => d.Code == "LV_UNIT" && d.Severity == DiagnosticSeverity.Warning), "independent length/piece unit hint missing");
                Check(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "LV metadata blocked a valid typed read");
                TemplateEditor.EnsureValid(p);
            });
            Test("Missing quantity warning identifies the exact LV position", () => {
                var p = ValidTemplate(); p.Mappings[0].Positions[0].Quantity = null;
                p.LvCatalog.Entries.Add(new("id2", "b", ["09", "25", "0110"], "09.25.0110", "Rohrbogen", "", "St"));
                p.Mappings[0].Positions.Add(new() { LvItemId = "id2", Quantity = null });
                var warnings = TemplateValidator.Validate(p).Where(d => d.Code == "QUANTITY_MISSING").ToArray();
                Check(warnings.Length == 2
                    && warnings.Any(d => d.Path.Contains("09.25.0100 — Rohr", StringComparison.Ordinal))
                    && warnings.Any(d => d.Path.Contains("09.25.0110 — Rohrbogen", StringComparison.Ordinal))
                    && warnings.All(d => d.Message.Contains("diese LV-Position", StringComparison.Ordinal)),
                    "Missing quantity warnings do not identify each position independently");
            });
            Test("Checklist uses independent scope and branch-specific reading requirements", () => {
                var p = ValidTemplate(); p.Mappings[0].Conditions.Add(new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Flag", Spec = "YESNO" }, Operand = OperandKind.Boolean, Boolean = true });
                var checks = ChecklistBuilder.Build(p);
                Check(checks.Any(c => c.Kind == "Prüfumfang" && c.Requirement.Contains(p.ReviewScope)), "independent scope missing");
                Check(checks.Count(c => c.Kind == "Identifikation") == 2, "identification bindings missing");
                Check(checks.Any(c => c.Kind == "Bedingung" && c.Requirement.Contains("nicht auswertbar")), "missing condition interpreted as false");
                Check(checks.Any(c => c.Kind == "Menge" && c.AppliesTo.Contains("nur bei erfüllten")), "quantity not branch specific");
                Check(checks.Any(c => c.Requirement.Contains("schreibgeschützt")), "read-only/formula semantics missing");
                p.Mappings.Clear(); Check(ChecklistBuilder.Build(p).Count(c => c.Kind == "Menge") == 0, "stale checklist");
            });
            Test("Editor safely imports validates and tracks saved state", () => {
                var editor = new TemplateEditor(); var p = ValidTemplate();
                editor.ImportJson(TemplateJson.Serialize(p)); Check(editor.IsDirty && editor.Document.ProfileId == p.ProfileId, "import identity/dirty");
                var before = TemplateJson.Serialize(editor.Document);
                Reject(() => editor.ImportJson("null")); Check(TemplateJson.Serialize(editor.Document) == before, "failed import replaced state");
                Reject(() => editor.ReplaceLv(new(new("", "", ""), []))); Check(TemplateJson.Serialize(editor.Document) == before, "catalog replacement lost references");
                p.Mappings[0].Positions[0].LvItemId = "invalid"; Reject(() => editor.ImportJson(TemplateJson.Serialize(p)));
                Check(TemplateJson.Serialize(editor.Document) == before, "semantic failure replaced state");
                string path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "ava-test-" + Guid.NewGuid() + ".json");
                try {
                    editor.SaveAs(path); Check(!editor.IsDirty && File.Exists(path), "save state");
                    editor.Document.Name = "Changed"; Check(editor.IsDirty, "metadata not tracked");
                    editor.Open(path); Check(!editor.IsDirty && editor.Document.Name == "Neue AVA-Vorlage", "open state");
                    var rule = TemplateJson.Copy(editor.Document.Mappings[0]); rule.Conditions.Add(new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Flag", Spec = "YESNO" }, Operand = OperandKind.Boolean });
                    editor.UpsertMapping(rule); Check(editor.Document.Mappings.Count == 1 && editor.Document.Mappings[0].Conditions.Count == 1, "update duplicated rule");
                    editor.RemoveMapping(rule.Id); Check(editor.Document.Mappings.Count == 0 && editor.IsDirty, "remove failed");
                } finally { if (File.Exists(path)) File.Delete(path); }
                var id = editor.Document.ProfileId; editor.New(); Check(editor.Document.ProfileId != id && !editor.IsDirty, "new identity");
            });
            Test("Malformed UTF8 is rejected and failed Open preserves document path and dirty state", () => {
                string directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new Exception("TMPDIR is required"), "ava-utf8-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                try {
                    var editor = new TemplateEditor(); editor.ImportJson(TemplateJson.Serialize(ValidTemplate()));
                    string active = Path.Combine(directory, "active.json"), malformed = Path.Combine(directory, "malformed.json");
                    var json = TemplateJson.Serialize(ValidTemplate());
                    var bytes = Encoding.UTF8.GetBytes(json);
                    int nameStart = json.IndexOf("AVA_Anzahl", StringComparison.Ordinal);
                    Check(nameStart >= 0, "local source name missing from test fixture");
                    bytes[Encoding.UTF8.GetByteCount(json.AsSpan(0, nameStart))] = 0xFF;
                    File.WriteAllBytes(malformed, bytes);
                    foreach (bool dirty in new[] { false, true }) {
                        editor.SaveAs(active);
                        if (dirty) editor.Document.Name = "Unsaved metadata";
                        var document = editor.Document; var before = TemplateJson.Serialize(document);
                        var path = editor.FilePath;
                        Check(editor.IsDirty == dirty, "incorrect initial dirty state");
                        Reject(() => editor.Open(malformed));
                        Check(ReferenceEquals(editor.Document, document) && TemplateJson.Serialize(editor.Document) == before, "failed Open replaced or changed active document");
                        Check(editor.FilePath == path && editor.IsDirty == dirty, "failed Open changed path or dirty state");
                    }
                    try { TemplateJson.Load(malformed); throw new Exception("malformed UTF8 accepted"); }
                    catch (InvalidDataException ex) { Check(ex.InnerException is DecoderFallbackException, "UTF8 decoding failure was not wrapped as InvalidDataException"); }
                    File.WriteAllBytes(malformed, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray());
                    Check(TemplateJson.Serialize(TemplateJson.Load(malformed)) == json, "valid UTF8 BOM no longer loads unchanged");
                } finally { Directory.Delete(directory, true); }
            });
            Test("UI handles invalid input as recoverable rather than an unhandled dispatcher error", () => {
                var classifier = typeof(BIMassist.Ava.Views.EditorUi).GetMethod("IsInputError", BindingFlags.Static | BindingFlags.NonPublic);
                Check(classifier != null, "shared UI input-error classifier missing");
                bool Handles(Exception error) => (bool)classifier!.Invoke(null, [error])!;
                Check(Handles(new InvalidDataException("invalid JSON or mapping")), "InvalidDataException is not IOException and must be handled explicitly");
                Check(Handles(new IOException()) && Handles(new UnauthorizedAccessException()), "file errors not handled");
                Check(!Handles(new NullReferenceException()), "programming errors must not be disguised as invalid input");
            });
            Test("WPF editor is public parameterless resizable and has all work areas", () => {
                var w = new BIMassist.Ava.Views.AvaTemplateWindow();
                Check(w.ResizeMode == System.Windows.ResizeMode.CanResize && w.MinWidth >= 800, "window layout");
                w.Measure(new System.Windows.Size(1180, 800)); w.Arrange(new System.Windows.Rect(0, 0, 1180, 800)); w.UpdateLayout();
                var tabs = (System.Windows.Controls.TabControl)w.FindName("EditorTabs");
                Check(tabs.Items.Count == 5, "profile/catalog/mapping/checklist/diagnostic tabs");
                Check(w.FindName("ProfileName") is System.Windows.Controls.TextBox, "name editor missing");
                w.Close();
            });
            Test("Entering local parameter name last refreshes available units", () => {
                var source = new BIMassist.Ava.Views.ParameterSourceEditor([], new() { Kind = ParameterKind.LocalFamily, Spec = "LENGTH" });
                bool validSourceNotified = false;
                source.SourceChanged += () => validSourceNotified = source.ReadSource().Spec == "LENGTH";
                var name = (System.Windows.Controls.TextBox)typeof(BIMassist.Ava.Views.ParameterSourceEditor).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
                name.Text = "Wanddicke";
                Check(validSourceNotified, "typing the final required name leaves unit choices empty");
            });
            Test("Parameter and mapping editors preserve explicit local source", () => {
                var p = ValidTemplate();
                var source = new BIMassist.Ava.Views.ParameterSourceEditor(p.ParameterCatalog.Entries, p.Mappings[0].Positions[0].Quantity!.Source!);
                var value = source.ReadSource(); Check(value.Kind == ParameterKind.LocalFamily && value.Spec == "NUMBER" && value.Scope == ParameterScope.Instance, "local source lost in UI");
                var mapping = new BIMassist.Ava.Views.MappingEditorWindow(p, p.Mappings[0]);
                var rule = mapping.BuildRule(); Check(rule.Id == p.Mappings[0].Id && rule.BgkKey == "09-25" && rule.TypeNumber == "100" && rule.Positions[0].LvItemId == "id", "mapping edits lose identity");
                Check(rule.Positions[0].Quantity!.Source!.Kind == ParameterKind.LocalFamily, "UI converted local parameter"); mapping.Close();
                var position = new BIMassist.Ava.Views.PositionEditorWindow(p, p.Mappings[0].Positions[0]);
                var edited = position.BuildPosition();
                Check(edited.Id == p.Mappings[0].Positions[0].Id && edited.LvItemId == "id", "position edits lose identity");
                Check(edited.Quantity!.Source!.Kind == ParameterKind.LocalFamily && edited.Quantity.Source.Spec == "NUMBER" && edited.Quantity.Source.Scope == ParameterScope.Instance && edited.Quantity.Unit == "St", "position editor changed explicit local source/unit");
                position.Close();
            });
            Test("X82 preserves hierarchical text keys", () => {
                var c = Read("ReadX82", Encoding.UTF8.GetBytes("<GAEB xmlns='http://www.gaeb.de/GAEB_DA_XML/DA82/3.3'><Award><BoQ ID='b'><BoQBody><BoQCtgy RNoPart='01'><BoQBody><BoQCtgy RNoPart='09'><BoQBody><Itemlist><Item ID='i' RNoPart='0010'><QU>m</QU><Description><CompleteText><OutlineText><p>A <span>B</span></p></OutlineText></CompleteText></Description></Item></Itemlist></BoQBody></BoQCtgy></BoQBody></BoQCtgy></BoQBody></BoQ></Award></GAEB>"));
                Check(c["Entries"]![0]!["Ordinal"]!.GetValue<string>() == "01.09.0010", "OZ hierarchy lost");
                Check(c["Entries"]![0]!["Text"]!.GetValue<string>() == "A B", "nested text lost");
                Check(c["Source"]!["Sha256"]!.GetValue<string>().Length == 64, "source hash missing");
            });
            Test("BGK preserves leading zero and type text", () => {
                var c = Read("ReadBgk", Encoding.UTF8.GetBytes("<Baugruppe><Key>01-02</Key><Description>Root</Description><Typmarkierungen><Typmarkierung><Typnummer>001</Typnummer><Typbezeichnung>Type</Typbezeichnung></Typmarkierung></Typmarkierungen><Baugruppen><Baugruppe><Key>01-02-03</Key><Typmarkierungen/></Baugruppe></Baugruppen></Baugruppe>"));
                Check(c["Entries"]!.AsArray().Count == 2 && c["Entries"]![0]!["Types"]![0]!["Number"]!.GetValue<string>() == "001", "BGK/type keys changed");
            });
            Test("UTF16 parameters keep GUID and spec", () => {
                var text = "*PARAM\tGUID\tNAME\tDATATYPE\tGROUP\nPARAM\t9af4d36c-6267-47f9-af4b-eb8ac2a0fc49\tAVA_Anzahl\tINTEGER\t1\n";
                var c = Read("ReadSharedParameters", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray());
                Check(c["Entries"]![0]!["Spec"]!.GetValue<string>() == "INTEGER", "spec changed");
                Check(c["Entries"]![0]!["Guid"]!.GetValue<string>() == "9af4d36c-6267-47f9-af4b-eb8ac2a0fc49", "GUID changed");
            });
            Test("Readers reject wrong XML and DTD", () => {
                Reject(() => Read("ReadX82", Encoding.UTF8.GetBytes("<GAEB/>")));
                Reject(() => Read("ReadBgk", Encoding.UTF8.GetBytes("<!DOCTYPE Baugruppe [<!ENTITY x 'bad'>]><Baugruppe><Key>&x;</Key></Baugruppe>")));
            });
            if (args.Length > 0) Test("Actual read-only reference catalogs", () => {
                var lv = Read("ReadX82", File.ReadAllBytes(Path.Combine(args[0], "StammLV Stand 09.07.2026.x82")));
                var bg = Read("ReadBgk", File.ReadAllBytes(Path.Combine(args[0], "SEBD_67_203_BGK_StammLV_30.10.2026.xml")));
                var sp = Read("ReadSharedParameters", File.ReadAllBytes(Path.Combine(args[0], "SEBD-Vorlage Gemeinsamgenutze Parameter.txt")));
                Check(lv["Entries"]!.AsArray().Count == 2272, "LV count");
                Check(bg["Entries"]!.AsArray().Count == 254 && bg["Entries"]!.AsArray().Sum(x => x!["Types"]!.AsArray().Count) == 776, "BGK count");
                Check(sp["Entries"]!.AsArray().Count == 95, "parameter count");
                var editor = new TemplateEditor();
                using var lvStream = File.OpenRead(Path.Combine(args[0], "StammLV Stand 09.07.2026.x82"));
                using var bgkStream = File.OpenRead(Path.Combine(args[0], "SEBD_67_203_BGK_StammLV_30.10.2026.xml"));
                using var parameterStream = File.OpenRead(Path.Combine(args[0], "SEBD-Vorlage Gemeinsamgenutze Parameter.txt"));
                editor.ReplaceLv(CatalogReaders.ReadX82(lvStream, "StammLV.x82"));
                editor.ReplaceBgk(CatalogReaders.ReadBgk(bgkStream, "BGK.xml"));
                editor.ReplaceParameters(CatalogReaders.ReadSharedParameters(parameterStream, "Parameter.txt"));
                editor.Document.Name = "TECHNISCHER TEST – keine fachlich freigegebene Zuordnung";
                editor.Document.ReviewScope = "Testbereich unabhängig von BGK; kein ausführbarer Modellfilter";
                var b = editor.Document.BgkCatalog.Entries.First(b => b.Types.Count > 0);
                var l = editor.Document.LvCatalog.Entries.First(l => l.Unit == "St");
                var p = editor.Document.ParameterCatalog.Entries.Single(p => p.Name == "AVA_Anzahl");
                editor.UpsertMapping(new() { BgkKey = b.Key, TypeNumber = b.Types[0].Number, Positions = [new() { LvItemId = l.Id,
                        Quantity = new() { Source = new() { Kind = ParameterKind.SharedGuid, SharedGuid = p.Guid, Name = p.Name, Spec = p.Spec, Scope = ParameterScope.Instance }, Unit = "St" } }] });
                string directory = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "ava-roundtrip-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                try {
                    string saved = Path.Combine(directory, "saved.json"), exported = Path.Combine(directory, "export.json");
                    editor.SaveAs(saved); editor.Export(exported);
                    Check(!editor.IsDirty && editor.FilePath == saved, "export changed save state");
                    var imported = new TemplateEditor(); imported.ImportJson(File.ReadAllText(exported));
                    Check(imported.IsDirty && imported.FilePath == null, "import must be an unsaved portable copy");
                    Check(TemplateJson.Serialize(imported.Document) == TemplateJson.Serialize(editor.Document), "real catalogs or rule changed in roundtrip");
                    Check(ChecklistBuilder.Build(imported.Document).SequenceEqual(ChecklistBuilder.Build(editor.Document)), "derived checklist changed in roundtrip");
                    var dialog = new BIMassist.Ava.Views.MappingEditorWindow(imported.Document, imported.Document.Mappings[0]);
                    Check(TemplateJson.Serialize(new AvaTemplate { Mappings = [dialog.BuildRule()] }).Contains(p.Guid.ToString()), "shared quantity GUID lost by real-catalog dialog");
                    dialog.Close();
                } finally { Directory.Delete(directory, true); }
            });
            Console.WriteLine($"PASS {passed} tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static AvaTemplate ValidTemplate()
    {
        var p = new AvaTemplate { ReviewScope = "Kategorie Rohre, unabhängig von BGK" };
        p.BgkCatalog.Entries.Add(new("09-25", "Rohr", "", [new("100", "Typ")]));
        p.LvCatalog.Entries.Add(new("id", "b", ["09", "25", "0100"], "09.25.0100", "Rohr", "", "St"));
        p.Mappings.Add(new() { BgkKey = "09-25", TypeNumber = "100", Positions = [new() { LvItemId = "id", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "AVA_Anzahl", Spec = "NUMBER", Scope = ParameterScope.Instance }, Unit = "St" } }] });
        return p;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected InvalidDataException"); }
    private static JsonNode Read(string method, byte[] data)
    {
        var type = typeof(Program).Assembly.GetType("BIMassist.Ava.Core.CatalogReaders") ?? throw new Exception("CatalogReaders missing");
        try { return JsonSerializer.SerializeToNode(type.GetMethod(method)!.Invoke(null, new object[] { new MemoryStream(data), "source" }))!; }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { throw (InvalidDataException)e.InnerException; }
    }
}
