using Autodesk.Revit.DB;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;
using BIMassist.Ava.Views;
using System.Windows;
using System.Windows.Controls;
using System.IO;

internal static class Program
{
    private enum FakeTransactionStatus { Uninitialized, Started, Pending, Committed, RolledBack }

    [STAThread]
    private static void Main()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        var document = new Document();
        var rollbackCalls = 0;
        var pendingRecovery = X31ReviewTransactionRecovery.Resolve(
            () => FakeTransactionStatus.Pending, () => { rollbackCalls++; return FakeTransactionStatus.RolledBack; },
            FakeTransactionStatus.Started, FakeTransactionStatus.Pending,
            status => status is FakeTransactionStatus.Committed or FakeTransactionStatus.RolledBack);
        Check(!pendingRecovery.CanDispose && rollbackCalls == 0,
            "Pending failure handling must neither retry rollback nor dispose the transaction.");
        foreach (var result in new[] { FakeTransactionStatus.Pending, FakeTransactionStatus.Started })
        {
            var held = X31ReviewTransactionRecovery.Resolve(
                () => FakeTransactionStatus.Started, () => { rollbackCalls++; return result; },
                FakeTransactionStatus.Started, FakeTransactionStatus.Pending,
                status => status is FakeTransactionStatus.Committed or FakeTransactionStatus.RolledBack);
            Check(!held.CanDispose && held.Status == result,
                $"Rollback result {result} must keep the transaction for later recovery.");
        }
        var rolledBack = X31ReviewTransactionRecovery.Resolve(
            () => FakeTransactionStatus.Started, () => { rollbackCalls++; return FakeTransactionStatus.RolledBack; },
            FakeTransactionStatus.Started, FakeTransactionStatus.Pending,
            status => status is FakeTransactionStatus.Committed or FakeTransactionStatus.RolledBack);
        Check(rolledBack.CanDispose && rolledBack.Status == FakeTransactionStatus.RolledBack,
            "A terminal rollback status must permit transaction cleanup.");
        var alreadyCommitted = X31ReviewTransactionRecovery.Resolve(
            () => FakeTransactionStatus.Committed, () => throw new Exception("rollback must not run"),
            FakeTransactionStatus.Started, FakeTransactionStatus.Pending,
            status => status is FakeTransactionStatus.Committed or FakeTransactionStatus.RolledBack);
        Check(alreadyCommitted.CanDispose && alreadyCommitted.Status == FakeTransactionStatus.Committed,
            "An already committed transaction must be cleaned up without rollback.");
        var statusError = X31ReviewTransactionRecovery.Resolve<FakeTransactionStatus>(
            () => throw new InvalidOperationException("status unavailable"), () => FakeTransactionStatus.RolledBack,
            FakeTransactionStatus.Started, FakeTransactionStatus.Pending,
            status => status is FakeTransactionStatus.Committed or FakeTransactionStatus.RolledBack);
        Check(!statusError.CanDispose && statusError.Error?.Message == "status unavailable",
            "An unreadable transaction status must not permit cleanup.");
        var rollbackError = X31ReviewTransactionRecovery.Resolve(
            () => FakeTransactionStatus.Started, () => throw new InvalidOperationException("rollback unavailable"),
            FakeTransactionStatus.Started, FakeTransactionStatus.Pending,
            status => status is FakeTransactionStatus.Committed or FakeTransactionStatus.RolledBack);
        Check(!rollbackError.CanDispose && rollbackError.Error?.Message == "rollback unavailable",
            "A failed Started rollback must retain the transaction for another recovery attempt.");
        var view = new View { Id = new(500) };
        var otherView = new View { Id = new(501) };
        document.Elements.AddRange([view, otherView]);
        Element Add(long id, CategoryType category = CategoryType.Model, long categoryId = 10)
        {
            var element = new Element { Id = new(id), Category = new Category { CategoryType = category, Id = new(categoryId) } };
            element.VisibleInViews.Add(view.Id.Value);
            document.Elements.Add(element);
            return element;
        }
        Add(1); // included by one review row
        Add(2); // included by another review row, not the selected row
        Add(3); // system-family instance (wall, floor etc.) with no AVA classification
        var family = new FamilyInstance { Id = new(4) }; family.VisibleInViews.Add(view.Id.Value); document.Elements.Add(family);
        Add(5).HiddenInViews.Add(view.Id.Value);
        Add(6, categoryId: 11); view.HiddenCategoryIds.Add(11);
        Add(7, CategoryType.Annotation);
        document.Elements.Add(new ElementType { Id = new(8), VisibleInViews = { 500 } });
        document.Elements.Add(new Element { Id = new(9), VisibleInViews = { 501 } });
        var link = new RevitLinkInstance { Id = new(10) }; link.VisibleInViews.Add(500); document.Elements.Add(link);
        var linkedImport = new ImportInstance { Id = new(11), IsLinked = true }; linkedImport.VisibleInViews.Add(500); document.Elements.Add(linkedImport);
        Add(12); // temporarily hidden
        var first = new X31ReviewRow(1, "01", "a", X31ReviewRowKind.Quantity, "", "1=", "1", null, null, ["1"], false);
        var second = new X31ReviewRow(2, "02", "b", X31ReviewRowKind.Quantity, "", "1=", "2", null, null, ["2"], false);
        var review = new X31ReviewDocument("sample.x31", [first, second], []);
        var included = review.Rows.SelectMany(row => row.ElementIdsForSelection).Select(long.Parse);
        var candidates = X31ReviewViewCandidates.Find(document, view, included);
        Check(candidates.Select(id => id.Value).OrderBy(id => id).SequenceEqual([3L, 4L, 12L]),
            "Expected only missing visible model instances (including system families), excluding ALL review rows and hidden/non-model items.");
        // Crop-space bounding boxes, including a rotated view frame, use the
        // transformed corner extrema; disjoint and touching cases stay distinct.
        Check(!X31ReviewViewCandidates.OverlapsRectangle(0, 0, 10, 10, 11, 2, 12, 3),
            "A model instance entirely beyond the rectangular crop must be excluded.");
        Check(X31ReviewViewCandidates.OverlapsRectangle(0, 0, 10, 10, 9, 2, 12, 3),
            "A partially cropped model instance must be retained.");
        Check(X31ReviewViewCandidates.OverlapsRectangle(0, 0, 10, 10, 10, 2, 12, 3),
            "An instance touching the boundary must be retained conservatively.");
        Check(!X31ReviewViewCandidates.OverlapsBox(0, 0, 0, 10, 10, 10, 11, 2, 2, 12, 3, 3),
            "An instance outside the active 3D section box must be excluded.");
        Check(X31ReviewViewCandidates.OverlapsBox(0, 0, 0, 10, 10, 10, 9, 2, 2, 12, 3, 12),
            "A partly intersecting 3D instance must be retained.");
        candidates = X31ReviewViewCandidates.Find(document, view, included, element => element.Id.Value != 12);
        Check(candidates.Select(id => id.Value).OrderBy(id => id).SequenceEqual([3L, 4L]),
            "An out-of-crop model instance returned by the view collector must not be selected.");
        view.TemporaryHideIsolateActive = true;
        view.TemporaryVisibleElementIds.UnionWith([1, 2, 3, 4]);
        candidates = X31ReviewViewCandidates.Find(document, view, included);
        Check(candidates.Select(id => id.Value).OrderBy(id => id).SequenceEqual([3L, 4L]),
            "Temporary-hide/isolate-invisible instances must be excluded.");
        view.IsTemplate = true;
        try { X31ReviewViewCandidates.Find(document, view, included); throw new Exception("Template view accepted"); }
        catch (InvalidOperationException) { }
        view.IsTemplate = false;
        try { X31ReviewViewCandidates.Find(document, new View { Id = new(777) }, included); throw new Exception("Invalid view accepted"); }
        catch (InvalidOperationException) { }

