using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Globalization;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CreatePipePlaceholdersFromLinesCommand : IExternalCommand
    {
        private const double MinSegmentLengthFeet = 1.0 / 304.8; // ca. 1 mm

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                PipePlaceholderOptions options = PipePlaceholderOptionsDialog.Ask(doc);
                if (options == null)
                    return Result.Cancelled;

                List<LineSegment3D> rawSegments = CollectLineSegments(uidoc, doc, out string selectionInfo);
                if (rawSegments.Count == 0)
                {
                    TaskDialog.Show("BIMassist - Neue Leitung aus Linien erstellen",
                        "Es wurden keine verwertbaren Linien gefunden.\n\n" +
                        "Unterstützt werden Revit-Modelllinien/Detail-Linien sowie Linien/Polylinien aus importierten oder eingebetteten CAD-Dateien.");
                    return Result.Cancelled;
                }

                List<LineSegment3D> validSegments = rawSegments
                    .Where(s => s.Length > MinSegmentLengthFeet)
                    .ToList();

                if (validSegments.Count == 0)
                {
                    TaskDialog.Show("BIMassist - Neue Leitung aus Linien erstellen",
                        "Die gewählten Linien sind zu kurz, um Rohrplatzhalter zu erzeugen.");
                    return Result.Cancelled;
                }

                List<LineSegment3D> connectedSegments = BuildContinuousSegments(validSegments, options.MaxBridgeGapInternal, out int bridgeCount, out int skippedBridgeCount);

                int created = 0;
                int diameterSet = 0;
                int converted = 0;
                int failed = 0;
                var createdPlaceholderIds = new List<ElementId>();
                var failures = new List<string>();

                using (Transaction tx = new Transaction(doc, "Neue Leitung aus Linien erstellen"))
                {
                    tx.Start();

                    foreach (LineSegment3D segment in connectedSegments)
                    {
                        if (segment.Length <= MinSegmentLengthFeet)
                            continue;

                        try
                        {
                            Pipe pipe = Pipe.CreatePlaceholder(doc,
                                options.SystemTypeId,
                                options.PipeTypeId,
                                options.LevelId,
                                segment.Start,
                                segment.End);

                            if (pipe == null)
                            {
                                failed++;
                                failures.Add("Revit gab keinen Rohrplatzhalter zurück.");
                                continue;
                            }

                            created++;
                            createdPlaceholderIds.Add(pipe.Id);
                            if (TrySetPipeDiameter(pipe, options.DiameterInternal))
                                diameterSet++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            if (failures.Count < 6)
                                failures.Add(ex.Message);
                        }
                    }

                    if (created == 0)
                    {
                        tx.RollBack();
                        TaskDialog.Show("BIMassist - Neue Leitung aus Linien erstellen",
                            "Es konnte kein Rohrplatzhalter erzeugt werden.\n\n" +
                            string.Join("\n", failures.Distinct().Take(6)));
                        return Result.Failed;
                    }

                    if (options.ConvertPlaceholdersToPipes && createdPlaceholderIds.Count > 0)
                    {
                        try
                        {
                            ICollection<ElementId> convertedIds = PlumbingUtils.ConvertPipePlaceholders(doc, createdPlaceholderIds);
                            converted = convertedIds?.Count ?? 0;
                        }
                        catch (Exception ex)
                        {
                            failures.Add("Konvertierung in Rohrleitungssystem fehlgeschlagen: " + ex.Message);
                        }
                    }

                    tx.Commit();
                }

                string failureText = failed > 0
                    ? $"\nFehlgeschlagene Teilstrecken: {failed}\n" + string.Join("\n", failures.Distinct().Take(4))
                    : string.Empty;

                TaskDialog.Show("BIMassist - Neue Leitung aus Linien erstellen",
                    $"Rohrplatzhalter wurden erstellt.\n\n" +
                    $"Auswahl: {selectionInfo}\n" +
                    $"Gefundene Liniensegmente: {validSegments.Count}\n" +
                    $"Erzeugte Rohrplatzhalter: {created}\n" +
                    $"Davon mit gesetztem Durchmesser: {diameterSet}\n" +
                    $"In Rohrleitungssystem konvertiert: {(options.ConvertPlaceholdersToPipes ? converted.ToString(CultureInfo.InvariantCulture) : "Nein")}\n" +
                    $"Erzeugte Überbrückungen: {bridgeCount}\n" +
                    $"Nicht überbrückte Lücken wegen Grenzwert: {skippedBridgeCount}" +
                    failureText);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("BIMassist - Neue Leitung aus Linien erstellen", "Fehler:\n" + ex.Message);
                return Result.Failed;
            }
        }

        private static bool TrySetPipeDiameter(Pipe pipe, double diameterInternal)
        {
            BuiltInParameter[] candidates =
            {
                BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
                BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
                BuiltInParameter.RBS_PIPE_OUTER_DIAMETER
            };

            foreach (BuiltInParameter bip in candidates)
            {
                Parameter p = pipe.get_Parameter(bip);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double)
                    continue;

                try
                {
                    p.Set(diameterInternal);
                    double readBack = p.AsDouble();
                    if (Math.Abs(readBack - diameterInternal) < 1e-9)
                        return true;
                }
                catch
                {
                    // nächster Durchmesserparameter
                }
            }

            return false;
        }

        private static List<LineSegment3D> CollectLineSegments(UIDocument uidoc, Document doc, out string selectionInfo)
        {
            var result = new List<LineSegment3D>();
            var selectedIds = uidoc.Selection.GetElementIds()?.ToList() ?? new List<ElementId>();

            if (selectedIds.Count > 0)
            {
                foreach (ElementId id in selectedIds)
                {
                    Element element = doc.GetElement(id);
                    CollectElementLineSegments(element, Transform.Identity, result);
                }

                selectionInfo = $"{selectedIds.Count} vorselektierte Elemente";
                return DeduplicateSegments(result);
            }

            IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element, new LineSourceSelectionFilter(),
                "Revit-Linien oder CAD-Importe auswählen. Bei CAD-Importen werden Linien/Polylinien aus dem gewählten Import gelesen.");

            foreach (Reference reference in refs)
            {
                Element element = doc.GetElement(reference.ElementId);
                if (element == null)
                    continue;

                GeometryObject referencedGeometry = null;
                try { referencedGeometry = element.GetGeometryObjectFromReference(reference); } catch { }

                if (referencedGeometry is Curve curveFromReference)
                    AddCurveSegments(curveFromReference, Transform.Identity, result);
                else
                    CollectElementLineSegments(element, Transform.Identity, result);
            }

            selectionInfo = $"{refs.Count} gewählte Elemente/Referenzen";
            return DeduplicateSegments(result);
        }

        private static void CollectElementLineSegments(Element element, Transform transform, List<LineSegment3D> result)
        {
            if (element == null)
                return;

            if (element is CurveElement curveElement)
            {
                AddCurveSegments(curveElement.GeometryCurve, transform, result);
                return;
            }

            Options options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement geometry = element.get_Geometry(options);
            if (geometry == null)
                return;

            CollectGeometryLineSegments(geometry, transform, result);
        }

        private static void CollectGeometryLineSegments(IEnumerable<GeometryObject> geometry, Transform transform, List<LineSegment3D> result)
        {
            foreach (GeometryObject obj in geometry)
            {
                if (obj is GeometryInstance instance)
                {
                    Transform nestedTransform = transform.Multiply(instance.Transform);
                    GeometryElement symbolGeometry = instance.GetSymbolGeometry();
                    if (symbolGeometry != null)
                        CollectGeometryLineSegments(symbolGeometry, nestedTransform, result);
                    continue;
                }

                if (obj is Curve curve)
                {
                    AddCurveSegments(curve, transform, result);
                    continue;
                }

                if (obj is PolyLine polyLine)
                {
                    IList<XYZ> points = polyLine.GetCoordinates();
                    for (int i = 0; i < points.Count - 1; i++)
                        AddSegment(transform.OfPoint(points[i]), transform.OfPoint(points[i + 1]), result);
                }
            }
        }

        private static void AddCurveSegments(Curve curve, Transform transform, List<LineSegment3D> result)
        {
            if (curve == null || !curve.IsBound)
                return;

            if (curve is Line line)
            {
                AddSegment(transform.OfPoint(line.GetEndPoint(0)), transform.OfPoint(line.GetEndPoint(1)), result);
                return;
            }

            // Rohrplatzhalter werden als gerade Teilstücke erzeugt. Nicht-lineare CAD-/Revit-Kurven werden
            // kontrolliert über Revit-Tessellierung in kurze gerade Rohrplatzhalter zerlegt.
            IList<XYZ> points = curve.Tessellate();
            for (int i = 0; i < points.Count - 1; i++)
                AddSegment(transform.OfPoint(points[i]), transform.OfPoint(points[i + 1]), result);
        }

        private static void AddSegment(XYZ start, XYZ end, List<LineSegment3D> result)
        {
            if (start == null || end == null || start.DistanceTo(end) <= MinSegmentLengthFeet)
                return;

            result.Add(new LineSegment3D(start, end, false));
        }

        private static List<LineSegment3D> DeduplicateSegments(List<LineSegment3D> segments)
        {
            var unique = new List<LineSegment3D>();
            var keys = new HashSet<string>();

            foreach (LineSegment3D segment in segments)
            {
                string a = PointKey(segment.Start);
                string b = PointKey(segment.End);
                string key = string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
                if (keys.Add(key))
                    unique.Add(segment);
            }

            return unique;
        }

        private static string PointKey(XYZ p)
        {
            const double scale = 304.8; // Fuß -> mm
            return $"{Math.Round(p.X * scale, 3):0.###};{Math.Round(p.Y * scale, 3):0.###};{Math.Round(p.Z * scale, 3):0.###}";
        }

        private static List<LineSegment3D> BuildContinuousSegments(List<LineSegment3D> source, double maxBridgeGap, out int bridgeCount, out int skippedBridgeCount)
        {
            var remaining = new List<LineSegment3D>(source);
            var ordered = new List<LineSegment3D>();
            bridgeCount = 0;
            skippedBridgeCount = 0;

            if (remaining.Count == 0)
                return ordered;

            LineSegment3D current = remaining[0];
            remaining.RemoveAt(0);
            ordered.Add(current);
            XYZ currentEnd = current.End;

            while (remaining.Count > 0)
            {
                int bestIndex = -1;
                bool reverse = false;
                double bestDistance = double.MaxValue;

                for (int i = 0; i < remaining.Count; i++)
                {
                    double dStart = currentEnd.DistanceTo(remaining[i].Start);
                    if (dStart < bestDistance)
                    {
                        bestDistance = dStart;
                        bestIndex = i;
                        reverse = false;
                    }

                    double dEnd = currentEnd.DistanceTo(remaining[i].End);
                    if (dEnd < bestDistance)
                    {
                        bestDistance = dEnd;
                        bestIndex = i;
                        reverse = true;
                    }
                }

                if (bestIndex < 0)
                    break;

                LineSegment3D next = remaining[bestIndex];
                remaining.RemoveAt(bestIndex);
                if (reverse)
                    next = next.Reversed();

                if (bestDistance > MinSegmentLengthFeet)
                {
                    if (maxBridgeGap <= 0 || bestDistance <= maxBridgeGap)
                    {
                        ordered.Add(new LineSegment3D(currentEnd, next.Start, true));
                        bridgeCount++;
                    }
                    else
                    {
                        skippedBridgeCount++;
                    }
                }

                ordered.Add(next);
                currentEnd = next.End;
            }

            return ordered;
        }

        private class LineSourceSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem == null)
                    return false;

                if (elem is CurveElement || elem is ImportInstance)
                    return true;

                BuiltInCategory? bic = TryGetBuiltInCategory(elem.Category?.Id);
                return bic == BuiltInCategory.OST_Lines
                    || bic == BuiltInCategory.OST_SketchLines
                    || bic == BuiltInCategory.OST_ImportObjectStyles;
            }

            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private static BuiltInCategory? TryGetBuiltInCategory(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
                return null;

            long value = id.Value;
            return Enum.IsDefined(typeof(BuiltInCategory), (int)value) ? (BuiltInCategory)(int)value : null;
        }

        private readonly record struct LineSegment3D(XYZ Start, XYZ End, bool IsBridge)
        {
            public double Length => Start.DistanceTo(End);
            public LineSegment3D Reversed() => new LineSegment3D(End, Start, IsBridge);
        }

        private class PipePlaceholderOptions
        {
            public ElementId SystemTypeId { get; init; }
            public ElementId PipeTypeId { get; init; }
            public ElementId LevelId { get; init; }
            public double DiameterInternal { get; init; }
            public double MaxBridgeGapInternal { get; init; }
            public bool ConvertPlaceholdersToPipes { get; init; }
        }

        private class ElementChoice<T> where T : Element
        {
            public T Element { get; }
            public string Name { get; }

            public ElementChoice(T element)
            {
                Element = element;
                Name = element?.Name ?? "<ohne Name>";
            }

            public override string ToString() => Name;
        }

        private class PipePlaceholderOptionsDialog : WF.Form
        {
            private readonly WF.ComboBox _systemCombo = new WF.ComboBox();
            private readonly WF.ComboBox _pipeTypeCombo = new WF.ComboBox();
            private readonly WF.ComboBox _levelCombo = new WF.ComboBox();
            private readonly WF.NumericUpDown _diameterBox = new WF.NumericUpDown();
            private readonly WF.NumericUpDown _bridgeGapBox = new WF.NumericUpDown();
            private readonly WF.CheckBox _convertCheckBox = new WF.CheckBox();
            private readonly WF.Button _okButton = new WF.Button();
            private readonly WF.Button _cancelButton = new WF.Button();

            private PipePlaceholderOptionsDialog(Document doc)
            {
                Text = "Neue Leitung aus Linien erstellen";
                StartPosition = WF.FormStartPosition.CenterScreen;
                FormBorderStyle = WF.FormBorderStyle.FixedDialog;
                MinimizeBox = false;
                MaximizeBox = false;
                ClientSize = new SD.Size(1200, 500);
                Font = new SD.Font("Segoe UI", 9F);
                AutoScaleMode = WF.AutoScaleMode.Dpi;

                var systems = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipingSystemType))
                    .Cast<PipingSystemType>()
                    .OrderBy(x => x.Name)
                    .Select(x => new ElementChoice<PipingSystemType>(x))
                    .ToList();

                var pipeTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(PipeType))
                    .Cast<PipeType>()
                    .OrderBy(x => x.Name)
                    .Select(x => new ElementChoice<PipeType>(x))
                    .ToList();

                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(x => x.Elevation)
                    .Select(x => new ElementChoice<Level>(x))
                    .ToList();

                AddLabel("Systemtyp", 32, 32);
                SetupCombo(_systemCombo, systems.Cast<object>().ToArray(), 520, 28);

                AddLabel("Rohrtyp", 32, 88);
                SetupCombo(_pipeTypeCombo, pipeTypes.Cast<object>().ToArray(), 520, 84);

                AddLabel("Bezugsebene", 32, 144);
                SetupCombo(_levelCombo, levels.Cast<object>().ToArray(), 520, 140);

                AddLabel("Durchmesser [mm]", 32, 200);
                SetupNumber(_diameterBox, 520, 196, 1, 10000, 100, 1);

                AddLabel("Max. Überbrückungslücke [cm]", 32, 256);
                SetupNumber(_bridgeGapBox, 520, 252, 0, 100000, 0, 10);
                AddHint("0 = alle Lücken zwischen den gewählten Linien automatisch überbrücken", 520, 286);

                _convertCheckBox.Text = "Platzhalter nach Erstellung direkt in Rohrleitungssystem konvertieren";
                _convertCheckBox.Location = new SD.Point(520, 330);
                _convertCheckBox.Size = new SD.Size(650, 32);
                _convertCheckBox.AutoSize = true;
                _convertCheckBox.Checked = true;
                Controls.Add(_convertCheckBox);

                _okButton.Text = "Erstellen";
                _okButton.DialogResult = WF.DialogResult.OK;
                _okButton.Location = new SD.Point(950, 440);
                _okButton.Size = new SD.Size(105, 34);

                _cancelButton.Text = "Abbrechen";
                _cancelButton.DialogResult = WF.DialogResult.Cancel;
                _cancelButton.Location = new SD.Point(1070, 440);
                _cancelButton.Size = new SD.Size(110, 34);

                Controls.Add(_okButton);
                Controls.Add(_cancelButton);
                AcceptButton = _okButton;
                CancelButton = _cancelButton;

                if (systems.Count > 0) _systemCombo.SelectedIndex = 0;
                if (pipeTypes.Count > 0) _pipeTypeCombo.SelectedIndex = 0;
                if (levels.Count > 0) _levelCombo.SelectedIndex = 0;
            }

            public PipePlaceholderOptions Options { get; private set; }

            public static PipePlaceholderOptions Ask(Document doc)
            {
                using var dialog = new PipePlaceholderOptionsDialog(doc);

                if (dialog._systemCombo.Items.Count == 0 || dialog._pipeTypeCombo.Items.Count == 0 || dialog._levelCombo.Items.Count == 0)
                {
                    TaskDialog.Show("BIMassist - Neue Leitung aus Linien erstellen",
                        "Im Projekt fehlen Systemtyp, Rohrtyp oder Ebene für Rohrplatzhalter.");
                    return null;
                }

                if (dialog.ShowDialog() != WF.DialogResult.OK)
                    return null;

                var system = ((ElementChoice<PipingSystemType>)dialog._systemCombo.SelectedItem).Element;
                var pipeType = ((ElementChoice<PipeType>)dialog._pipeTypeCombo.SelectedItem).Element;
                var level = ((ElementChoice<Level>)dialog._levelCombo.SelectedItem).Element;

                dialog.Options = new PipePlaceholderOptions
                {
                    SystemTypeId = system.Id,
                    PipeTypeId = pipeType.Id,
                    LevelId = level.Id,
                    DiameterInternal = UnitUtils.ConvertToInternalUnits((double)dialog._diameterBox.Value, UnitTypeId.Millimeters),
                    MaxBridgeGapInternal = dialog._bridgeGapBox.Value <= 0
                        ? 0
                        : UnitUtils.ConvertToInternalUnits((double)dialog._bridgeGapBox.Value, UnitTypeId.Centimeters),
                    ConvertPlaceholdersToPipes = dialog._convertCheckBox.Checked
                };

                return dialog.Options;
            }

            private void AddLabel(string text, int x, int y)
            {
                Controls.Add(new WF.Label
                {
                    Text = text,
                    Location = new SD.Point(x, y + 4),
                    AutoSize = true
                });
            }

            private void AddHint(string text, int x, int y)
            {
                Controls.Add(new WF.Label
                {
                    Text = text,
                    Location = new SD.Point(x, y),
                    AutoSize = true,
                    ForeColor = SD.Color.DimGray
                });
            }

            private void SetupCombo(WF.ComboBox combo, object[] items, int x, int y)
            {
                combo.DropDownStyle = WF.ComboBoxStyle.DropDownList;
                combo.Location = new SD.Point(x, y);
                combo.Size = new SD.Size(650, 32);
                combo.Items.AddRange(items);
                Controls.Add(combo);
            }

            private void SetupNumber(WF.NumericUpDown box, int x, int y, decimal min, decimal max, decimal value, decimal increment)
            {
                box.Location = new SD.Point(x, y);
                box.Size = new SD.Size(120, 25);
                box.Minimum = min;
                box.Maximum = max;
                box.Value = value;
                box.DecimalPlaces = 1;
                box.Increment = increment;
                Controls.Add(box);
            }
        }
    }
}
