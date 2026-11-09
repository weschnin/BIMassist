using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Static wiring tests: inspect actual dialog initializers, including aliases, then
// bind their literal settings against the real WPF/WinForms API. No Revit or UI run.
internal static class Program
{
    private static readonly string Root = FindRoot();
    private static readonly MetadataReference[] References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("BGK OpenData selects XML first with explicit open settings", BgkOpen),
            ("BGK SaveData selects XML first with explicit save settings", BgkSave),
            ("Borehole hole CSV picker", () => BoreholeCsv("ImportHoles", 1)),
            ("Borehole geology CSV picker", () => BoreholeCsv("ImportGeology", 1)),
            ("Borehole paired CSV pickers preserve multiselect", () => BoreholeCsv("ImportPair", 2)),
            ("Surface family RFT picker", () => FamilyTemplates("Services/SurfaceEnclosureFamilyBuilder.cs", 1)),
            ("Void family RFT picker", () => FamilyTemplates("Services/VoidGeometryFamilyBuilder.cs", 1)),
            ("Core geometry RFT pickers", () => FamilyTemplates("Core/FamilyGeometryTools.cs", 2)),
            ("AVA takeoff opens only JSON templates plus All Files", TakeoffOpen),
            ("AVA takeoff saves only X31 plus All Files", TakeoffSave),
        };
        int failed = 0;
        foreach (var test in tests)
        {
            try { test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} static dialog wiring tests passed");
        return failed == 0 ? 0 : 1;
    }

    private static void TakeoffOpen()
    {
        var dialog = Dialogs("Commands/AvaQuantityTakeoffCommand.cs", "Execute")
            .Single(d => d.TypeName == "Microsoft.Win32.OpenFileDialog");
        ExpectFilter(dialog, ".json");
        Expect(dialog, "FilterIndex", 1);
        Expect(dialog, "CheckFileExists", true);
        Expect(dialog, "Multiselect", false);
    }

    private static void TakeoffSave()
    {
        var dialog = Dialogs("Commands/AvaQuantityTakeoffCommand.cs", "Execute")
            .Single(d => d.TypeName == "Autodesk.Revit.UI.FileSaveDialog");
        string filter = (string)((LiteralExpressionSyntax)dialog.Creation.ArgumentList!.Arguments.Single().Expression).Token.Value!;
        string[] parts = filter.Split('|');
        Check(parts.Length == 4 && parts[1] == "*.x31" && parts[3] == "*.*"
            && parts[0].Contains("*.x31", StringComparison.Ordinal), "Native Revit picker must offer X31 and All Files");
        Expect(dialog, "DefaultFilterEntry", "*.x31");
        Expect(dialog, "InitialFileName", "Mengenermittlung.x31");
    }

    private static void BgkOpen()
    {
        var dialog = Dialogs("ViewModels/MainViewModel.cs", "OpenData").Single();
        Expect(dialog, "FilterIndex", 1); // Regression: formerly 2 although there was only one filter.
        ExpectFilter(dialog, ".xml");
        Expect(dialog, "CheckFileExists", true);
        Expect(dialog, "Multiselect", false);
    }

    private static void BgkSave()
    {
        var dialog = Dialogs("ViewModels/MainViewModel.cs", "SaveData").Single();
        Expect(dialog, "FilterIndex", 1);
        ExpectFilter(dialog, ".xml");
        Expect(dialog, "AddExtension", true);
        Expect(dialog, "OverwritePrompt", true);
        Expect(dialog, "RestoreDirectory", true);
    }

    private static void BoreholeCsv(string method, int expectedCount)
    {
        var dialogs = Dialogs("ViewModels/BoreholeManagerViewModel.cs", method);
        Check(dialogs.Length == expectedCount, method + ": incorrect CSV picker count");
        foreach (var dialog in dialogs)
        {
            ExpectFilter(dialog, ".csv");
            Expect(dialog, "FilterIndex", 1);
            Expect(dialog, "CheckFileExists", true);
            if (method == "ImportPair") Expect(dialog, "Multiselect", true);
        }
    }

    private static void FamilyTemplates(string relativePath, int expectedCount)
    {
        var dialogs = Dialogs(relativePath);
        Check(dialogs.Length == expectedCount, relativePath + ": incorrect RFT picker count");
        foreach (var dialog in dialogs)
        {
            ExpectFilter(dialog, ".rft");
            Expect(dialog, "FilterIndex", 1);
            Expect(dialog, "CheckFileExists", true);
        }
    }

    private static void ExpectFilter(Dialog dialog, string extension)
    {
        string filter = (string)Value(dialog, "Filter");
        string[] parts = filter.Split('|');
        Check(parts.Length == 4 && parts.All(part => !string.IsNullOrWhiteSpace(part)),
            dialog.Label + ": expected primary type plus all-files fallback, got " + filter);
        Check(parts[1] == "*" + extension && parts[3] == "*.*",
            dialog.Label + ": incorrect primary/fallback patterns: " + filter);
        Check(parts[0].Contains("*" + extension, StringComparison.Ordinal),
            dialog.Label + ": primary label must show extension");
        Expect(dialog, "DefaultExt", extension);
    }

    private static void Expect(Dialog dialog, string property, object expected)
    {
        object actual = Value(dialog, property);
        Check(Equals(actual, expected), $"{dialog.Label}: {property} expected {expected}, got {actual}");
    }

    private static object Value(Dialog dialog, string property)
    {
        var assignment = dialog.Creation.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(item => item.Left.ToString() == property);
        var setting = assignment ?? throw new InvalidOperationException(dialog.Label + ": missing explicit " + property);
        // Compile only the setting being asserted, not business logic referencing
        // Revit state. Binding catches wrong API names/types, not just text matches.
        string createExpression = dialog.TypeName == "Autodesk.Revit.UI.FileSaveDialog"
            ? setting.Right.ToString()
            : $"new {dialog.TypeName}{dialog.Creation.ArgumentList} {{ {setting} }}";
        var tree = CSharpSyntaxTree.ParseText($"class Wiring {{ object Create() => {createExpression}; }}");
        var compilation = CSharpCompilation.Create("DialogWiring", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        Check(errors.Length == 0, dialog.Label + ": invalid dialog API setting: " + string.Join("; ", errors.Select(e => e.ToString())));
        ExpressionSyntax valueExpression = dialog.TypeName == "Autodesk.Revit.UI.FileSaveDialog"
            ? tree.GetRoot().DescendantNodes().OfType<ArrowExpressionClauseSyntax>().Single().Expression
            : tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>().Single().Right;
        var constant = compilation.GetSemanticModel(tree).GetConstantValue(valueExpression);
        Check(constant.HasValue && constant.Value != null, dialog.Label + ": expected constant " + property);
        return constant.Value!;
    }

    private static Dialog[] Dialogs(string relativePath, string? method = null)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Root, relativePath)));
        var root = tree.GetCompilationUnitRoot();
        Check(!tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), relativePath + ": syntax errors");
        var aliases = root.Usings.Where(u => u.Alias != null)
            .ToDictionary(u => u.Alias!.Name.Identifier.ValueText, u => u.Name!.ToString());
        return root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Select(creation =>
            {
                string type = creation.Type.ToString();
                if (aliases.TryGetValue(type, out string? resolved)) type = resolved;
                if (type is "OpenFileDialog" or "SaveFileDialog" or "FileSaveDialog")
                {
                    string ns = type == "FileSaveDialog" ? "Autodesk.Revit.UI"
                        : root.Usings.Select(u => u.Name?.ToString()).Single(name => name is "Microsoft.Win32" or "System.Windows.Forms")!;
                    type = ns + "." + type;
                }
                return new Dialog(relativePath + ":" + (creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1), type, creation);
            })
            .Where(dialog => dialog.TypeName is "Microsoft.Win32.OpenFileDialog" or "Microsoft.Win32.SaveFileDialog"
                or "System.Windows.Forms.OpenFileDialog" or "System.Windows.Forms.SaveFileDialog"
                or "Autodesk.Revit.UI.FileSaveDialog")
            .Where(dialog => method == null || dialog.Creation.Ancestors().OfType<MethodDeclarationSyntax>()
                .First().Identifier.ValueText == method).ToArray();
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ViewModels", "MainViewModel.cs"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate BIMassist source root from test executable");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Dialog(string Label, string TypeName, ObjectCreationExpressionSyntax Creation);
}
