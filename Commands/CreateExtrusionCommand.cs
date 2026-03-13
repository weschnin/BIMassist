using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.DB.Structure;
using System.IO;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CreateExtrusionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;

            bool isFamilyDoc = doc.IsFamilyDocument;

            // 1. Eingaben
            string familyName = Microsoft.VisualBasic.Interaction.InputBox("Name der Familie (bei Projektdokument):", "Familienname", "ExtrusionFamilie");
            if (string.IsNullOrWhiteSpace(familyName)) return Result.Cancelled;

            string depthStr = Microsoft.VisualBasic.Interaction.InputBox("Tiefe der Extrusion (in Metern):", "Tiefe", "1.0");
            if (!double.TryParse(depthStr.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double depthMeters) || depthMeters <= 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Ungültige Tiefe.");
                return Result.Failed;
            }

            double extrusionDepth = UnitUtils.ConvertToInternalUnits(depthMeters, UnitTypeId.Meters);

            TaskDialogResult dir = Autodesk.Revit.UI.TaskDialog.Show("Richtung", "Extrusionsrichtung?\nJa = Vorwärts\nNein = Rückwärts", TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);
            bool reverse = dir == TaskDialogResult.No;

            // 2. Fläche wählen
            Reference faceRef = uidoc.Selection.PickObject(ObjectType.Face, "Wähle eine Fläche");
            Face face = doc.GetElement(faceRef).GetGeometryObjectFromReference(faceRef) as Face;
            if (face == null) return Result.Failed;

            UV uv = new UV(0.5, 0.5);
            XYZ origin = face.Evaluate(uv);
            XYZ normal = face.ComputeNormal(uv);
            if (reverse) normal = -normal;

            // 3. Ziel-Dokument vorbereiten
            Document targetDoc = doc;

            if (!isFamilyDoc)
            {
                // Projekt: Neue Familie erzeugen
                string templatePath = Properties.Settings.Default.PfadVorlageAllgemeineFamilie;
                if (string.IsNullOrEmpty(templatePath)) templatePath = app.FamilyTemplatePath + "\\Vorlage Allgemeine Familie.rft";

                if (!File.Exists(templatePath))
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Fehler", "Vorlage nicht gefunden:\n" + templatePath);
                    return Result.Failed;
                }

                targetDoc = app.NewFamilyDocument(templatePath);
            }

            // 4. Extrusion erzeugen
            using (Transaction tx = new Transaction(targetDoc, "Extrusion erstellen"))
            {
                tx.Start();

                Plane sketchPlane = Plane.CreateByNormalAndOrigin(normal, origin);
                SketchPlane sp = SketchPlane.Create(targetDoc, sketchPlane);
                Transform identity = Transform.Identity;

                IList<CurveLoop> loops = face.GetEdgesAsCurveLoops();
                CurveArrArray loopArray = new CurveArrArray();

                foreach (CurveLoop loop in loops)
                {
                    CurveArray ca = new CurveArray();
                    foreach (Curve c in loop)
                    {
                        ca.Append(c.CreateTransformed(identity));
                    }
                    loopArray.Append(ca);
                }

                targetDoc.FamilyCreate.NewExtrusion(true, loopArray, sp, extrusionDepth);

                tx.Commit();
            }

            if (!isFamilyDoc)
            {
                // 5. Familie speichern & laden
                string tempPath = Path.Combine(Path.GetTempPath(), familyName + ".rfa");
                targetDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
                targetDoc.Close(false);

                Family loadedFamily;
                using (Transaction tx = new Transaction(doc, "Familie laden"))
                {
                    tx.Start();
                    doc.LoadFamily(tempPath, out loadedFamily);
                    tx.Commit();
                }

                // 6. Instanz platzieren
                FamilySymbol symbol = loadedFamily.GetFamilySymbolIds()
                    .Select(id => doc.GetElement(id))
                    .OfType<FamilySymbol>()
                    .FirstOrDefault();

                if (symbol != null)
                {
                    using (Transaction tx = new Transaction(doc, "Instanz platzieren"))
                    {
                        tx.Start();
                        if (!symbol.IsActive) symbol.Activate();
                        doc.Create.NewFamilyInstance(origin, symbol, StructuralType.NonStructural);
                        tx.Commit();
                    }
                }

                Autodesk.Revit.UI.TaskDialog.Show("Fertig", $"Familie '{familyName}' wurde platziert.");
            }
            else
            {
                Autodesk.Revit.UI.TaskDialog.Show("Fertig", $"Extrusion in Familien-Dokument eingefügt.");
            }

            return Result.Succeeded;
        }
    }
}
