using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;
using BIMassist.Views;

namespace BIMassist.Commands;

[Transaction(TransactionMode.ReadOnly)]
public sealed class AvaX31ReviewCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var uiapp = commandData.Application;
            var uiDocument = uiapp.ActiveUIDocument;
            if (uiDocument == null) throw new InvalidOperationException("Zuerst ein Revit-Projekt öffnen.");
            if (uiDocument.Document.IsFamilyDocument) throw new InvalidOperationException("Die X31-Prüfung steht nur in einem Revit-Projekt zur Verfügung.");

            var dialog = AvaFileDialogs.OpenX31();
            if (dialog.ShowDialog() != true) return Result.Cancelled;
            var review = X31ReviewReader.ReadFile(dialog.FileName);
            var handler = new X31ReviewExternalEventHandler(uiDocument.Document);
            var externalEvent = ExternalEvent.Create(handler);
            var window = new X31ReviewWindow(review, (plan, isolate) =>
            {
                handler.SetRequest(plan, isolate);
                externalEvent.Raise();
            });
            handler.Window = window;
            window.Closed += (_, _) => externalEvent.Dispose();
            WpfOwner.ShowModeless(window, uiapp);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = "Die X31-Prüfung konnte nicht geöffnet werden.\n\n" + ex.Message;
            TaskDialog.Show("AVA – X31 prüfen", message);
            return Result.Failed;
        }
    }

    private sealed class X31ReviewExternalEventHandler(Document sourceDocument) : IExternalEventHandler
    {
        private X31SelectionPlan? request;
        private bool isolate;

        public X31ReviewWindow? Window { get; set; }

        public string GetName() => "BIMassist X31 review selection";

        public void SetRequest(X31SelectionPlan plan, bool isolateRequest)
        {
            request = plan ?? throw new ArgumentNullException(nameof(plan));
            isolate = isolateRequest;
        }

        public void Execute(UIApplication app)
        {
            var plan = request;
            var isolateRequest = isolate;
            request = null;
            isolate = false;
            var window = Window;
            try
            {
                var uiDocument = app.ActiveUIDocument;
                if (uiDocument == null || !sourceDocument.IsValidObject || !ReferenceEquals(uiDocument.Document, sourceDocument))
                {
                    window?.ShowStatus("Das ursprüngliche Revit-Dokument ist nicht mehr aktiv. Öffnen Sie die X31-Prüfung für das aktuelle Dokument erneut.");
                    return;
                }
                if (plan == null || plan.ElementIds.Count == 0)
                {
                    window?.ShowStatus("Keine gültigen ElementIDs zum Auswählen vorhanden.");
                    return;
                }

                var document = uiDocument.Document;
                var seeds = new List<ElementId>();
                var missing = new List<string>();
                foreach (var text in plan.ElementIds)
                {
                    if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                    {
                        missing.Add(text);
                        continue;
                    }
                    var id = new ElementId(value);
                    if (document.GetElement(id) == null) missing.Add(text);
                    else seeds.Add(id);
                }
                seeds = seeds.Distinct().ToList();
                if (seeds.Count == 0)
                {
                    window?.ShowStatus("Keine der ausgewählten ElementIDs ist im aktiven Revit-Dokument vorhanden.");
                    return;
                }

                uiDocument.Selection.SetElementIds(seeds);
                if (!isolateRequest)
                {
                    window?.ShowStatus($"{seeds.Count} Revit-Element(e) selektiert." + (missing.Count > 0 ? $" {missing.Count} IDs fehlen im Dokument." : string.Empty));
                    return;
                }
                if (plan.SelectionIncomplete || missing.Count > 0)
                {
                    window?.ShowStatus("Teil-Isolation verhindert: Die X31-Zuordnung ist unvollständig oder ElementIDs fehlen. Vorhandene Elemente wurden nur selektiert.");
                    return;
                }

                var allIds = X31ElementSelectionPlanner.ExpandGroupMembers(seeds.Select(id => id.Value), rawId =>
                {
                    var element = document.GetElement(new ElementId(rawId));
                    return element is Group group ? group.GetMemberIds().Select(member => member.Value) : null;
                }).Select(id => new ElementId(id)).ToList();
                uiDocument.ActiveView.IsolateElementsTemporary(allIds);
                window?.ShowStatus($"{allIds.Count} Revit-Element(e) einschließlich verschachtelter Gruppen temporär isoliert.");
            }
            catch (Exception ex)
            {
                window?.ShowStatus("Revit konnte die Auswahl/Isolation nicht ausführen: " + ex.Message);
            }
        }
    }
}