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
        Test("actual Execute exports real model-parameter calculation to a readable X31", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions[0].Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily,
                Scope = ParameterScope.Instance, Name = "Length", Spec = "LENGTH" }, Unit = "m" };
            f.Instance.Parameters.Add(new("Length", SpecTypeId.Length, 10d, StorageType.Double));
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute()); Equal("", f.Message);
            var output = XDocument.Load(f.Output);
            var rows = output.Descendants(Ns + "QTakeoff").Select(e => e.Attribute("Row")!.Value).ToList();
            Check(rows.Any(r => r.Contains("913,048=")), "10 internal feet must export 3.048m without rounding");
            Check(output.Descendants(Ns + "Item").Single().Attribute("ID")!.Value == "FirstItem", "Original LV identity lost");
            Check(TaskDialog.Notices.Any(s => s.Contains("zurückgelesen")), "No verified-save notification");
            var picker = (FileSaveDialog)CommonDialog.Shown.Single().Dialog;
            Check(picker.Filter.Contains("*.x31", StringComparison.Ordinal) && picker.DefaultFilterEntry == "*.x31"
                && picker.InitialFileName == "Mengenermittlung.x31", "Native save-dialog defaults wrong");
            Equal(TaskDialogResult.No, TaskDialog.Confirmations.Single().DefaultButton);
            Check(File.ReadAllText(f.TemplatePath) == TemplateJson.Serialize(template), "Template mutated");
            Equal(1, Directory.GetFiles(f.Directory, "*.x31").Length);
        });
        Test("differing named parameter values prompt once and the opt-in remembered choice is reused", f =>
        {
            var template = f.Template();
            template.Mappings[0].Positions[0].Quantity = new() { Source = new() { Kind = ParameterKind.ByName,
                Scope = ParameterScope.Instance, Name = "Volumen", Spec = "VOLUME" }, Unit = "m3" };
            f.Instance.BuiltIns[BuiltInParameter.HOST_VOLUME_COMPUTED] = new("Volumen", SpecTypeId.Volume, 100d, StorageType.Double)
                { Id = new((long)BuiltInParameter.HOST_VOLUME_COMPUTED) };
            f.Instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 200d, StorageType.Double) { Id = new(10) });
            var source = template.Mappings[0].Positions[0].Quantity!.Source!;
            var key = AvaParameterChoiceStore.Key(Path.GetFullPath(f.Document.PathName), template.ProfileId, f.Type.UniqueId, source);
            new AvaParameterChoiceStore(f.ChoiceSettings).Remember(key, "Id:stale-choice");
            f.Remember(template);
            BIMassist.Ava.Views.ParameterChoiceWindow.NextIdentity = "BuiltIn:HOST_VOLUME_COMPUTED";
            BIMassist.Ava.Views.ParameterChoiceWindow.RememberNext = true;
            f.ApproveAndSave(); Equal(Result.Succeeded, f.Execute());
            Equal(1, BIMassist.Ava.Views.ParameterChoiceWindow.Shown);
            Check(File.Exists(f.ChoiceSettings), "Opt-in parameter choice was not persisted");

            BIMassist.Ava.Views.ParameterChoiceWindow.NextIdentity = null;
            BIMassist.Ava.Views.ParameterChoiceWindow.RememberNext = false;
            f.ApproveAndSave(); Equal(Result.Succeeded, f.Execute());
            Equal(1, BIMassist.Ava.Views.ParameterChoiceWindow.Shown);
            Check(File.Exists(f.Output), "Remembered parameter choice did not allow a subsequent export");
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
        Test("grouping selection is read from an arbitrary chosen text parameter and rendered like the screenshot", f =>
        {
            f.Instance.Parameters.Add(new("Teilbereich", SpecTypeId.String.Text, "Erdgeschoss Nord", StorageType.String));
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.BuildingArea, new("Teilbereich", ParameterScope.Instance)));
            f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var rows = XDocument.Load(f.Output).Descendants(Ns + "QTakeoff").Select(e => e.Attribute("Row")!.Value).ToArray();
            Check(rows.Any(r => r[12] == '*' && r.Substring(13, 56).TrimEnd() == "Baubereich: Erdgeschoss Nord"), "Selected group label missing");
            Check(rows.Any(r => r[12] != '*' && r.Substring(13, 9).Trim() == "429496"), "Element ID remark missing on the quantity row");
            Contains("Baubereich nach Parameter „Teilbereich (Instanz)“", TaskDialog.Confirmations.Single().MainContent);
        });
        Test("family and type grouping uses exact Revit family/type names", f =>
        {
            f.Type.Family.Name = "Rohrfamilie"; f.Type.Name = "DN 20";
            AvaGroupingDialog.Choices.Enqueue(new(QuantityGroupingMode.FamilyAndType)); f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var rows = XDocument.Load(f.Output).Descendants(Ns + "QTakeoff").Select(e => e.Attribute("Row")!.Value).ToArray();
            Check(rows.Any(r => r[12] == '*' && r.Substring(13, 56).TrimEnd() == "Familie und Typ: Rohrfamilie — DN 20"), "Exact family/type grouping comment missing");
            Check(rows.Any(r => r[12] != '*' && r.Substring(13, 9).Trim() == "429496"), "Element ID remark missing on quantity row");
            Contains("Gruppierung: nach Familienname und Typ", TaskDialog.Confirmations.Single().MainContent);
        });
        Test("X31 export fails closed when a Revit ID exceeds the nine-character REB remark", f =>
        {
            f.Instance.Id = new(4294967296); f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Failed, f.Execute()); Contains("max. 9 Zeichen", f.Message); f.NoOutput();
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
        Test("initial valid template selection is remembered then exported", f =>
        {
            File.WriteAllText(f.TemplatePath, TemplateJson.Serialize(f.Template()));
            CommonDialog.OpenChoices.Enqueue(f.TemplatePath); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute()); Equal(f.TemplatePath, new AvaWorkspace().LastTemplatePath!);
            Equal(2, CommonDialog.Shown.Count); Check(File.Exists(f.Output), "No output from actual writer");
        });
        Test("confirmation refusal blocks the SaveFileDialog", f =>
        {
            f.Remember(f.Template()); TaskDialog.Answers.Enqueue(TaskDialogResult.No);
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); Equal(0, CommonDialog.Shown.Count);
        });
        Test("save cancellation creates no file", f =>
        {
            f.Remember(f.Template()); TaskDialog.Answers.Enqueue(TaskDialogResult.Yes); CommonDialog.SaveChoices.Enqueue(null);
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); Equal(1, CommonDialog.Shown.Count);
        });
        Test("one failed quantity blocks all valid sibling rows and never asks to save", f =>
        {
            var template = f.Template(); template.Mappings[0].Positions.Add(new() { LvItemId = "FirstItem", Quantity = new()
                { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Missing", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" } });
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Failed, f.Execute()); Contains("Keine X31-Datei", f.Message); Contains("Missing", f.Message);
            Contains("429496", f.Message); f.NoOutput(); Equal(0, CommonDialog.Shown.Count); Equal(0, TaskDialog.Confirmations.Count);
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
            var template = f.Template();
            template.Mappings[0].Positions.Add(new() { LvItemId = "FirstItem", Quantity = new()
                { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Absent", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" } });
            var unmatchedType = new FamilySymbol { Id = new(202) };
            unmatchedType.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, "001", StorageType.String);
            unmatchedType.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, "03", StorageType.String);
            f.Document.Elements.AddRange([new FamilyInstance { Id = new(101) }, unmatchedType,
                new FamilyInstance { Id = new(102), TypeId = unmatchedType.Id }]);
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
        Test("actual Execute excludes matching " + linkKind + " link containers from host counts and exported rows", f =>
        {
            Element link = linkKind == "Revit" ? new RevitLinkInstance() : new ImportInstance { IsLinked = true };
            link.Id = new(501); link.TypeId = f.Instance.TypeId;
            f.Document.Elements.Add(link); f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var rows = XDocument.Load(f.Output).Descendants(Ns + "QTakeoff").Select(e => e.Attribute("Row")!.Value).ToList();
            Equal(1, rows.Count(r => r[12] != '*'));
            Contains("1 Modellelemente untersucht; 1 Mengenzeilen.", TaskDialog.Confirmations.Single().MainContent);
        });
        Test("different LV and result units are explicit and nonblocking only after Yes", f =>
        {
            var template = f.Template(); template.Mappings[0].Positions[0].Quantity!.Unit = "St";
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            var confirmation = TaskDialog.Confirmations.Single();
            Contains("Keine automatische Umrechnung", confirmation.MainContent); Contains("Menge St / LV-Einheit m", confirmation.ExpandedContent);
            var rows = XDocument.Load(f.Output).Descendants(Ns + "QTakeoff").Select(e => e.Attribute("Row")!.Value).ToList();
            Check(rows.Any(r => r.Contains("912=")), "Unit mismatch silently converted numeric result");
            Check(!rows.Any(r => r.Contains("Info:")), "Screenshot-oriented output includes extra explanation rows");
        });
        Test("unit mismatch refusal creates no file", f =>
        {
            var template = f.Template(); template.Mappings[0].Positions[0].Quantity!.Unit = "St";
            f.Remember(template); TaskDialog.Answers.Enqueue(TaskDialogResult.No);
            Equal(Result.Cancelled, f.Execute()); f.NoOutput(); Equal(0, CommonDialog.Shown.Count);
        });
        Test("writer validation error creates no partial file or success message", f =>
        {
            var template = f.Template(); template.LvCatalog = new(new("fixture", "test", ""), template.LvCatalog.Entries);
            f.Remember(template); f.ApproveAndSave();
            Equal(Result.Failed, f.Execute()); f.NoOutput(); Equal(0, TaskDialog.Notices.Count);
        });
        Test("writer IO error is reported without a partial target", f =>
        {
            f.Remember(f.Template()); TaskDialog.Answers.Enqueue(TaskDialogResult.Yes);
            CommonDialog.SaveChoices.Enqueue(Path.Combine(f.Directory, "missing-directory", "takeoff.x31"));
            Equal(Result.Failed, f.Execute()); Check(f.Message.Length > 0, "Missing IO error"); f.NoOutput(); Equal(0, TaskDialog.Notices.Count);
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
            Equal(Result.Succeeded, f.Execute()); Check(File.Exists(f.Output), "Recovery did not export");
            Equal(valid, new AvaWorkspace().LastTemplatePath!);
        });
        Test("export cannot overwrite the active template or workspace settings", f =>
        {
            f.Remember(f.Template()); var templateBytes = File.ReadAllBytes(f.TemplatePath); var settingsBytes = File.ReadAllBytes(f.Settings);
            foreach (var protectedPath in new[] { f.TemplatePath, f.Settings })
            {
                TaskDialog.Answers.Enqueue(TaskDialogResult.Yes); CommonDialog.SaveChoices.Enqueue(protectedPath);
                Equal(Result.Failed, f.Execute()); Contains("überschreiben", f.Message); f.NoOutput();
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
        Test("template picker is Revit-owned and X31 picker uses Revit native dialog", f =>
        {
            File.WriteAllText(f.TemplatePath, TemplateJson.Serialize(f.Template()));
            CommonDialog.OpenChoices.Enqueue(f.TemplatePath); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute());
            Equal(2, CommonDialog.Shown.Count);
            Check(CommonDialog.Shown.Single(s => s.Dialog is OpenFileDialog).Owner == f.Data.Application.MainWindowHandle, "Template picker Revit owner missing");
            Check(CommonDialog.Shown.Single(s => s.Dialog is FileSaveDialog).Owner == IntPtr.Zero, "Expected Revit-owned native X31 picker");
            Equal(1, System.Windows.Window.Created); Equal(System.Windows.Window.Created, System.Windows.Window.Closed);
        });
        Test("native X31 picker does not depend on a synthetic WPF owner", f =>
        {
            f.Data.Application.MainWindowHandle = IntPtr.Zero; f.Remember(f.Template()); f.ApproveAndSave();
            Equal(Result.Succeeded, f.Execute()); Equal(IntPtr.Zero, CommonDialog.Shown.Single().Owner);
            Equal(0, System.Windows.Window.Created);
        });
        Test("screenshot-oriented export labels Revit ID in the remark and preserves compact row ID", f =>
        {
            f.Remember(f.Template()); f.ApproveAndSave(); Equal(Result.Succeeded, f.Execute());
            var rows = XDocument.Load(f.Output).Descendants(Ns + "QTakeoff").Select(e => e.Attribute("Row")!.Value).ToArray();
            Equal("429496", rows.Single(r => r[12] != '*').Substring(13, 9).Trim());
            Check(rows.Any(r => r[12] == '*' && r.Substring(13, 56).TrimEnd() == "Revit-ElementID: 429496"), "Labeled Revit element remark missing");
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
            TaskDialog.Reset(); CommonDialog.Reset(); AvaGroupingDialog.Reset(); BIMassist.Ava.Views.ParameterChoiceWindow.Reset();
            System.Windows.Window.Created = System.Windows.Window.Closed = 0;
            var symbol = Type;
            symbol.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, "001", StorageType.String);
            symbol.BuiltIns[BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Typ", SpecTypeId.String.Text, "02", StorageType.String);
            Document.Elements.AddRange([Instance, symbol]); Data.Application.ActiveUIDocument = new(Document);
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
