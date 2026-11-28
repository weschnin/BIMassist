using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Commands;
using BIMassist.Core;

internal static class WorkflowTests
{
    internal static void Run()
    {
        var target = new Document(); target.Symbols.Add(new FamilySymbol());
        var reference = new Document(); reference.Symbols.Add(new FamilySymbol { Bgk = new Parameter { Value = "09-25" } });
        var other = new Document();
        var app = new UIApplication { ActiveUIDocument = new(target) };
        app.Application.Documents.AddRange([target, reference, other]);
        TaskDialog.Reset();
        TaskDialog.OnShow = dialog => { if (dialog.Title == "BGK aus Referenzprojekt übernehmen") app.ActiveUIDocument = new(other); };
        string message = "";
        var result = BgkReferenceImportWorkflow.Run(app, target, new(), ref message, (_, _) => reference);
        if (result != Result.Failed || target.Symbols[0].Bgk!.SetCount != 0 || other.LastTransaction != null)
            throw new Exception("A project switch during confirmation must prevent writes to both projects");
        Console.WriteLine("PASS BGK import rechecks the bound target after confirmation");
        TaskDialog.Reset(); app.ActiveUIDocument = new(target); reference.IsValidObject = false;
        result = BgkReferenceImportWorkflow.Run(app, target, new(), ref message, (_, _) => reference);
        if (result != Result.Failed || target.Symbols[0].Bgk!.SetCount != 0)
            throw new Exception("A closed reference selected by a stale picker must be rejected before collection/write");
        Console.WriteLine("PASS BGK import rejects a closed reference");
        reference.IsValidObject = true;
        TaskDialog.Reset(); TaskDialog.Confirmation = TaskDialogResult.No;
        result = BgkReferenceImportWorkflow.Run(app, target, new(), ref message, (_, _) => reference);
        if (result != Result.Cancelled || target.LastTransaction != null) throw new Exception("Declined confirmation must not start a transaction");
        TaskDialog.Reset();
        result = BgkReferenceImportWorkflow.Run(app, target, new(), ref message, (_, _) => null);
        if (result != Result.Cancelled || target.LastTransaction != null) throw new Exception("Cancelled picker must not start a transaction");
        Console.WriteLine("PASS BGK import confirmation/picker cancellation is model-read-only");

        TaskDialog.Reset();
        bool defaultNo = false;
        TaskDialog.OnShow = dialog => { if (dialog.Title == "BGK aus Referenzprojekt übernehmen") defaultNo = dialog.DefaultButton == TaskDialogResult.No; };
        result = BgkReferenceImportWorkflow.Run(app, target, new(), ref message, (_, choices) =>
        {
            if (choices.Contains(target)) throw new Exception("The target project was offered as reference");
            return reference;
        });
        if (result != Result.Succeeded || !defaultNo || target.Symbols[0].Bgk!.Value != "09-25"
            || target.Symbols[0].Other.SetCount != 0 || reference.Symbols[0].Bgk!.SetCount != 0
            || !TaskDialog.Shown.Contains("BGK-Übernahme abgeschlossen"))
            throw new Exception("Confirmed exact-match import must write only target ASSEMBLY_CODE and show completion after readback");
        Console.WriteLine("PASS BGK import default-No confirmation, exact import, source preservation and ASSEMBLY_CODE-only writes");

        var wrapperTarget = new Document(); wrapperTarget.Symbols.Add(new FamilySymbol());
        var targetAlias = new Document { Identity = wrapperTarget.Identity };
        var wrapperReference = new Document();
        wrapperReference.Symbols.Add(new FamilySymbol { Bgk = new Parameter { Value = "09-25" } });
        var wrapperApp = new UIApplication { ActiveUIDocument = new(wrapperTarget) };
        wrapperApp.Application.Documents.AddRange([targetAlias, wrapperReference]);
        TaskDialog.Reset();
        result = BgkReferenceImportWorkflow.Run(wrapperApp, wrapperTarget, new(), ref message, (_, choices) =>
        {
            if (choices.Any(choice => choice.Equals(wrapperTarget)))
                throw new Exception("A separate managed wrapper for the active target must not appear as a reference");
            return wrapperReference;
        });
        if (result != Result.Succeeded || wrapperTarget.Symbols[0].Bgk!.Value != "09-25")
            throw new Exception("Equivalent target wrappers must be excluded while valid references remain importable");
        Console.WriteLine("PASS BGK reference list excludes equivalent active-document wrappers");

        TaskDialog.Reset(); target.CorruptAfterCommit = true;
        result = BgkReferenceImportWorkflow.Run(app, target, new(), ref message, (_, _) => reference);
        if (result != Result.Failed || TaskDialog.Shown.Contains("BGK-Übernahme abgeschlossen")) throw new Exception("Failed readback must never show completion");
        Console.WriteLine("PASS BGK import with failed readback never shows success");
    }
}