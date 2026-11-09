using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var t = new AvaTemplate { ReviewScope = "Technical mixed-unit regression fixture" };
            t.BgkCatalog.Entries.Add(new("01-10", "BGK", "", [new("100", "Type")]));
            t.LvCatalog.Entries.AddRange([
                new("piece", "b", ["01"], "01", "Piece", "", "St"),
                new("GW_8862428", "b", ["01", "20", "0210"], "01.20.0210", "Container vorhalten", "", "psch"),
                new("month", "b", ["03"], "03", "Month", "", "Mt"),
                new("custom", "b", ["04"], "04", "Custom", "", "Gerät·Monat")]);
            var saved = new MappingPosition { LvItemId = "piece", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 1, Unit = "St" } };
            var rule = new MappingRule { BgkKey = "01-10", TypeNumber = "100", Positions = [saved] }; t.Mappings.Add(rule);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var untouched = new PositionEditorWindow(t, null);
                Field<ListBox>(untouched, "lv").SelectedItem = t.LvCatalog.Entries[0];
                Field<CheckBox>(untouched, "hasQuantity").IsChecked = true;
                WithRealizedQuantity(untouched, inner => {
                    Check(inner.Text == "St", "Realized quantity editor did not synchronize its default unit");
                    Field<ListBox>(untouched, "lv").SelectedItem = t.LvCatalog.Entries[1];
                    DrainDispatcher(untouched);
                    var quantity = untouched.BuildPosition().Quantity;
                    Check(quantity is { Unit: "psch", ConstantValue: 1 }, $"Untouched realized default should follow LV St→psch; got {quantity?.Unit}");
                });
            }
            Console.WriteLine("PASS untouched realized quantity default follows LV St→psch (twice)");
            var realizedTyped = new PositionEditorWindow(t, null);
            Field<ListBox>(realizedTyped, "lv").SelectedItem = t.LvCatalog.Entries[0];
            Field<CheckBox>(realizedTyped, "hasQuantity").IsChecked = true;
            WithRealizedQuantity(realizedTyped, inner => {
                Field<TextBlock>(realizedTyped, "preview").Text = "stale preview";
                inner.Focus(); inner.SelectAll();
                inner.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, inner, "Leistungsmonat")) { RoutedEvent = TextCompositionManager.TextInputEvent });
                DrainDispatcher(realizedTyped);
                Check(Field<TextBlock>(realizedTyped, "preview").Text == "", "Typing in the realized unit TextBox did not clear preview");
                Check(realizedTyped.BuildPosition().Quantity!.Unit == "Leistungsmonat", "Realized unit typing did not reach the quantity");
                SelectKind(realizedTyped, "Formula"); SelectKind(realizedTyped, "Constant");
                Field<ListBox>(realizedTyped, "lv").SelectedItem = t.LvCatalog.Entries[1];
                var tabs = ((DockPanel)realizedTyped.Content).Children.OfType<TabControl>().Single();
                tabs.SelectedIndex = 0; DrainDispatcher(realizedTyped);
                tabs.SelectedIndex = 1; DrainDispatcher(realizedTyped);
                Check(realizedTyped.BuildPosition().Quantity is { Unit: "Leistungsmonat", ConstantValue: 1 }, "Realized typed unit was relabelled by mode/LV/tab browsing");
            });
            foreach (bool useFormula in new[] { false, true })
            {
                var entered = new PositionEditorWindow(t, null);
                Field<ListBox>(entered, "lv").SelectedItem = t.LvCatalog.Entries[0];
                Field<CheckBox>(entered, "hasQuantity").IsChecked = true;
                WithRealizedQuantity(entered, _ => {
                    if (useFormula) SelectKind(entered, "Formula");
                    Field<TextBlock>(entered, "preview").Text = "stale preview";
                    Field<TextBox>(entered, useFormula ? "expression" : "constantValue").Text = useFormula ? "2 * 3" : "2";
                    Check(Field<TextBlock>(entered, "preview").Text == "", "Numeric/formula edit did not clear preview");
                    Field<ListBox>(entered, "lv").SelectedItem = t.LvCatalog.Entries[1];
                    SelectKind(entered, useFormula ? "Constant" : "Formula");
                    SelectKind(entered, useFormula ? "Formula" : "Constant"); DrainDispatcher(entered);
                    var value = entered.BuildPosition().Quantity!;
                    Check(value.Unit == "St" && (useFormula ? value.Expression == "2 * 3" : value.ConstantValue == 2), "Numeric/formula edit did not preserve its entered value and declared default unit");
                });
            }
            var realizedSaved = new PositionEditorWindow(t, saved);
            WithRealizedQuantity(realizedSaved, _ => {
                Field<ListBox>(realizedSaved, "lv").SelectedItem = t.LvCatalog.Entries[1];
                SelectKind(realizedSaved, "Formula"); SelectKind(realizedSaved, "Constant"); DrainDispatcher(realizedSaved);
                Check(realizedSaved.BuildPosition().Quantity is { Unit: "St", ConstantValue: 1 }, "Realized saved quantity was relabelled");
            });
            Console.WriteLine("PASS realized inner-TextBox edits, preview invalidation, mode/LV/tab browsing, numeric/formula values and saved units");
            var edit = new PositionEditorWindow(t, saved);
            Check(Field<ComboBox>(edit, "quantityUnit").IsEditable, "Quantity units are still restricted to a fixed picker");
            Field<ListBox>(edit, "lv").SelectedItem = t.LvCatalog.Entries[1];
            Check(edit.BuildPosition().Quantity is { Unit: "St", ConstantValue: 1 }, "Changing the LV relabelled the saved quantity");
            var accepted = Accept(edit);
            Check(accepted.Id == saved.Id && accepted.LvItemId == "GW_8862428", "Screenshot edit was not accepted with its original position identity");
            var group = new MappingEditorWindow(t, rule); group.UpsertPosition(accepted);
            var month = new PositionEditorWindow(t, null);
            Field<ListBox>(month, "lv").SelectedItem = t.LvCatalog.Entries[2];
            Field<CheckBox>(month, "hasQuantity").IsChecked = true;
            Check(month.BuildPosition().Quantity!.Unit == "Mt", "New LV unit Mt was dropped");
            Field<TextBox>(month, "constantValue").Text = "2";
            group.UpsertPosition(Accept(month));
            var formula = new PositionEditorWindow(t, null);
            Field<ListBox>(formula, "lv").SelectedItem = t.LvCatalog.Entries[3];
            Field<CheckBox>(formula, "hasQuantity").IsChecked = true;
            SelectKind(formula, "Formula");
            Field<TextBox>(formula, "expression").Text = "Length * 2";
            formula.UpsertVariable(new() { Name = "Length", Source = new() { Kind = ParameterKind.LocalFamily, Name = "Length", Spec = "LENGTH", Scope = ParameterScope.Instance }, Unit = "m" });
            Field<ComboBox>(formula, "quantityUnit").Text = "Leistungsmonat";
            Check(formula.BuildPosition().Quantity!.Unit == "Leistungsmonat", "Typed independent formula unit was lost");
            Check(formula.EvaluatePreview(new Dictionary<string, decimal> { ["Length"] = 3 }) == 6, "Independent formula preview failed");
            group.UpsertPosition(Accept(formula));
            var parameter = new MappingPosition { LvItemId = "month", Quantity = new() { Source = new() { Kind = ParameterKind.LocalFamily, Name = "Count", Spec = "INTEGER", Scope = ParameterScope.Type }, Unit = "St" } };
            group.UpsertPosition(Accept(new PositionEditorWindow(t, parameter)));
            var typed = new PositionEditorWindow(t, null);
            Field<ListBox>(typed, "lv").SelectedItem = t.LvCatalog.Entries[3]; Field<CheckBox>(typed, "hasQuantity").IsChecked = true;
            var units = Field<ComboBox>(typed, "quantityUnit"); units.Text = "freie Einheit";
            SelectKind(typed, "Formula"); SelectKind(typed, "Constant");
            Check(typed.BuildPosition().Quantity!.Unit == "freie Einheit", "Typed unit was lost when quantity mode changed");
            group.UpsertPosition(Accept(typed));
            t.Mappings = [group.BuildRule()]; group.Close(); TemplateEditor.EnsureValid(t);
            Check(t.Mappings[0].Positions.Count == 5 && rule.Positions.Count == 1, "Independent edits changed the original group or lost children");
            Check(t.Mappings[0].Positions[0].Quantity is { ConstantValue: 1, Unit: "St" } && t.Mappings[0].Positions[1].Quantity is { ConstantValue: 2, Unit: "Mt" }, "Sibling quantities were coupled");
            Check(TemplateValidator.Validate(t).Where(d => d.Code == "LV_UNIT").All(d => d.Severity == DiagnosticSeverity.Warning), "LV units still block saving");
            foreach (var item in t.Mappings[0].Positions)
            {
                var reopened = new PositionEditorWindow(t, item); var restored = reopened.BuildPosition(); reopened.Close();
                Check(TemplateJson.Serialize(t) == TemplateJson.Serialize(TemplateJson.Copy(t)), "Copy changed independent template");
                Check(restored.Quantity!.Unit == item.Quantity!.Unit && restored.Quantity.Expression == item.Quantity.Expression && restored.Quantity.ConstantValue == item.Quantity.ConstantValue, "Mixed-unit edit roundtrip changed a quantity");
            }
            var path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "ava-independent-ui-" + Guid.NewGuid() + ".json");
            try { TemplateJson.Save(path, t); Check(TemplateJson.Serialize(TemplateJson.Load(path)) == TemplateJson.Serialize(t), "Mixed-unit file roundtrip changed data"); }
            finally { if (File.Exists(path)) File.Delete(path); }
            if (args.Length > 0)
            {
                var actual = TemplateJson.Load(args[0]); var before = TemplateJson.Serialize(actual);
                var actualLv = actual.LvCatalog.Entries.Single(l => l.Id == "GW_8862428");
                var child = new PositionEditorWindow(actual, actual.Mappings[0].Positions[0]);
                Field<ListBox>(child, "lv").SelectedItem = actualLv;
                var newChild = Accept(child);
                var actualGroup = new MappingEditorWindow(actual, actual.Mappings[0]); actualGroup.UpsertPosition(newChild);
                var updated = actualGroup.BuildRule(); actualGroup.Close();
                Check(updated.Positions[0].Quantity is { ConstantValue: 1, Unit: "St" } && actualLv.Unit == "psch", "Actual catalog screenshot pair was changed instead of accepted");
                Check(TemplateJson.Serialize(actual) == before, "Dialog changed supplied template without acceptance at parent level");
                var copy = TemplateJson.Copy(actual); copy.Mappings[0] = updated; TemplateEditor.EnsureValid(copy);
                Console.WriteLine("PASS actual supplied LV GW_8862428 with independent saved 1 St quantity");
            }
            Console.WriteLine("PASS modal acceptance of saved St→psch edit, mixed-unit siblings, free constant/formula units, parameter reads and file/edit roundtrip"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void WithRealizedQuantity(PositionEditorWindow window, Action<TextBox> check)
    {
        window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        try
        {
            window.Show(); DrainDispatcher(window);
            var tabs = ((DockPanel)window.Content).Children.OfType<TabControl>().Single();
            tabs.SelectedIndex = 1; DrainDispatcher(window);
            var units = Field<ComboBox>(window, "quantityUnit");
            var inner = units.Template.FindName("PART_EditableTextBox", units) as TextBox;
            Check(inner is { IsLoaded: true }, "Quantity tab did not realize its editable unit TextBox");
            check(inner!);
        }
        finally { window.Close(); }
    }
    private static void DrainDispatcher(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
    private static MappingPosition Accept(PositionEditorWindow window)
    {
        // Fail before opening a modal dialog if validation would produce an unexpected message box.
        try { typeof(PositionEditorWindow).GetMethod("EnsurePositionValid", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window.BuildPosition()]); }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
        window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(() => {
            var root = (DockPanel)window.Content;
            root.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(b => (string)b.Content == "Position übernehmen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Check(window.ShowDialog() == true && window.Result != null, "Actual position acceptance button did not return a result");
        return window.Result!;
    }
    private static void SelectKind(object window, string name) { var c = Field<ComboBox>(window, "quantityKind"); c.SelectedItem = c.Items.Cast<ComboBoxItem>().Single(i => i.Tag.ToString() == name); }
    private static T Field<T>(object o, string name) => (T)(o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o) ?? throw new Exception("Missing UI field: " + name));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