        bool? missingIsolation = null;
        X31SelectionPlan? rowPlan = null;
        var window = new X31ReviewWindow(review, (plan, _) => rowPlan = plan, isolate => missingIsolation = isolate);
        try
        {
            Check(window.ReviewGrid.Columns.Select(column => column.Header?.ToString()).SequenceEqual(
                ["OZ", "Kurztext", "Zeilentyp", "Bemerkung", "Formelnummer", "Berechnungsansatz", "Ergebnis", "Einheit"]),
                "Review must show every requested quantity field in separate columns.");
            Check(window.ReviewGrid.Columns.All(column => column.Width.IsAuto),
                "Every review column must size to its optimal content width.");
            Check(window.SelectButton.Content?.ToString() == "Selektieren" && window.IsolateButton.Content?.ToString() == "Isolieren",
                "Existing included-row buttons changed.");
            Check(missingIsolation == null, "Window posted an action before a click.");
            window.SelectMissingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(missingIsolation == false && rowPlan == null, "Missing-select button must post its own view-wide action without row selection.");
            missingIsolation = null;
            window.IsolateMissingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(missingIsolation == true && rowPlan == null, "Missing-isolate button must post a separate isolation action.");
            window.ReviewGrid.SelectedItem = first;
            window.SelectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rowPlan?.ElementIds.SequenceEqual(["1"]) == true, "Existing row-select action changed.");
        }
        finally { window.Close(); }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Commands", "AvaX31ReviewCommand.cs")))
            root = root.Parent;
        Check(root != null, "Could not locate production command source.");
        var command = File.ReadAllText(Path.Combine(root!.FullName, "Commands", "AvaX31ReviewCommand.cs"));
        Check(command.Contains("workspace.LoadLast()", StringComparison.Ordinal)
            && command.Contains("new X31ReviewPosition(", StringComparison.Ordinal)
            && command.Contains("X31ReviewReader.ReadFile(dialog.FileName, positions, X31Writer.EvaluateReb91)", StringComparison.Ordinal),
            "Review must load exact position metadata and evaluate emitted REB91 approaches with the production evaluator.");
        Check(command.Contains("handler.SetMissingRequest(", StringComparison.Ordinal)
            && command.Contains("X31ReviewViewCandidates.Find(", StringComparison.Ordinal)
            && command.Contains("review.Rows.SelectMany(row => row.ElementIdsForSelection)", StringComparison.Ordinal)
            && command.Contains("uiDocument.ActiveView", StringComparison.Ordinal)
            && command.Contains("uiDocument.ActiveView.IsolateElementsTemporary(allIds)", StringComparison.Ordinal),
            "The command must wire both missing actions through the Revit external event, compare every X31 row, and isolate in the active view.");
        Check(command.IndexOf("X31ReviewViewCandidates.Find(document, uiDocument.ActiveView, includedIds)", StringComparison.Ordinal)
                < command.IndexOf("if (!ClearPreviousTemporaryIsolation(app, uiDocument, document)) return;", StringComparison.Ordinal),
            "Candidate snapshot must respect temporary hide/isolate before resetting the view mode.");
        Check(!command.Contains("using var transaction = new Transaction(document, \"AVA – vorherige", StringComparison.Ordinal)
            && command.Contains("if (!ClearPreviousTemporaryIsolation(app, uiDocument, document)) return;", StringComparison.Ordinal)
            && command.Contains("pendingReset", StringComparison.Ordinal)
            && command.Contains("X31ReviewTransactionRecovery.Resolve(", StringComparison.Ordinal),
            "A Pending reset must retain its transaction and stop this request before another transaction starts.");
        var candidateSource = File.ReadAllText(Path.Combine(root.FullName, "Ava", "Revit", "X31ReviewViewCandidates.cs"));
        Check(candidateSource.Contains("view.CropBoxActive", StringComparison.Ordinal)
            && candidateSource.Contains("GetCropRegionShapeManager()", StringComparison.Ordinal)
            && candidateSource.Contains("view is View3D view3D", StringComparison.Ordinal)
            && candidateSource.Contains("view3D.IsSectionBoxActive", StringComparison.Ordinal)
            && candidateSource.Contains("view3D.GetSectionBox()", StringComparison.Ordinal)
            && candidateSource.Contains("inverse.OfPoint(box.Transform.OfPoint(", StringComparison.Ordinal)
            && candidateSource.Contains("OverlapsRectangle(crop.Min.X", StringComparison.Ordinal)
            && candidateSource.Contains("OverlapsBox(sectionBox.Min.X", StringComparison.Ordinal),
            "The host adapter must check 2D crop and 3D section-box geometry in their transformed coordinate frames.");
        Check(!command.Contains("nicht enthaltenen sichtbaren Modellelemente", StringComparison.Ordinal),
            "A bounding-box candidate is not necessarily visible/actually drawn.");
        Console.WriteLine("PASS: view-wide missing candidates, crop policy, and reset lifecycle wiring (API doubles/static; no live Revit).");
    }
}
