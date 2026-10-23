using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMassist.Services;

namespace BIMassist.Core
{
    internal sealed class Bgk3DTextExternalEventHandler : IExternalEventHandler
    {
        internal Bgk3DTextBatch? Request { get; set; }
        internal Document? TargetDocument { get; set; }
        internal double HeightMm { get; set; }
        internal double DepthMm { get; set; }
        internal Action<string>? Report { get; set; }

        public void Execute(UIApplication app)
        {
            Bgk3DTextBatch? batch = Request;
            Document? target = TargetDocument;
            Request = null;
            TargetDocument = null;
            if (batch == null) return;
            FamilySymbol? createdSymbol = null;
            string familyStatus = string.Empty;
            try
            {
                if (target == null || !target.IsValidObject || !target.Equals(app.ActiveUIDocument?.Document))
                    throw new InvalidOperationException("Das Projekt wurde seit dem Klick gewechselt oder geschlossen. Bitte erneut auslösen.");
                Bgk3DTextBuildResult result = Bgk3DTextMultiFamilyBuilder.Build(app, target, batch, HeightMm, DepthMm);
                FamilySymbol symbol = result.Symbol;
                createdSymbol = symbol;
                familyStatus = result.NewFamily
                    ? $"3DText-Familie '{batch.FamilyName}' mit {result.AddedTypes} Typ(en) erstellt"
                    : result.AddedTypes > 0
                        ? $"3DText-Familie '{batch.FamilyName}' um {result.AddedTypes} Typ(en) ergänzt"
                        : $"3DText-Familie '{batch.FamilyName}' enthält die ausgewählten Typen bereits";
                // Die Familie muss vor der interaktiven Ebenen- oder Flächenwahl vollständig geladen sein.
                TaskDialog dialog = new TaskDialog("BGK-3DText platzieren")
                {
                    MainInstruction = "Platzierungsart auswählen",
                    MainContent = "Für die Arbeitsebene nach einem Mausklick mit ESC beenden; für eine Fläche diese einmal anklicken. Es muss kein neues Exemplar platziert werden.",
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Auf aktueller Arbeitsebene platzieren");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Auf einer angeklickten Fläche platzieren");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Zunächst keine Exemplare platzieren");
                dialog.DefaultButton = TaskDialogResult.CommandLink1;
                TaskDialogResult choice = dialog.Show();
                if (choice == TaskDialogResult.CommandLink3)
                {
                    Report?.Invoke(familyStatus + "; keine neuen Exemplare platziert.");
                    return;
                }
                if (choice == TaskDialogResult.Cancel)
                {
                    Report?.Invoke(familyStatus + "; Platzierung abgebrochen.");
                    return;
                }
                if (choice != TaskDialogResult.CommandLink1 && choice != TaskDialogResult.CommandLink2)
                    throw new InvalidOperationException("Keine Platzierungsart gewählt.");
                if (!target.IsValidObject || !target.Equals(app.ActiveUIDocument?.Document))
                    throw new InvalidOperationException("Projekt vor dem Platzieren gewechselt. Die Familie wurde erstellt, aber nicht platziert.");

                if (!symbol.IsActive)
                {
                    using (Transaction transaction = new Transaction(target, "BGK-3DText-Typ aktivieren"))
                    {
                        transaction.Start();
                        symbol.Activate();
                        target.Regenerate();
                        if (transaction.Commit() != TransactionStatus.Committed || !symbol.IsActive)
                            throw new InvalidOperationException("Der Familientyp konnte für die Platzierung nicht aktiviert werden.");
                    }
                }
                HashSet<long> existingInstanceIds = new FilteredElementCollector(target)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(i => i.Symbol?.Id == symbol.Id).Select(i => i.Id.Value).ToHashSet();

                if (choice == TaskDialogResult.CommandLink2)
                {
                    Report?.Invoke(familyStatus + ". Jetzt eine Fläche im Modell anklicken.");
                    Reference faceReference;
                    try
                    {
                        faceReference = app.ActiveUIDocument.Selection.PickObject(
                            Autodesk.Revit.UI.Selection.ObjectType.Face, "Eine Fläche für den BGK-3DText anklicken");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        Report?.Invoke(familyStatus + "; Platzierung abgebrochen.");
                        return;
                    }
                    if (!target.IsValidObject || !target.Equals(app.ActiveUIDocument?.Document))
                        throw new InvalidOperationException("Projekt nach der Flächenwahl gewechselt. Keine Instanz platziert.");
                    Face face = target.GetElement(faceReference)?.GetGeometryObjectFromReference(faceReference) as Face
                        ?? throw new InvalidOperationException("Die angeklickte Fläche ist keine Fläche im aktiven Projekt.");
                    XYZ clickPoint = faceReference.GlobalPoint
                        ?? throw new InvalidOperationException("Revit hat keinen Punkt auf der angeklickten Fläche geliefert.");
                    IntersectionResult projection = face.Project(clickPoint)
                        ?? throw new InvalidOperationException("Der angeklickte Punkt liegt nicht auf einer platzierbaren Fläche.");
                    XYZ normal = face.ComputeNormal(projection.UVPoint);
                    XYZ direction = XYZ.BasisX.Subtract(normal.Multiply(XYZ.BasisX.DotProduct(normal)));
                    if (direction.GetLength() < 1e-8)
                        direction = XYZ.BasisY.Subtract(normal.Multiply(XYZ.BasisY.DotProduct(normal)));
                    using (Transaction transaction = new Transaction(target, "BGK-3DText auf Fläche platzieren"))
                    {
                        transaction.Start();
                        FamilyInstance instance = target.Create.NewFamilyInstance(
                            faceReference, projection.XYZPoint, direction.Normalize(), symbol);
                        if (instance == null || transaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Revit konnte die Instanz nicht auf der Fläche platzieren.");
                    }
                }
                else
                {
                    Report?.Invoke(familyStatus + ". Jetzt einmal auf der Arbeitsebene klicken, dann ESC drücken.");
                    try
                    {
                        // Die Revit-API lässt mehrere Klicks zu; nach Beenden bleibt nur die erste Instanz.
                        app.ActiveUIDocument.PromptForFamilyInstancePlacement(symbol,
                            new PromptForFamilyInstancePlacementOptions
                            {
                                FaceBasedPlacementType = FaceBasedPlacementType.PlaceOnWorkPlane
                            });
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // ESC beendet die native Platzierung auch nach einem erfolgreichen Klick.
                    }
                }

                List<FamilyInstance> after = new FilteredElementCollector(target)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(i => i.Symbol?.Id == symbol.Id).ToList();
                HashSet<long> newlyPlacedIds = Bgk3DTextBatch.NewInstanceIds(
                    existingInstanceIds, after.Select(i => i.Id.Value)).ToHashSet();
                List<FamilyInstance> placed = after.Where(i => newlyPlacedIds.Contains(i.Id.Value))
                    .OrderBy(i => i.Id.Value).ToList();
                if (placed.Count == 0)
                {
                    Report?.Invoke(familyStatus + "; keine neue Instanz platziert.");
                    return;
                }
                if (placed.Count > 1)
                {
                    using (Transaction transaction = new Transaction(target, "Weitere BGK-3DText-Instanzen entfernen"))
                    {
                        transaction.Start();
                        foreach (FamilyInstance extra in placed.Skip(1))
                            target.Delete(extra.Id);
                        if (transaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Zusätzliche Instanzen konnten nicht entfernt werden.");
                    }
                }
                if (target.GetElement(placed[0].Id) == null || new FilteredElementCollector(target)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Count(i => i.Symbol?.Id == symbol.Id && newlyPlacedIds.Contains(i.Id.Value)) != 1)
                    throw new InvalidOperationException("Die einzelne platzierte Familieninstanz konnte nicht bestätigt werden.");
                Report?.Invoke(familyStatus + $"; Typ '{batch.PlacementTypeName}' einmal platziert.");
            }
            catch (Exception ex)
            {
                Report?.Invoke(createdSymbol == null
                    ? "3DText-Familie konnte nicht erstellt oder ergänzt werden: " + ex.Message
                    : familyStatus + "; Platzierung fehlgeschlagen: " + ex.Message);
            }
        }

        public string GetName() => "BIMassist – BGK-3DText generieren";
    }
}
