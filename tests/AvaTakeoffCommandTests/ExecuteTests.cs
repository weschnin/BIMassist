using System.Text;
using System.Xml.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;
using BIMassist.Commands;
using Microsoft.Win32;

internal static class ExecuteTests
{
    private static int passed, failed;
    private static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";
    private const string X82 = """
        <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA82/3.3"><Award><DP>82</DP><BoQ ID="OriginalBoQ">
        <BoQInfo><Name>Command test</Name><Date>2026-01-01</Date>
        <BoQBkdn><Type>Item</Type><Length>4</Length><Num>Yes</Num></BoQBkdn></BoQInfo>
        <BoQBody><Itemlist><Item ID="FirstItem" RNoPart="0010"><Qty>999</Qty><QU>m</QU></Item></Itemlist></BoQBody>
        </BoQ></Award></GAEB>
        """;

    public static void Run()
    {
        Test("recalculation diagnoses exact identification source and missing versus empty without guessing values", f =>
        {
            var template = f.Template();
            f.Type.BuiltIns.Remove(BuiltInParameter.ASSEMBLY_CODE);
            f.Type.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, " ", StorageType.String);
            // A populated opposite-scope value must remain diagnostic only, never become a replacement.
            f.Instance.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, "001", StorageType.String);
            f.Instance.Category!.Name = "Skelettbau";
            f.Remember(template);
            Equal(Result.Cancelled, f.Recalculate(false, _ => throw new Exception("Unmarked element was measured")));
            var notice = TaskDialog.Notices.Single();
            Contains("Klasse: FamilyInstance", notice);
            Contains("Kategorie: Skelettbau", notice);
            Contains("Familie: Sample family", notice);
            Contains("Revit-Typ: Sample type", notice);
            Contains("ASSEMBLY_CODE", notice);
            Contains("ALL_MODEL_TYPE_COMMENTS", notice);
            Contains("BGK-Quelle: Baugruppenkennzeichen | TEXT | Type", notice);
            Contains("nicht vorhanden", notice);
            Contains("vorhanden, aber leer", notice);
            Equal(0, Transaction.StartCount);
        });
        Test("successful diagnostics reconcile distinct elements versus rows and disclose capped skips", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions.Add(new() { LvItemId = "FirstItem", Quantity = new()
                { Kind = QuantityKind.Constant, Source = null, ConstantValue = 3, Unit = "m" } });
            template.Mappings[0].Conditions.Add(new() { Source = new() { Kind = ParameterKind.ByName,
                Name = "Include", Spec = "TEXT", Scope = ParameterScope.Instance }, Operand = OperandKind.Text, Text = "yes" });
            f.Instance.Parameters.Add(new("Include", SpecTypeId.String.Text, "yes", StorageType.String));
            var excluded = new FamilyInstance { Id = new(430000), TypeId = f.Type.Id };
            excluded.Parameters.Add(new("Include", SpecTypeId.String.Text, "no", StorageType.String));
            f.Document.Elements.Add(excluded);
            for (var i = 0; i < 101; i++) f.Document.Elements.Add(new FamilyInstance { Id = new(500000 + i), TypeId = new(999999) });
            f.Remember(template);
            Equal(Result.Succeeded, f.Recalculate(false, _ => { }));
            var notice = TaskDialog.Notices.Single();
            Contains("Untersuchte Modellelemente: 103 (verschiedene ElementIDs)", notice);
            Contains("Mit passender BGK-/Typzuordnung: 2", notice);
            Contains("Elemente mit Mengenzeile: 1 (verschiedene ElementIDs)", notice);
            Contains("Ermittelte Mengenzeilen: 2", notice);
            Contains("Elemente ohne Mengenzeile: 102", notice);
            Contains("Ohne vollständige BGK-/Typkennzeichnung: 101", notice);
            Contains("Diagnosedetails: 100 von 102", notice);
            Contains("2 weitere Elemente ohne Mengenzeile nicht angezeigt", notice);
            Contains("Ein Element kann mehrere Mengenzeilen erzeugen", notice);
            Contains("benutzerweit zuletzt gewählt, nicht projektgebunden", notice);
            Contains(f.TemplatePath, notice);
            Contains("Umfang: alle Modellelement-Instanzen", notice);
        });
        Test("three physical beams plus cameras HVAC zone and model line report only the genuinely unmarked beam", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions.Add(new() { LvItemId = "FirstItem", Quantity = new()
                { Kind = QuantityKind.Constant, Source = null, ConstantValue = 3, Unit = "m" } });
            f.Instance.Id = new(19100416);
            var emptyType = new FamilySymbol { Id = new(14549342), Name = "Bohrpfahlwandverbau Aussteifung HEB 300",
                Family = new() { Name = "HEB Träger" } };
            emptyType.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("Baugruppenkennzeichen", SpecTypeId.String.Text, "", StorageType.String);
            emptyType.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typenkommentare", SpecTypeId.String.Text, "", StorageType.String);
            f.Document.Elements.AddRange([emptyType,
                new FamilyInstance { Id = new(19101376), TypeId = emptyType.Id, Category = new() { Name = "Skelettbau" } },
                new FamilyInstance { Id = new(19101312), TypeId = f.Type.Id },
                new Autodesk.Revit.DB.Mechanical.Zone { Id = new(570537), Name = "Standard" },
                new ModelLine { Id = new(6247427), Name = "Modelllinien" }]);
            foreach (var id in new[] { 13032132, 17810710 })
                f.Document.Elements.Add(new Element { Id = new(id), Category = new() { Name = "Kameras", CanAddSubcategory = false } });
            foreach (var element in f.Document.Elements) element.VisibleInViews.Add(700);
            f.Remember(template);
            foreach (var visibleOnly in new[] { false, true })
            {
                TaskDialog.Notices.Clear();
                Equal(Result.Succeeded, f.Recalculate(visibleOnly, _ => { }));
                var notice = TaskDialog.Notices.Single();
                Contains("Untersuchte Modellelemente: 3", notice);
                Contains("Ohne vollständige BGK-/Typkennzeichnung: 1", notice);
                Contains("Elemente mit Mengenzeile: 2", notice);
                Contains("Ermittelte Mengenzeilen: 4", notice);
                Contains("Element 19101376: BGK '' / Typ '' unvollständig", notice);
                Contains("BGK: vorhanden, aber leer; Typ: vorhanden, aber leer", notice);
                Check(new[] { "13032132", "17810710", "570537", "6247427" }.All(id => !notice.Contains(id)),
                    "Camera/zone/model-line resources were diagnosed as physical model instances");
                Equal(4, AvaQuantityRunStorage.LastRun!.Records.Count);
                var collected = RevitQuantityReader.Elements(f.Document, template, viewId: visibleOnly ? new ElementId(700) : null);
                Check(collected.Select(element => element.Id).OrderBy(id => id).SequenceEqual(new[] { "19100416", "19101312", "19101376" }),
                    "Physical candidate IDs differ from the three actual placed beams");
            }
        });
        Test("project run captures multiple grouping parameters before the modeless window opens", f =>
        {
            f.Instance.Parameters.Add(new("Teilbereich", SpecTypeId.String.Text, "Raum A", StorageType.String));
            f.Type.Parameters.Add(new("Gewerk", SpecTypeId.String.Text, "Rohbau", StorageType.String));
            f.Remember(f.Template());
            Equal(Result.Succeeded, f.Execute());
            var record = AvaQuantityRunStorage.LastRun!.Records.Single();
            Check(record.GroupingValues.TryGetValue("Teilbereich (Instanz)", out var area) && area == "Raum A",
                "The instance grouping snapshot was not persisted");
            Check(record.GroupingValues.TryGetValue("Gewerk (Typ)", out var trade) && trade == "Rohbau",
                "The independent type grouping snapshot was not persisted");
            f.Instance.Parameters.Clear();
            f.Type.Parameters.Clear();
            Equal(Result.Succeeded, f.Execute());
            Equal("Raum A", AvaQuantityRunWindowHost.LastRun!.Records.Single().GroupingValues["Teilbereich (Instanz)"]);
        });
        Test("last successful takeoff is saved as the only current run in the active document", f =>
        {
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.FamilyAndType));
            f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var run = AvaQuantityRunStorage.LastRun ?? throw new Exception("No current run was written to project storage");
            Equal(1, AvaQuantityRunStorage.SaveCount);
            Equal("Testprojekt", run.ProjectTitle);
            Equal(Path.GetFullPath(f.Document.PathName), run.ProjectPath);
            Equal("B0000001", run.Records.Single().BimassistId);
            Equal("429496", run.Records.Single().RevitElementId);
            Equal("Familie und Typ", run.Records.Single().GroupingParameter);
            Equal("Sample family — Sample type", run.Records.Single().GroupingValue);
            Equal("Sample family", run.Records.Single().FamilyName);
            Equal("Sample type", run.Records.Single().TypeName);
            Equal(1, BIMassist.Ava.Revit.AvaQuantityRunWindowHost.ShowCount);
            Check(ReferenceEquals(run, BIMassist.Ava.Revit.AvaQuantityRunWindowHost.LastRun),
                "The run window must display the exact project-persisted snapshot");
            Check(f.Document.IsModifiable == false, "Project storage transaction remained open");
        });
        Test("second Execute reopens the persisted run before an unavailable template or grouping dialog", f =>
        {
            f.Remember(f.Template());
            Equal(Result.Succeeded, f.Execute());
            var run = AvaQuantityRunStorage.LastRun!;
            File.Delete(f.TemplatePath);
            f.Document.Elements.Clear(); // A fresh calculation cannot reproduce the saved result.
            AvaGroupingDialog.Choices.Enqueue(null);

            Equal(Result.Succeeded, f.Execute());
            Equal(1, AvaQuantityRunStorage.SaveCount);
            Equal(1, AvaGroupingDialog.Calls);
            Equal(0, CommonDialog.Shown.Count);
            Equal(0, TaskDialog.Notices.Count);
            Equal(2, AvaQuantityRunWindowHost.ShowCount);
            Check(ReferenceEquals(run, AvaQuantityRunWindowHost.LastRun), "Reopening replaced the persisted snapshot");
            Equal(1, run.Records.Count);
        });
        foreach (var visibleOnly in new[] { true, false })
        Test("recalculation replaces the saved run through its callback using supplied visibleOnly=" + visibleOnly, f =>
        {
            f.Instance.VisibleInViews.Add(700);
            f.Document.Elements.Add(new FamilyInstance { Id = new(430000), TypeId = f.Type.Id });
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.FamilyAndType));
            f.Remember(f.Template());
            Equal(Result.Succeeded, f.Execute());
            var previous = AvaQuantityRunStorage.LastRun!;
            Equal(2, previous.Records.Count);
            var changedTemplate = f.Template();
            changedTemplate.Mappings[0].Positions[0].Quantity!.ConstantValue = 7;
            f.Remember(changedTemplate);
            AvaGroupingDialog.Choices.Enqueue(null); // Recalculation must not ask for grouping again.
            AvaQuantityRun? presented = null;
            var callbackCount = 0;

            Equal(Result.Succeeded, f.Recalculate(visibleOnly, run =>
            {
                presented = run;
                callbackCount++;
                Check(!f.Document.IsModifiable, "Presentation ran inside the storage transaction");
                Check(ReferenceEquals(AvaQuantityRunStorage.Load(f.Document), run), "Callback did not receive the persisted run");
            }));
            Equal("", f.Message);
            Equal(1, callbackCount);
            Check(presented != null && presented.RunId != previous.RunId, "Recalculation just reopened the old run");
            Equal(2, AvaQuantityRunStorage.SaveCount);
            Check(Transaction.LastCommitForcedModal, "Recalculation committed without modal failure handling");
            Equal(visibleOnly ? 1 : 2, presented!.Records.Count);
            Equal(visibleOnly, (bool?)typeof(AvaQuantityRun).GetProperty("VisibleOnly")?.GetValue(presented));
            Check(presented.Records.All(record => record.Result == 7m && record.Unit == "m"), "New quantities or LV units were not persisted");
            Equal("", presented.Records[0].GroupingParameter);
            Contains(visibleOnly ? "Revit-Ansichtssichtbarkeit" : "alle Modellelement-Instanzen", presented.ScopeDescription);
            Equal(1, AvaGroupingDialog.Calls);
            Equal(1, AvaQuantityRunWindowHost.ShowCount);
            Equal(0, CommonDialog.Shown.Count);
        });
        Test("scope options default to all model elements and identify the active view", f =>
        {
            var hidden = new FamilyInstance { Id = new(430000), TypeId = f.Type.Id };
            f.Document.Elements.Add(hidden);
            f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            Equal(1, AvaGroupingDialog.Calls);
            Equal(700L, AvaGroupingDialog.LastViewId);
            var run = AvaQuantityRunStorage.LastRun!;
            Contains("alle Modellelement-Instanzen im aktuellen Hauptmodell", run.ScopeDescription);
            Equal(2, run.Records.Count);
        });
        Test("storage commit forces modal failure handling and reopening starts no transaction", f =>
        {
            f.Remember(f.Template());
            Equal(Result.Succeeded, f.Execute());
            Check(Transaction.LastCommitForcedModal, "Storage commit did not force modal handling; Pending remains possible");
            Equal(1, Transaction.StartCount);
            Equal(Result.Succeeded, f.Execute());
            Equal(1, Transaction.StartCount);
        });
        Test("rolled-back recalculation never claims success or presents an uncommitted replacement", f =>
        {
            f.Remember(f.Template());
            Equal(Result.Succeeded, f.Execute());
            var previous = AvaQuantityRunStorage.LastRun!;
            Transaction.NextCommitStatus = TransactionStatus.RolledBack;
            var callbacks = 0;
            Equal(Result.Failed, f.Recalculate(false, _ => callbacks++));
            Contains("nicht im Revit-Projekt bestätigt", f.Message);
            Equal(0, callbacks);
            Equal(1, AvaQuantityRunWindowHost.ShowCount);
            Check(ReferenceEquals(previous, AvaQuantityRunStorage.Load(f.Document)), "Rollback lost the last confirmed snapshot");
            Check(!f.Document.IsModifiable, "Rollback left the storage transaction open");
        });
        Test("failed recalculation preserves the last run and its existing error details", f =>
        {
            f.Remember(f.Template());
            Equal(Result.Succeeded, f.Execute());
            var previous = AvaQuantityRunStorage.LastRun!;
            var invalid = f.Template();
            invalid.Mappings[0].Positions[0].Quantity = new() { Unit = "m", Source = new()
                { Kind = ParameterKind.LocalFamily, Name = "Missing", Spec = "LENGTH", Scope = ParameterScope.Instance } };
            f.Remember(invalid);
            var callbacks = 0;
            Equal(Result.Failed, f.Recalculate(false, _ => callbacks++));
            Contains("Missing", f.Message);
            Contains("429496", f.Message);
            Equal(0, callbacks);
            Equal(1, Transaction.StartCount);
            Equal(1, AvaQuantityRunStorage.SaveCount);
            Check(ReferenceEquals(previous, AvaQuantityRunStorage.Load(f.Document)), "Calculation error replaced the last run");
        });
        Test("empty recalculation cancels without discarding the saved run", f =>
        {
            f.Remember(f.Template());
            Equal(Result.Succeeded, f.Execute());
            var previous = AvaQuantityRunStorage.LastRun!;
            var callbacks = 0;
            Equal(Result.Cancelled, f.Recalculate(true, _ => callbacks++)); // No element is visible in this view.
            Equal(0, callbacks);
            Equal(1, Transaction.StartCount);
            Equal(1, AvaQuantityRunStorage.SaveCount);
            Check(ReferenceEquals(previous, AvaQuantityRunStorage.Load(f.Document)), "Empty recalculation discarded the last run");
            Contains("Keine passenden Mengenzeilen", TaskDialog.Notices.Single());
        });
        Test("visible-only scope passes the active view to collection and persists only its visible instances", f =>
        {
            f.Instance.VisibleInViews.Add(700);
            var hidden = new FamilyInstance { Id = new(430000), TypeId = f.Type.Id };
            f.Document.Elements.Add(hidden);
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.Position, VisibleOnly: true));
            f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var run = AvaQuantityRunStorage.LastRun!;
            Contains("Revit-Ansichtssichtbarkeit in „Grundriss EG“", run.ScopeDescription);
            Equal(true, (bool?)typeof(AvaQuantityRun).GetProperty("VisibleOnly")?.GetValue(run));
            Contains("Elemente außerhalb des Zuschneidebereichs können dennoch enthalten sein", run.ScopeDescription);
            Equal(1, run.Records.Count);
        });
        Test("last-run snapshot embeds all source LV positions for later complete X82 export", f =>
        {
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.Position, ExportLvX82: true));
            f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var run = AvaQuantityRunStorage.LastRun!;
            Equal(1, TemplateJson.Deserialize(run.TemplateJson).LvCatalog.Entries.Count);
            Equal("FirstItem", run.Records.Single().LvItemId);
            Equal(0, CommonDialog.Shown.Count);
        });
        Test("successful takeoff reveals per-element identification and mapping skips", f =>
        {
            var unmatchedType = new FamilySymbol { Id = new(701) };
            unmatchedType.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, "999", StorageType.String);
            unmatchedType.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, "99", StorageType.String);
            f.Document.Elements.Add(unmatchedType);
            var unmarkedType = new FamilySymbol { Id = new(702) };
            f.Document.Elements.Add(unmarkedType);
            for (var id = 500; id <= 520; id++) f.Document.Elements.Add(new FamilyInstance { Id = new(id), TypeId = unmarkedType.Id });
            for (var id = 600; id <= 609; id++) f.Document.Elements.Add(new FamilyInstance { Id = new(id), TypeId = unmatchedType.Id });
            f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var details = TaskDialog.Notices.Single();
            Contains("Elemente ohne Mengenzeile", details);
            Contains("Element 520: BGK '' / Typ '' unvollständig", details);
            Contains("Element 609: BGK '999' / Typ '99': keine exakte Zuordnung.", details);
        });
        Test("visible-only scope rejects a template active view without producing output", f =>
        {
            f.Data.Application.ActiveUIDocument!.ActiveView.IsTemplate = true;
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.Position, VisibleOnly: true));
            f.Remember(f.Template());
            Equal(Result.Failed, f.Execute()); Contains("Ansichtsvorlage", f.Message); f.NoOutput();
            Equal(0, TaskDialog.Confirmations.Count);
        });
        Test("actual Execute stores converted real model-parameter results in the project run", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions[0].Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily,
                Scope = ParameterScope.Instance, Name = "Length", Spec = "LENGTH" }, Unit = "m" };
            f.Instance.Parameters.Add(new("Length", SpecTypeId.Length, 10d, StorageType.Double));
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute()); Equal("", f.Message);
            var record = AvaQuantityRunStorage.LastRun!.Records.Single();
            Equal("FirstItem", record.LvItemId);
            Equal("m", record.Unit);
            Equal(3.048m, record.Result);
            Equal(0, CommonDialog.Shown.Count);
            Check(File.ReadAllText(f.TemplatePath) == TemplateJson.Serialize(template), "Template mutated");
        });
        Test("actual Execute carries the stored formula and substituted values into the project run", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions[0].Quantity = new() { Kind = QuantityKind.Formula, Source = null,
                Expression = "Length*2", Unit = "m", Variables = [new() { Name = "Length", Unit = "cm",
                    Source = new() { Kind = ParameterKind.LocalFamily, Scope = ParameterScope.Instance, Name = "Length", Spec = "LENGTH" } }] };
            f.Instance.Parameters.Add(new("Length", SpecTypeId.Length, 10d, StorageType.Double));
            f.Remember(template); f.ApproveAndSave(); Equal(Result.Succeeded, f.Execute());
            var record = AvaQuantityRunStorage.LastRun!.Records.Single();
            Check(record.FormulaWithParameters.StartsWith("Length*2", StringComparison.Ordinal), "Symbolic formula was not persisted.");
            Contains("304,8", record.FormulaWithValues);
            Equal(6.096m, record.Result);
            Equal(File.ReadAllText(f.TemplatePath), TemplateJson.Serialize(template));
        });
        Test("differing named parameter values prompt once and the opt-in remembered choice is reused", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions[0].Quantity = new() { Source = new() { Kind = ParameterKind.ByName,
                Scope = ParameterScope.Instance, Name = "Länge", Spec = "LENGTH" }, Unit = "m" };
            f.Instance.Parameters.Add(new("Länge", SpecTypeId.Length, 1d, StorageType.Double) { Id = new(10) });
            f.Instance.Parameters.Add(new("Länge", SpecTypeId.Length, 2d, StorageType.Double) { Id = new(11) });
            var source = template.Mappings[0].Positions[0].Quantity!.Source!;
            var key = AvaParameterChoiceStore.Key(Path.GetFullPath(f.Document.PathName), template.ProfileId, f.Type.UniqueId, source);
            new AvaParameterChoiceStore(f.ChoiceSettings).Remember(key, "Id:stale-choice");
            f.Remember(template);
            BIMassist.Ava.Views.ParameterChoiceWindow.NextIdentity = "Id:10";
            BIMassist.Ava.Views.ParameterChoiceWindow.RememberNext = true;
            f.ApproveAndSave(); Equal(Result.Succeeded, f.Execute());
            Equal(1, BIMassist.Ava.Views.ParameterChoiceWindow.Shown);
            Check(File.Exists(f.ChoiceSettings), "Opt-in parameter choice was not persisted");

            BIMassist.Ava.Views.ParameterChoiceWindow.NextIdentity = null;
            BIMassist.Ava.Views.ParameterChoiceWindow.RememberNext = false;
            Equal(Result.Succeeded, f.Recalculate(false, _ => { }));
            Equal(1, BIMassist.Ava.Views.ParameterChoiceWindow.Shown);
            Equal(2, AvaQuantityRunStorage.SaveCount);
            Equal(1, AvaQuantityRunWindowHost.ShowCount);
        });
        Test("cancelling a differing-parameter choice cancels takeoff without producing an X31", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions[0].Quantity = new() { Source = new() { Kind = ParameterKind.ByName,
                Scope = ParameterScope.Instance, Name = "Volumen", Spec = "VOLUME" }, Unit = "m3" };
            f.Instance.BuiltIns[BuiltInParameter.HOST_VOLUME_COMPUTED] = new("Volumen", SpecTypeId.Volume, 100d, StorageType.Double)
                { Id = new((long)BuiltInParameter.HOST_VOLUME_COMPUTED) };
            f.Instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 200d, StorageType.Double) { Id = new(10) });
            f.Remember(template);
            Equal(Result.Cancelled, f.Execute());
            Equal(1, BIMassist.Ava.Views.ParameterChoiceWindow.Shown);
            Equal(0, TaskDialog.Confirmations.Count); f.NoOutput();
        });
        Test("grouping selection is persisted with its exact parameter label and value", f =>
        {
            f.Instance.Parameters.Add(new("Teilbereich", SpecTypeId.String.Text, "Erdgeschoss Nord", StorageType.String));
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.BuildingArea, new("Teilbereich", ParameterScope.Instance)));
            f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var record = AvaQuantityRunStorage.LastRun!.Records.Single();
            Equal("Teilbereich (Instanz)", record.GroupingParameter);
            Equal("Erdgeschoss Nord", record.GroupingValue);
        });
        Test("family and type grouping uses exact Revit family/type names", f =>
        {
            f.Type.Family.Name = "Rohrfamilie"; f.Type.Name = "DN 20";
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.FamilyAndType)); f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var record = AvaQuantityRunStorage.LastRun!.Records.Single();
            Equal("Familie und Typ", record.GroupingParameter);
            Equal("Rohrfamilie — DN 20", record.GroupingValue);
        });
        Test("persisted run preserves the full 64-bit Revit ElementID for later X31 export", f =>
        {
            f.Instance.Id = new(4294967296); f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            Equal("4294967296", AvaQuantityRunStorage.LastRun!.Records.Single().RevitElementId);
        });
        Test("cancelled grouping choice creates no confirmation or X31 file", f =>
        {
            AvaGroupingDialog.Choices.Enqueue(null); f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); Equal(0, TaskDialog.Confirmations.Count); Equal(0, CommonDialog.Shown.Count);
        });
        Test("missing project fails before template or save dialogs", f =>
        {
            f.Data.Application.ActiveUIDocument = null;
            Equal(Result.Failed, f.Execute()); Contains("Revit-Projekt", f.Message); f.NoOutput(); Equal(0, CommonDialog.Shown.Count);
        });
        Test("family document fails without file or dialogs", f =>
        {
            f.Document.IsFamilyDocument = true;
            Equal(Result.Failed, f.Execute()); Contains("Familieneditor", f.Message); f.NoOutput(); Equal(0, CommonDialog.Shown.Count);
        });
        Test("initial template selection can be cancelled", f =>
        {
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); Equal(0, TaskDialog.Confirmations.Count);
            Check(CommonDialog.Shown.Single().Dialog is OpenFileDialog, "Expected template picker");
        });
        Test("initial valid template selection is remembered and opens the unified run window", f =>
        {
            File.WriteAllText(f.TemplatePath, TemplateJson.Serialize(f.Template()));
            CommonDialog.OpenChoices.Enqueue(f.TemplatePath); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute()); Equal(f.TemplatePath, new AvaWorkspace().LastTemplatePath!);
            Equal(1, CommonDialog.Shown.Count); Equal(1, AvaQuantityRunStorage.SaveCount);
            Equal(1, AvaQuantityRunWindowHost.ShowCount);
        });
        Test("successful takeoff opens the run window without a separate export confirmation", f =>
        {
            f.Remember(f.Template()); TaskDialog.Answers.Enqueue(TaskDialogResult.No);
            Equal(Result.Succeeded, f.Execute());
            Equal(1, AvaQuantityRunStorage.SaveCount); Equal(1, AvaQuantityRunWindowHost.ShowCount);
            Equal(0, CommonDialog.Shown.Count); Equal(0, TaskDialog.Confirmations.Count);
        });
        Test("stale export-dialog choices cannot suppress the persisted main window", f =>
        {
            f.Remember(f.Template()); TaskDialog.Answers.Enqueue(TaskDialogResult.Yes); CommonDialog.SaveChoices.Enqueue(null);
            Equal(Result.Succeeded, f.Execute()); Equal(0, CommonDialog.Shown.Count);
            Equal(1, AvaQuantityRunStorage.SaveCount); Check(AvaQuantityRunStorage.LastRun != null, "Export cancellation discarded the valid last run");
            Equal(1, AvaQuantityRunWindowHost.ShowCount);
            Check(ReferenceEquals(AvaQuantityRunStorage.LastRun, AvaQuantityRunWindowHost.LastRun),
                "Cancelling the optional export must leave the persisted takeoff available in its main window.");
        });
        Test("failed DN condition identifies the active template, BGK, type, LV and expected dimension", f =>
        {
            var template = f.Template();
            template.Mappings[0].Conditions.Add(new() { Source = new() { Kind = ParameterKind.LocalFamily,
                Name = "AVA_DN", Spec = "LENGTH", Scope = ParameterScope.Type },
                Operator = ConditionOperator.Equal, Operand = OperandKind.Numeric, Number = 625, Unit = "mm" });
            f.Remember(template);
            Equal(Result.Failed, f.Execute()); f.NoOutput();
            Contains(f.TemplatePath, f.Message); Contains("BGK 001 / Typ 02", f.Message);
            Contains("LV 0010", f.Message); Contains("AVA_DN Equal 625 mm", f.Message);
            Contains(template.Mappings[0].Id.ToString(), f.Message);
            Check(!f.Message.Contains("Element 429496: Element 429496"), "Element prefix is duplicated");
            Equal(0, CommonDialog.Shown.Count); Equal(0, TaskDialog.Confirmations.Count);
        });
        Test("one failed quantity blocks all valid sibling rows and never asks to save", f =>
        {
            var template = f.Template(); template.Mappings[0].Positions.Add(new() { LvItemId = "FirstItem", Quantity = new()
                { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Missing", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" } });
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Failed, f.Execute()); Contains("Keine X31-Datei", f.Message); Contains("Missing", f.Message);
            Contains("429496", f.Message); f.NoOutput(); Equal(0, CommonDialog.Shown.Count); Equal(0, TaskDialog.Confirmations.Count);
            Equal(0, AvaQuantityRunStorage.SaveCount);
        });
        foreach (var defect in new[] { "spec", "storage" })
        Test("actual Execute blocks a valueless wrong-" + defect + " identification even with valid sibling elements", f =>
        {
            var template = f.Template();
            var badType = new FamilySymbol { Id = new(201) };
            badType.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK",
                defect == "spec" ? SpecTypeId.Length : SpecTypeId.String.Text, 0d, StorageType.Double) { HasValue = false };
            badType.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, "02", StorageType.String);
            f.Document.Elements.AddRange([badType, new FamilyInstance { Id = new(101), TypeId = badType.Id }]);
            f.Remember(template); f.ApproveAndSave();
            var outcome = f.Execute();
            // The old bug exported the valid sibling. Check the actual file, not just a fabricated reader result.
            f.NoOutput(); Equal(Result.Failed, outcome); Contains("Identification", f.Message);
            Contains(defect == "spec" ? "Parameterspezifikation" : "Zeichenfolge", f.Message);
            Equal(0, CommonDialog.Shown.Count); Equal(0, TaskDialog.Confirmations.Count);
        });
        Test("fatal quantity failure reports complete scope counts in message and dialog without replacing a target", f =>
        {
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.Position, VisibleOnly: true));
            var template = f.Template();
            template.Mappings[0].Positions.Add(new() { LvItemId = "FirstItem", Quantity = new()
                { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Absent", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" } });
            var unmatchedType = new FamilySymbol { Id = new(202) };
            unmatchedType.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, "001", StorageType.String);
            unmatchedType.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, "03", StorageType.String);
            f.Document.Elements.AddRange([new FamilyInstance { Id = new(101) }, unmatchedType,
                new FamilyInstance { Id = new(102), TypeId = unmatchedType.Id }]);
            foreach (var instance in f.Document.Elements.OfType<FamilyInstance>()) instance.VisibleInViews.Add(700);
            var original = new byte[] { 0, 255, 31, 128, 0 }; File.WriteAllBytes(f.Output, original);
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Failed, f.Execute());
            Check(original.SequenceEqual(File.ReadAllBytes(f.Output)), "Fatal result replaced existing output");
            Equal(1, System.IO.Directory.GetFiles(f.Directory, "*.x31").Length);
            Equal(0, CommonDialog.Shown.Count); Equal(0, TaskDialog.Confirmations.Count);
            Contains("Untersuchte Modellelemente: 3", f.Message);
            Contains("Ohne vollständige BGK-/Typkennzeichnung: 1", f.Message);
            Contains("Ohne passende BGK-/Typzuordnung: 1", f.Message);
            Contains("Ermittelte Mengenzeilen (nicht exportiert): 1", f.Message);
            Contains("1 Fehler", f.Message); Contains("Absent", f.Message);
            Contains("Umfang: Revit-Ansichtssichtbarkeit in „Grundriss EG“", f.Message);
            Contains("dauerhaft in Ansicht oder Kategorie sowie temporär ausgeblendete Elemente ausgeschlossen", f.Message);
            Contains("Zoom-/Pan-Bildausschnitt nicht maßgeblich", f.Message);
            Contains("Elemente außerhalb des Zuschneidebereichs können dennoch enthalten sein", f.Message);
            Contains("keine pixelgenaue Sichtbarkeits- oder Verdeckungsprüfung", f.Message);
            Equal(f.Message, TaskDialog.Notices.Single());
        });
        Test("fatal error detail limit discloses total, displayed and omitted counts", f =>
        {
            var template = f.Template();
            for (var i = 1; i <= 22; i++)
                template.Mappings[0].Positions.Add(new() { LvItemId = "FirstItem", Quantity = new()
                    { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Missing" + i, Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" } });
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Failed, f.Execute()); f.NoOutput();
            Contains("22 Fehler", f.Message); Contains("erste 15 von 22", f.Message); Contains("7 weitere Fehler", f.Message);
            Contains("Missing15", f.Message); Check(!f.Message.Contains("Missing16"), "Error detail limit ignored");
            Contains("Ermittelte Mengenzeilen (nicht exportiert): 1", f.Message);
            Equal(f.Message, TaskDialog.Notices.Single()); Equal(0, CommonDialog.Shown.Count);
        });
        Test("empty model returns no rows and no save dialog", f =>
        {
            f.Remember(f.Template()); f.Document.Elements.Clear();
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); Contains("Untersuchte Modellelemente: 0", TaskDialog.Notices.Single());
            Contains("Umfang: alle Modellelement-Instanzen im aktuellen Hauptmodell", TaskDialog.Notices.Single());
            Equal(0, CommonDialog.Shown.Count);
        });
        Test("empty visible-only result discloses the active view scope", f =>
        {
            f.Document.Elements.Remove(f.Instance);
            f.Document.Elements.Remove(f.Type);
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.Position, VisibleOnly: true));
            f.Remember(f.Template());
            Equal(Result.Cancelled, f.Execute());
            Contains("Umfang: Revit-Ansichtssichtbarkeit in „Grundriss EG“", TaskDialog.Notices.Single());
            Contains("dauerhaft in Ansicht oder Kategorie sowie temporär ausgeblendete Elemente ausgeschlossen", TaskDialog.Notices.Single());
            Contains("Zoom-/Pan-Bildausschnitt nicht maßgeblich", TaskDialog.Notices.Single());
            Contains("Elemente außerhalb des Zuschneidebereichs können dennoch enthalten sein", TaskDialog.Notices.Single());
            Contains("keine pixelgenaue Sichtbarkeits- oder Verdeckungsprüfung", TaskDialog.Notices.Single());
            Equal(0, CommonDialog.Shown.Count);
        });
        Test("missing and unmatched marks are counted independently; types and annotations excluded", f =>
        {
            f.Remember(f.Template());
            f.Document.Elements.Remove(f.Instance);
            f.Document.Elements.Add(new FamilyInstance { Id = new(101) }); // no type/marks
            var otherType = new FamilySymbol { Id = new(202) };
            otherType.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, "001", StorageType.String);
            otherType.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, "03", StorageType.String);
            f.Document.Elements.AddRange([otherType, new FamilyInstance { Id = new(102), TypeId = otherType.Id },
                new ElementType { Id = new(900) }, new Element { Id = new(901), Category = null },
                new Element { Id = new(902), Category = new() { CategoryType = CategoryType.Annotation } }]);
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); var notice = TaskDialog.Notices.Single();
            Contains("Untersuchte Modellelemente: 2", notice); Contains("Typkennzeichnung: 1", notice); Contains("Typzuordnung: 1", notice);
        });
        foreach (var linkKind in new[] { "Revit", "CAD" })
        Test("actual Execute excludes matching " + linkKind + " link containers from host counts and saved rows", f =>
        {
            Element link = linkKind == "Revit" ? new RevitLinkInstance() : new ImportInstance { IsLinked = true };
            link.Id = new(501); link.TypeId = f.Instance.TypeId;
            f.Document.Elements.Add(link); f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            Equal(1, AvaQuantityRunStorage.LastRun!.Records.Count);
        });
        Test("compatible Revit quantity and LV units are converted before the persisted run", f =>
        {
            var template = f.Template(); template.Mappings[0].Positions[0].Quantity = new() { Source = new()
                { Kind = ParameterKind.ByName, Scope = ParameterScope.Instance, Name = "Länge", Spec = "LENGTH" }, Unit = "cm" };
            f.Instance.Parameters.Add(new("Länge", SpecTypeId.Length, 2d, StorageType.Double));
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var record = AvaQuantityRunStorage.LastRun!.Records.Single();
            Equal("m", record.Unit);
            Equal(0.6096m, record.Result);
        });
        Test("incompatible quantity and LV unit blocks before confirmation", f =>
        {
            var template = f.Template(); template.Mappings[0].Positions[0].Quantity!.Unit = "St";
            f.Remember(template); TaskDialog.Answers.Enqueue(TaskDialogResult.No);
            Equal(Result.Failed, f.Execute()); f.NoOutput(); Equal(0, TaskDialog.Confirmations.Count);
            Contains("nicht umrechenbar", f.Message);
        });
        Test("missing source LV snapshot is preserved for explicit X82 rejection, without blocking the takeoff", f =>
        {
            var template = f.Template(); template.LvCatalog = new(new("fixture", "test", ""), template.LvCatalog.Entries);
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var run = AvaQuantityRunStorage.LastRun!;
            Check(string.IsNullOrEmpty(TemplateJson.Deserialize(run.TemplateJson).LvCatalog.Source.X82SnapshotBase64), "Missing X82 source was synthesized in the persisted template.");
            var rejected = false;
            try { AvaQuantityRunExport.SerializeX82(run); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "X82 export accepted a missing immutable source LV snapshot.");
        });
        Test("takeoff no longer opens any file writer before the unified run window", f =>
        {
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.Position, VisibleOnly: true));
            f.Instance.VisibleInViews.Add(700);
            f.Remember(f.Template()); TaskDialog.Answers.Enqueue(TaskDialogResult.Yes);
            CommonDialog.SaveChoices.Enqueue(Path.Combine(f.Directory, "missing-directory", "takeoff.x31"));
            Equal(Result.Succeeded, f.Execute());
            Equal(1, AvaQuantityRunWindowHost.ShowCount);
            Equal(0, CommonDialog.Shown.Count);
        });
        Test("invalid chosen JSON fails without file or remembered invalid path", f =>
        {
            File.WriteAllText(f.TemplatePath, "{broken"); CommonDialog.OpenChoices.Enqueue(f.TemplatePath);
            Equal(Result.Failed, f.Execute()); f.NoOutput(); Check(!File.Exists(f.Settings), "Invalid template remembered");
        });
        Test("invalid remembered template can be replaced by choosing a valid one", f =>
        {
            f.Remember(f.Template()); File.WriteAllText(f.TemplatePath, "{broken");
            var valid = Path.Combine(f.Directory, "valid.json"); File.WriteAllText(valid, TemplateJson.Serialize(f.Template()));
            CommonDialog.OpenChoices.Enqueue(valid); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute()); Check(AvaQuantityRunStorage.LastRun != null, "Recovery did not persist the run");
            Equal(valid, new AvaWorkspace().LastTemplatePath!);
        });
        Test("stale export paths cannot overwrite the active template or workspace settings during takeoff", f =>
        {
            f.Remember(f.Template()); var templateBytes = File.ReadAllBytes(f.TemplatePath); var settingsBytes = File.ReadAllBytes(f.Settings);
            foreach (var protectedPath in new[] { f.TemplatePath, f.Settings })
            {
                TaskDialog.Answers.Enqueue(TaskDialogResult.Yes); CommonDialog.SaveChoices.Enqueue(protectedPath);
                Equal(Result.Succeeded, f.Execute());
                Check(templateBytes.SequenceEqual(File.ReadAllBytes(f.TemplatePath)), "Template overwritten by X31");
                Check(settingsBytes.SequenceEqual(File.ReadAllBytes(f.Settings)), "Workspace overwritten by X31");
            }
        });
        Test("invalid remembered template selection can be cancelled without touching it", f =>
        {
            f.Remember(f.Template()); File.WriteAllText(f.TemplatePath, "{broken"); CommonDialog.OpenChoices.Enqueue(null);
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); Equal("{broken", File.ReadAllText(f.TemplatePath));
            Equal(0, TaskDialog.Confirmations.Count);
        });
        Test("template picker is Revit-owned; exports are deferred to the modeless run window", f =>
        {
            File.WriteAllText(f.TemplatePath, TemplateJson.Serialize(f.Template()));
            CommonDialog.OpenChoices.Enqueue(f.TemplatePath); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            Equal(1, CommonDialog.Shown.Count);
            Check(CommonDialog.Shown.Single(s => s.Dialog is OpenFileDialog).Owner == f.Data.Application.MainWindowHandle, "Template picker Revit owner missing");
            Equal(1, System.Windows.Window.Created); Equal(System.Windows.Window.Created, System.Windows.Window.Closed);
        });
        Test("native template picker does not depend on a synthetic WPF owner", f =>
        {
            f.Data.Application.MainWindowHandle = IntPtr.Zero;
            File.WriteAllText(f.TemplatePath, TemplateJson.Serialize(f.Template()));
            CommonDialog.OpenChoices.Enqueue(f.TemplatePath); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute()); Equal(IntPtr.Zero, CommonDialog.Shown.Single().Owner);
            Equal(0, System.Windows.Window.Created);
        });
        Test("persisted calculation retains full Revit ID for later export", f =>
        {
            f.Remember(f.Template()); f.ApproveAndSave(); Equal(Result.Succeeded, f.Execute());
            Equal("429496", AvaQuantityRunStorage.LastRun!.Records.Single().RevitElementId);
        });
        Console.WriteLine($"Execute integration with Revit/UI/Win32 doubles and real core/writer: {passed} passed; {failed} failed (not a Revit-host test)");
        if (failed != 0) Environment.ExitCode = 1;
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch"), "bimassist-command-" + Guid.NewGuid().ToString("N"));
        public string Settings => Path.Combine(Directory, "workspace.json");
        public string ChoiceSettings => Path.Combine(Directory, "parameter-choices.json");
        public string TemplatePath => Path.Combine(Directory, "template.json");
        public string Output => Path.Combine(Directory, "takeoff.x31");
        public Document Document { get; } = new() { Title = "Testprojekt", PathName = @"C:\model\project.rvt" };
        public FamilyInstance Instance { get; } = new() { Id = new(429496), TypeId = new(200), UniqueId = "actual-element-unique-identity" };
        public FamilySymbol Type { get; } = new() { Id = new(200), Family = new() { Name = "Sample family" }, Name = "Sample type" };
        public ExternalCommandData Data { get; } = new();
        public string Message = "";
        private readonly string? previousSettings;
        private readonly string? previousChoiceSettings;
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            previousSettings = Environment.GetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS");
            previousChoiceSettings = Environment.GetEnvironmentVariable("BIMASSIST_AVA_PARAMETER_CHOICES");
            Environment.SetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS", Settings);
            Environment.SetEnvironmentVariable("BIMASSIST_AVA_PARAMETER_CHOICES", ChoiceSettings);
            TaskDialog.Reset(); CommonDialog.Reset(); AvaGroupingDialog.Reset(); AvaQuantityRunStorage.Reset(); BIMassist.Ava.Views.ParameterChoiceWindow.Reset(); BIMassist.Ava.Views.AvaTakeoffOptionsWindow.Reset(); BIMassist.Ava.Views.LvX82ViewerWindow.Reset();
            System.Windows.Window.Created = System.Windows.Window.Closed = 0;
            Transaction.Reset();
            BIMassist.Ava.Revit.AvaQuantityRunWindowHost.Reset();
            var symbol = Type;
            symbol.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, "001", StorageType.String);
            symbol.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, "02", StorageType.String);
            var activeView = new Autodesk.Revit.DB.View { Id = new(700), Name = "Grundriss EG", Category = null };
            Document.Elements.AddRange([Instance, symbol, activeView]); Data.Application.ActiveUIDocument = new(Document) { ActiveView = activeView };
        }
        public AvaTemplate Template()
        {
            var stamp = new SourceStamp("fixture", "test", "");
            return new() { Name = "Ausführungstest", ReviewScope = "alle Hauptmodellinstanzen",
                BgkCatalog = new(stamp, [new("001", "Group", "Category", [new("02", "Type")])]),
                LvCatalog = CatalogReaders.ReadX82(new MemoryStream(Encoding.UTF8.GetBytes(X82)), "missing-source.x82"),
                Mappings = [new() { BgkKey = "001", TypeNumber = "02", Positions = [new() { LvItemId = "FirstItem",
                    Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 2, Unit = "m" } }] }] };
        }
        public void Remember(AvaTemplate template) { File.WriteAllText(TemplatePath, TemplateJson.Serialize(template)); new AvaWorkspace().Remember(TemplatePath); }
        public void ApproveAndSave() { TaskDialog.Answers.Enqueue(TaskDialogResult.Yes); CommonDialog.SaveChoices.Enqueue(Output); }
        public Result Execute() => new AvaQuantityTakeoffCommand().Execute(Data, ref Message, new ElementSet());
        public Result Recalculate(bool visibleOnly, Action<AvaQuantityRun> present)
        {
            var method = typeof(AvaQuantityTakeoffCommand).GetMethod("Recalculate",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                [typeof(UIApplication), typeof(bool), typeof(Action<AvaQuantityRun>), typeof(string).MakeByRefType()]);
            Check(method != null && method.IsAssembly && method.GetParameters()[3].IsOut,
                "Missing exact internal static Recalculate callback interface");
            return AvaQuantityTakeoffCommand.Recalculate(Data.Application, visibleOnly, present, out Message);
        }
        public void NoOutput() { Check(!System.IO.Directory.EnumerateFiles(Directory, "*.x31", SearchOption.AllDirectories).Any(), "Unexpected output on failure/cancellation"); }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS", previousSettings);
            Environment.SetEnvironmentVariable("BIMASSIST_AVA_PARAMETER_CHOICES", previousChoiceSettings);
            System.IO.Directory.Delete(Directory, true);
        }
    }
    private static void Test(string name, Action<Fixture> action)
    {
        using var f = new Fixture();
        try { action(f); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message + "; command message: " + f.Message); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Contains(string expected, string actual) => Check(actual.Contains(expected, StringComparison.Ordinal), "Missing " + expected + " in " + actual);
}
