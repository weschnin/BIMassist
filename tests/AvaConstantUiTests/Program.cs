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
