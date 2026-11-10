using System.IO;
using System.Reflection;
using System.Windows.Controls;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var template = new AvaTemplate { ReviewScope = "Losflansche" };
            template.BgkCatalog.Entries.Add(new("44-40", "Flansche", "", [new("110", "Losflansch Typ 02")]));
            template.LvCatalog.Entries.Add(new("test-id", "test-boq", ["44", "40", "0420"], "44.40.0420", "Test DN100", "", "St"));
            var position = new MappingPosition { LvItemId = "test-id", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Count", Spec = "INTEGER", Scope = ParameterScope.Instance }, Unit = "St" } };
            var group = new MappingRule { BgkKey = "44-40", TypeNumber = "110", Positions = [position] }; template.Mappings.Add(group);
            var window = new PositionEditorWindow(template, position);
            var mode = Field<ComboBox>(window, "quantityKind"); var value = Field<TextBox>(window, "constantValue");
            mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().Single(i => i.Tag.ToString() == "Constant"); value.Text = "1";
            var result = window.BuildPosition();
            Check(result.Quantity is { Kind: QuantityKind.Constant, Source: null, ConstantValue: 1, Unit: "St" }, "Constant number/unit/source changed");
            Check(!Field<ParameterSourceEditor>(window, "quantity").IsEnabled, "Parameter input enabled in constant mode");
            group.Positions = [result]; TemplateEditor.EnsureValid(template);
            value.Text = "not a number";
            try { window.BuildPosition(); throw new Exception("Invalid constant accepted"); } catch (InvalidDataException) { }
            value.Text = "1,5"; Check(window.BuildPosition().Quantity!.ConstantValue == 1.5m, "German decimal parsing failed");
            mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().Single(i => i.Tag.ToString() == "Parameter");
            var parameter = window.BuildPosition();
            Check(parameter.Quantity!.Source!.Name == "Count" && parameter.Quantity.ConstantValue == null, "Mode switch lost parameter or retained constant"); window.Close();
            var reopened = new PositionEditorWindow(template, result);
            Check(TemplateJson.Copy(reopened.BuildPosition()).Quantity is { Source: null, ConstantValue: 1 }, "Constant edit roundtrip failed"); reopened.Close();
            var lump = new LvEntry("lump-id", "test-boq", ["01", "10", "0120"], "01.10.0120", "Baustelleneinrichtung abbauen", "", "psch");
            template.LvCatalog.Entries.Add(lump);
            var added = new PositionEditorWindow(template, null);
            added.SelectLvEntry(lump.Id);
            Field<CheckBox>(added, "hasQuantity").IsChecked = true;
            var lumpPosition = added.BuildPosition();
            Check(lumpPosition.Quantity is { Kind: QuantityKind.Constant, ConstantValue: 1, Unit: "psch" }, "New psch LV position incorrectly keeps the initial St quantity unit");
            var multiple = TemplateJson.Copy(template); multiple.Mappings[0].Positions.Add(lumpPosition);
            TemplateEditor.EnsureValid(multiple);
            Check(multiple.Mappings[0].Positions[0].Quantity!.Unit == "St", "Adding psch position changed sibling quantity");
            added.SelectLvEntry(template.LvCatalog.Entries[0].Id);
            Check(added.BuildPosition().Quantity!.Unit == "St", "Untouched default unit does not follow the newly selected LV");
            Field<ComboBox>(added, "quantityUnit").SelectedItem = "m";
            added.SelectLvEntry(lump.Id);
            var explicitUnit = added.BuildPosition();
            Check(explicitUnit.Quantity!.Unit == "m", "LV selection silently relabels an explicitly chosen quantity unit");
            multiple.Mappings[0].Positions[1] = explicitUnit;
            Check(TemplateValidator.Validate(multiple).Any(d => d.Code == "LV_UNIT" && d.Severity == DiagnosticSeverity.Warning), "Independent LV/quantity unit difference is not explained");
            TemplateEditor.EnsureValid(multiple); added.Close();
            var saved = new PositionEditorWindow(template, result);
            saved.SelectLvEntry(lump.Id);
            Check(saved.BuildPosition().Quantity!.Unit == "St", "Saved unit silently changed when selecting another LV"); saved.Close();
            var entered = new PositionEditorWindow(template, null);
            entered.SelectLvEntry(lump.Id); Field<CheckBox>(entered, "hasQuantity").IsChecked = true;
            Field<TextBox>(entered, "constantValue").Text = "2";
            entered.SelectLvEntry(template.LvCatalog.Entries[0].Id);
            Check(entered.BuildPosition().Quantity is { ConstantValue: 2, Unit: "psch" }, "Entered amount was silently relabelled after LV change"); entered.Close();
            foreach (var expectedUnit in new[] { "St", "psch", "m", "m²", "m³" })
            {
                var unitTemplate = TemplateJson.Copy(template);
                var selectedLv = new LvEntry("unit-lv", "test-boq", ["99", "01"], "99.01", "Test", "", expectedUnit);
                unitTemplate.LvCatalog.Entries.Add(selectedLv);
                var fresh = new PositionEditorWindow(unitTemplate, null);
                Check(Field<ComboBox>(fresh, "quantityUnit").SelectedItem == null, "New position assumes a unit before LV selection");
                fresh.SelectLvEntry(selectedLv.Id);
                Field<CheckBox>(fresh, "hasQuantity").IsChecked = true;
                Check(fresh.BuildPosition().Quantity!.Unit == expectedUnit, "New position does not propose LV unit " + expectedUnit);
                unitTemplate.Mappings[0].Positions.Add(fresh.BuildPosition()); TemplateEditor.EnsureValid(unitTemplate); fresh.Close();
            }
            Console.WriteLine("PASS LV unit defaults, psch child addition, browsing, explicit/saved/entered-unit preservation and mismatch validation");
            foreach (var unit in new[] { "m²", "m³" })
            {
                var aliasTemplate = TemplateJson.Copy(template);
                aliasTemplate.LvCatalog.Entries[0] = aliasTemplate.LvCatalog.Entries[0] with { Unit = unit };
                var aliasPosition = new MappingPosition { LvItemId = "test-id", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = unit } };
                var aliasWindow = new PositionEditorWindow(aliasTemplate, aliasPosition);
                Check(aliasWindow.BuildPosition().Quantity!.Unit == unit, "Accepted unit alias lost in UI"); aliasWindow.Close();
            }
            if (args.Length > 0)
            {
                var delivered = TemplateJson.Load(args[0]);
                foreach (var mapping in delivered.Mappings)
                {
                    var dialog = new MappingEditorWindow(delivered, mapping); Layout(dialog);
                    var copy = dialog.BuildRule();
                    Check(copy.Id == mapping.Id && copy.Conditions.Single().Number == mapping.Conditions.Single().Number, "Migrated group ID/DN changed");
                    var item = copy.Positions.Single();
                    Check(item.LvItemId == mapping.Positions.Single().LvItemId && item.Quantity is { Kind: QuantityKind.Constant, Source: null, ConstantValue: 1, Unit: "St" }, "Delivered position changed in group UI");
                    var positionWindow = new PositionEditorWindow(delivered, item); Layout(positionWindow);
                    Check(positionWindow.BuildPosition().Quantity!.ConstantValue == 1, "Delivered constant changed in position UI");
                    positionWindow.Close(); dialog.Close();
                }
                Console.WriteLine("PASS all " + delivered.Mappings.Count + " delivered DN groups and positions through WPF layout/edit roundtrip");
            }
            Console.WriteLine("PASS constant quantity WPF mode switch, parsing, parameter preservation, aliases and roundtrip"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Layout(System.Windows.Window window) { window.Measure(new System.Windows.Size(1140, 850)); window.Arrange(new System.Windows.Rect(0, 0, 1140, 850)); window.UpdateLayout(); }
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target) ?? throw new Exception("Missing UI field: " + name));
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
}
