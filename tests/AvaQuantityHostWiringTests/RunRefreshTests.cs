using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class RunRefreshTests
{
    internal static void Run(string hostSource, string handlerSource, string root)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        var host = CSharpSyntaxTree.ParseText(hostSource).GetRoot();
        var handler = CSharpSyntaxTree.ParseText(handlerSource).GetRoot();
        static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxNode node, string name) =>
            node.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
                call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == name
                || call.Expression is MemberBindingExpressionSyntax binding && binding.Name.Identifier.ValueText == name
                || call.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == name);
        static bool Assigns(AssignmentExpressionSyntax assignment, string field) =>
            assignment.Left is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == field;

        // Find the same-document branch and the actual run/ID state writer, not a private signature or formatting.
        var refresh = host.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(branch => Calls(branch.Condition, "IsForDocument").Any()).Statement;
        var display = Calls(refresh, "SetRun").Single();
        var stateWriters = handler.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => Assigns(a, "quantityRun"))
                && method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => Assigns(a, "includedIds"))).ToArray();
        Check(stateWriters.Length == 1,
            "A shared run/ID synchronization method is required before refreshing the same-document window (Undo A -> B -> A).");
        var update = stateWriters.Single();
        var sync = Calls(refresh, update.Identifier.ValueText).SingleOrDefault();
        Check(sync != null && sync.SpanStart < display.SpanStart
            && sync.ArgumentList.Arguments.Single().ToString() == display.ArgumentList.Arguments.Single().ToString(),
            "The same-document handler must receive the displayed run before SetRun, so X31/X82 and missing IDs cannot retain B after Undo to A.");
        Check(Calls(refresh, "Activate").Single().SpanStart > display.SpanStart,
            "The refreshed window must only activate after synchronizing and displaying the run.");
        var callback = handler.DescendantNodes().OfType<LambdaExpressionSyntax>()
            .Single(lambda => Calls(lambda, "SetRun").Any());
        var recalculateSync = Calls(callback, update.Identifier.ValueText).SingleOrDefault();
        var recalculateDisplay = Calls(callback, "SetRun").Single();
        Check(recalculateSync != null && recalculateSync.SpanStart < recalculateDisplay.SpanStart
            && recalculateSync.ArgumentList.Arguments.Single().ToString() == recalculateDisplay.ArgumentList.Arguments.Single().ToString(),
            "Recalculation must use the same run/ID synchronization before displaying its result.");
        Check(handler.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => Assigns(a, "quantityRun") || Assigns(a, "includedIds"))
            .All(a => a.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == update),
            "Run and included-ID replacements must not be duplicated in callbacks.");
        Check(!Calls(update, "CancelRequest").Any()
            && !update.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString().Contains("request", StringComparison.OrdinalIgnoreCase)
                    || a.Left.ToString().StartsWith("pending", StringComparison.OrdinalIgnoreCase)),
            "Refreshing snapshot state must not reset queued requests or pending isolation lifecycle state.");
        Console.WriteLine("PASS same-document and recalculation synchronize shared run/ID state before display (syntax wiring; not live Revit)");

        // Execute the unmodified production method in a dependency-free shell. This tests
        // the state transition itself, not a fabricated Document/ExternalEvent runtime.
        var probeSource = """
            using System;
            using System.IO;
            using System.Globalization;
            using System.Collections.Generic;
            using System.Linq;
            using BIMassist.Ava.Core;
            public sealed class RunStateProbe
            {
                private AvaQuantityRun? quantityRun;
                private IReadOnlySet<long> includedIds = new HashSet<long>();
                public AvaQuantityRun? Run => quantityRun;
                public IReadOnlySet<long> Ids => includedIds;
            """ + update.ToFullString() + "\n}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("RunRefreshProbe",
            [CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq;"),
             CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Ava/Core/AvaQuantityRun.cs"))),
             CSharpSyntaxTree.ParseText(probeSource)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var bytes = new MemoryStream();
        var emitted = compilation.Emit(bytes);
        Check(emitted.Success, "Could not compile the production state-update method: " + string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(bytes.ToArray());
        var probeType = assembly.GetType("RunStateProbe")!;
        var recordType = assembly.GetType("BIMassist.Ava.Core.AvaQuantityRecord")!;
        var runType = assembly.GetType("BIMassist.Ava.Core.AvaQuantityRun")!;
        object MakeRun(params (string Id, decimal Quantity)[] rows)
        {
            var records = Array.CreateInstance(recordType, rows.Length);
            for (var i = 0; i < rows.Length; i++)
                records.SetValue(Activator.CreateInstance(recordType, Guid.NewGuid(), "B0000001", "item", "0010", "Menge", "m",
                    rows[i].Id, "Length", "3.123456789", "", "", "Family", "Type", rows[i].Quantity), i);
            return Activator.CreateInstance(runType, 1, Guid.NewGuid(), DateTimeOffset.UtcNow, "Project", "", "View", "{}", records)!;
        }
        var probe = Activator.CreateInstance(probeType)!;
        var apply = probeType.GetMethod(update.Identifier.ValueText, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var runProperty = probeType.GetProperty("Run")!;
        var idsProperty = probeType.GetProperty("Ids")!;
        var a = MakeRun(("00012", 3.123456789m), ("12", 0.000000001m), ("9007199254740993", 7m));
        var b = MakeRun(("27", 9.987654321m));
        foreach (var expected in new[] { a, b, a })
        {
            apply.Invoke(probe, [expected]);
            Check(ReferenceEquals(runProperty.GetValue(probe), expected), "A -> B -> A must retain the exact full-precision run instance for exports.");
            var ids = (IReadOnlySet<long>)idsProperty.GetValue(probe)!;
            Check(ids.SetEquals(ReferenceEquals(expected, a) ? [12L, 9007199254740993L] : [27L]),
                "A -> B -> A must replace (not accumulate) distinct full-width IDs for missing-element actions.");
        }
        var previousIds = idsProperty.GetValue(probe);
        foreach (var badId in new[] { "not-an-id", "9223372036854775808" })
        {
            try
            {
                apply.Invoke(probe, [MakeRun(("99", 1m), (badId, 2m))]);
                throw new Exception("Malformed/overflow IDs must reject the refresh.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException or FormatException or OverflowException) { }
            Check(ReferenceEquals(runProperty.GetValue(probe), a) && ReferenceEquals(idsProperty.GetValue(probe), previousIds),
                "All IDs must be validated before replacing either state member; a late invalid ID must leave the previous run and IDs intact.");
        }
        var firstRecord = ((Array)runType.GetProperty("Records")!.GetValue(a)!).GetValue(0)!;
        Check((decimal)recordType.GetProperty("Result")!.GetValue(firstRecord)! == 3.123456789m,
            "State refresh must not round stored quantities to display precision.");
        Console.WriteLine("PASS extracted production run update: A -> B -> A, distinct 64-bit IDs, full precision, and invalid-ID atomic rejection (no Revit runtime)");
    }
}
