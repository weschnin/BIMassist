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
            var rule = new MappingRule { BgkKey = "44-40", TypeNumber = "110", LvItemId = "test-id", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Count", Spec = "INTEGER", Scope = ParameterScope.Instance }, Unit = "St" } };
            template.Mappings.Add(rule);
            var window = new MappingEditorWindow(template, rule);
            var mode = Field<ComboBox>(window, "quantityKind");
            var value = Field<TextBox>(window, "constantValue");
            mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().Single(i => i.Tag.ToString() == "Constant");
            value.Text = "1";
            var result = window.BuildRule();
            Check(result.Quantity!.Source == null, "Constant must not carry a parameter source");
            Check(result.Quantity.GetType().GetProperty("ConstantValue")!.GetValue(result.Quantity) is decimal n && n == 1, "Constant number lost");
            Check(result.Quantity.Unit == "St", "St unit lost");
            Check(!Field<ParameterSourceEditor>(window, "quantity").IsEnabled, "Parameter input enabled in constant mode");
            Check(!TemplateValidator.Validate(new AvaTemplate { ReviewScope = template.ReviewScope, BgkCatalog = template.BgkCatalog, LvCatalog = template.LvCatalog, Mappings = [result] }).Any(x => x.Severity == DiagnosticSeverity.Error), "Constant fails validation");
            value.Text = "not a number";
            try { window.BuildRule(); throw new Exception("Invalid constant accepted"); } catch (InvalidDataException) { }
            value.Text = "1,5";
            Check(window.BuildRule().Quantity!.GetType().GetProperty("ConstantValue")!.GetValue(window.BuildRule().Quantity) is decimal fraction && fraction == 1.5m, "German decimal parsing failed");
            mode.SelectedItem = mode.Items.Cast<ComboBoxItem>().Single(i => i.Tag.ToString() == "Parameter");
            var parameter = window.BuildRule();
            Check(parameter.Quantity!.Source!.Name == "Count", "Mode switch lost original parameter");
            Check(parameter.Quantity.GetType().GetProperty("ConstantValue")!.GetValue(parameter.Quantity) == null, "Parameter mode retains constant");
            window.Close();
            var reopened = new MappingEditorWindow(template, result);
            var restored = reopened.BuildRule();
            Check(TemplateJson.Copy(restored).Quantity!.Source == null && restored.Quantity!.ToString()!.Contains("1"), "Constant edit roundtrip failed");
            reopened.Close();
            if (args.Length > 0)
            {
                var delivered = TemplateJson.Load(args[0]);
                foreach (var mapping in delivered.Mappings)
                {
                    var dialog = new MappingEditorWindow(delivered, mapping);
                    dialog.Measure(new System.Windows.Size(1140, 800));
                    dialog.Arrange(new System.Windows.Rect(0, 0, 1140, 800)); dialog.UpdateLayout();
                    var copy = dialog.BuildRule();
                    Check(copy.Id == mapping.Id && copy.LvItemId == mapping.LvItemId && copy.Quantity is { Kind: QuantityKind.Constant, Source: null, ConstantValue: 1, Unit: "St" }, "Delivered mapping changed in WPF");
                    Check(copy.Conditions.Single().Number == mapping.Conditions.Single().Number, "DN lost in WPF");
                    dialog.Close();
                }
                Console.WriteLine("PASS all " + delivered.Mappings.Count + " delivered DN mappings through WPF construction/layout/edit roundtrip");
            }
            Console.WriteLine("PASS constant quantity WPF mode switch, input parsing, parameter preservation and edit roundtrip");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target) ?? throw new Exception("Missing constant UI field: " + name));
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
}
