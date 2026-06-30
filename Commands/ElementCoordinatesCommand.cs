using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIMassist.Views;
using System;
using System.Globalization;
using System.Linq;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ElementCoordinatesCommand : IExternalCommand
    {
        private const double NumericalTolerance = 1e-9;
        private static ElementCoordinatesWindow? _window;
        private static ElementCoordinatesExternalEventHandler? _handler;
        private static ExternalEvent? _externalEvent;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                EnsureWindow(uiapp);

                FamilyInstance? selected = TryGetSelectedSupportedFamilyInstance(uidoc, doc);
                bool hasInitialSelection = selected != null;
                if (hasInitialSelection && _handler != null && _window != null)
                {
                    _handler.SetCurrentElement(uiapp, selected!.Id, _window);
                }

                if (_window == null)
                    return Result.Failed;

                if (!_window.IsVisible)
                    WpfOwner.ShowModeless(_window, uiapp);

                _window.Activate();

                if (!hasInitialSelection && _handler != null && _externalEvent != null)
                {
                    _window.SetStatus("Bitte ladbare Familie oder Projektfamilie auswählen. Nach der Auswahl werden die aktuellen Koordinaten hier angezeigt.");
                    _handler.RequestSelect(_window);
                    _externalEvent.Raise();
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Elementkoordinaten", "Fehler beim Öffnen der Elementkoordinaten:\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        public static void CloseWindow()
        {
            try
            {
                _window?.Close();
            }
            catch
            {
            }
            finally
            {
                _window = null;
                _handler = null;
                _externalEvent = null;
            }
        }

        private static void EnsureWindow(UIApplication uiapp)
        {
            if (_window != null)
                return;

            _handler = new ElementCoordinatesExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            _window = new ElementCoordinatesWindow(_handler, _externalEvent);
            _window.Closed += (_, _) =>
            {
                _window = null;
                _handler = null;
                _externalEvent = null;
            };
        }

        private static FamilyInstance? TryGetSelectedSupportedFamilyInstance(UIDocument uidoc, Document doc)
        {
            return uidoc.Selection.GetElementIds()
                .Select(doc.GetElement)
                .OfType<FamilyInstance>()
                .FirstOrDefault(IsSupportedFamilyInstance);
        }

        private static bool IsSupportedFamilyInstance(FamilyInstance instance)
        {
            try
            {
                Family? family = instance.Symbol?.Family;
                return family != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetElementPoint(FamilyInstance instance, out XYZ point, out string error)
        {
            point = XYZ.Zero;
            error = string.Empty;

            if (instance.Pinned)
            {
                error = "Das ausgewählte Element ist fixiert. Bitte Fixierung lösen und erneut versuchen.";
                return false;
            }

            if (instance.Location is LocationPoint locationPoint)
            {
                point = locationPoint.Point;
                return true;
            }

            if (instance.Location is LocationCurve locationCurve)
            {
                Curve curve = locationCurve.Curve;
                point = curve.Evaluate(0.5, true);
                return true;
            }

            BoundingBoxXYZ? box = instance.get_BoundingBox(null);
            if (box != null)
            {
                point = (box.Min + box.Max) * 0.5;
                return true;
            }

            error = "Für das ausgewählte Familienelement konnte kein Bezugspunkt ermittelt werden.";
            return false;
        }

        private sealed class ElementCoordinatesExternalEventHandler : IExternalEventHandler, IElementCoordinatesRequestHandler
        {
            private ElementCoordinatesWindow? _window;
            private RequestKind _requestKind = RequestKind.None;
            private ElementId? _currentElementId;

            public string GetName() => "BIMassist Elementkoordinaten ExternalEvent";

            public void RequestApply(ElementCoordinatesWindow window)
            {
                _window = window;
                _requestKind = RequestKind.Apply;
            }

            public void RequestSelect(ElementCoordinatesWindow window)
            {
                _window = window;
                _requestKind = RequestKind.Select;
            }

            public void Execute(UIApplication app)
            {
                ElementCoordinatesWindow? window = _window;
                RequestKind request = _requestKind;
                _requestKind = RequestKind.None;

                if (window == null)
                    return;

                try
                {
                    if (request == RequestKind.Select)
                    {
                        SelectNewElement(app, window);
                        return;
                    }

                    if (request == RequestKind.Apply)
                    {
                        ApplyCoordinates(app, window);
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    window.SetStatus("Auswahl abgebrochen.");
                }
                catch (Exception ex)
                {
                    window.SetStatus("Fehler: " + ex.Message);
                    TaskDialog.Show("Elementkoordinaten", "Fehler:\n\n" + ex.Message);
                }
            }

            public void SetCurrentElement(UIApplication app, ElementId elementId, ElementCoordinatesWindow window)
            {
                UIDocument uidoc = app.ActiveUIDocument;
                Document doc = uidoc.Document;
                if (doc.GetElement(elementId) is not FamilyInstance instance || !IsSupportedFamilyInstance(instance))
                {
                    window.ClearElement("Bitte eine ladbare Familie oder Projektfamilie auswählen. Systemfamilien werden nicht unterstützt.");
                    _currentElementId = null;
                    return;
                }

                if (!TryGetElementPoint(instance, out XYZ currentPoint, out string pointError))
                {
                    window.ClearElement(pointError);
                    _currentElementId = null;
                    return;
                }

                ProjectCoordinateSystem coordinateSystem = ProjectCoordinateSystem.FromDocument(doc, currentPoint);
                ProjectCoordinate currentCoordinates = coordinateSystem.GetProjectCoordinates(currentPoint);
                double currentEastingMeters = UnitUtils.ConvertFromInternalUnits(currentCoordinates.Easting, UnitTypeId.Meters);
                double currentNorthingMeters = UnitUtils.ConvertFromInternalUnits(currentCoordinates.Northing, UnitTypeId.Meters);

                _currentElementId = elementId;
                window.SetElementData(elementId, GetElementLabel(instance), currentEastingMeters, currentNorthingMeters);
            }

            private void SelectNewElement(UIApplication app, ElementCoordinatesWindow window)
            {
                UIDocument uidoc = app.ActiveUIDocument;
                Document doc = uidoc.Document;

                bool wasVisible = window.IsVisible;
                try
                {
                    window.Hide();
                    Reference reference = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new SupportedFamilyInstanceSelectionFilter(),
                        "Ladbare Familie oder Projektfamilie auswählen");

                    if (wasVisible)
                        window.Show();

                    SetCurrentElement(app, reference.ElementId, window);
                    window.Activate();
                }
                catch
                {
                    if (wasVisible && !window.IsVisible)
                        window.Show();
                    window.Activate();
                    throw;
                }
            }

            private void ApplyCoordinates(UIApplication app, ElementCoordinatesWindow window)
            {
                UIDocument uidoc = app.ActiveUIDocument;
                Document doc = uidoc.Document;

                ElementId? elementId = _currentElementId;
                if (elementId == null || doc.GetElement(elementId) is not FamilyInstance instance || !IsSupportedFamilyInstance(instance))
                {
                    window.ClearElement("Keine gültige ladbare Familie oder Projektfamilie geladen. Bitte 'Anderes Element auswählen' verwenden.");
                    _currentElementId = null;
                    return;
                }

                if (!TryGetElementPoint(instance, out XYZ currentPoint, out string pointError))
                {
                    window.SetStatus(pointError);
                    return;
                }

                ProjectCoordinateSystem coordinateSystem = ProjectCoordinateSystem.FromDocument(doc, currentPoint);
                ProjectCoordinate currentCoordinates = coordinateSystem.GetProjectCoordinates(currentPoint);

                double targetEasting = UnitUtils.ConvertToInternalUnits(window.TargetEastingMeters, UnitTypeId.Meters);
                double targetNorthing = UnitUtils.ConvertToInternalUnits(window.TargetNorthingMeters, UnitTypeId.Meters);

                XYZ moveVector = coordinateSystem.GetModelDelta(
                    targetEasting - currentCoordinates.Easting,
                    targetNorthing - currentCoordinates.Northing);

                if (moveVector.GetLength() > NumericalTolerance)
                {
                    using Transaction tx = new Transaction(doc, "Element nach Koordinaten setzen");
                    tx.Start();
                    ElementTransformUtils.MoveElement(doc, instance.Id, moveVector);
                    tx.Commit();
                }

                if (doc.GetElement(instance.Id) is FamilyInstance movedInstance && TryGetElementPoint(movedInstance, out XYZ movedPoint, out _))
                {
                    ProjectCoordinateSystem movedCoordinateSystem = ProjectCoordinateSystem.FromDocument(doc, movedPoint);
                    ProjectCoordinate movedCoordinates = movedCoordinateSystem.GetProjectCoordinates(movedPoint);
                    double currentEastingMeters = UnitUtils.ConvertFromInternalUnits(movedCoordinates.Easting, UnitTypeId.Meters);
                    double currentNorthingMeters = UnitUtils.ConvertFromInternalUnits(movedCoordinates.Northing, UnitTypeId.Meters);
                    window.UpdateCurrentCoordinates(currentEastingMeters, currentNorthingMeters);
                }

                if (window.CloseAfterApply)
                    window.Close();
            }

            private static string GetElementLabel(FamilyInstance instance)
            {
                string familyName = instance.Symbol?.FamilyName ?? instance.Name ?? "Familie";
                string typeName = instance.Symbol?.Name ?? string.Empty;
                string label = string.IsNullOrWhiteSpace(typeName) ? familyName : familyName + " : " + typeName;
                if (instance.Symbol?.Family?.IsInPlace == true)
                    label += " (Projektfamilie)";
                return label;
            }

            private enum RequestKind
            {
                None,
                Apply,
                Select
            }
        }

        private sealed class SupportedFamilyInstanceSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is FamilyInstance familyInstance && IsSupportedFamilyInstance(familyInstance);
            }

            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private readonly struct ProjectCoordinate
        {
            public ProjectCoordinate(double easting, double northing)
            {
                Easting = easting;
                Northing = northing;
            }

            public double Easting { get; }
            public double Northing { get; }
        }

        private sealed class ProjectCoordinateSystem
        {
            private readonly XYZ _originPoint;
            private readonly ProjectCoordinate _originProjectCoordinate;
            private readonly double _xEasting;
            private readonly double _xNorthing;
            private readonly double _yEasting;
            private readonly double _yNorthing;

            private ProjectCoordinateSystem(
                XYZ originPoint,
                ProjectCoordinate originProjectCoordinate,
                double xEasting,
                double xNorthing,
                double yEasting,
                double yNorthing)
            {
                _originPoint = originPoint;
                _originProjectCoordinate = originProjectCoordinate;
                _xEasting = xEasting;
                _xNorthing = xNorthing;
                _yEasting = yEasting;
                _yNorthing = yNorthing;
            }

            public static ProjectCoordinateSystem FromDocument(Document doc, XYZ originPoint)
            {
                ProjectLocation projectLocation = doc.ActiveProjectLocation;
                ProjectCoordinate origin = ReadProjectPosition(projectLocation, originPoint);
                ProjectCoordinate xStep = ReadProjectPosition(projectLocation, originPoint + XYZ.BasisX);
                ProjectCoordinate yStep = ReadProjectPosition(projectLocation, originPoint + XYZ.BasisY);

                return new ProjectCoordinateSystem(
                    originPoint,
                    origin,
                    xStep.Easting - origin.Easting,
                    xStep.Northing - origin.Northing,
                    yStep.Easting - origin.Easting,
                    yStep.Northing - origin.Northing);
            }

            public ProjectCoordinate GetProjectCoordinates(XYZ point)
            {
                XYZ delta = point - _originPoint;
                return new ProjectCoordinate(
                    _originProjectCoordinate.Easting + (delta.X * _xEasting) + (delta.Y * _yEasting),
                    _originProjectCoordinate.Northing + (delta.X * _xNorthing) + (delta.Y * _yNorthing));
            }

            public XYZ GetModelDelta(double eastingDelta, double northingDelta)
            {
                double determinant = (_xEasting * _yNorthing) - (_yEasting * _xNorthing);
                if (Math.Abs(determinant) <= 1e-12)
                    return new XYZ(eastingDelta, northingDelta, 0);

                double dx = ((eastingDelta * _yNorthing) - (_yEasting * northingDelta)) / determinant;
                double dy = ((_xEasting * northingDelta) - (eastingDelta * _xNorthing)) / determinant;
                return new XYZ(dx, dy, 0);
            }

            private static ProjectCoordinate ReadProjectPosition(ProjectLocation projectLocation, XYZ point)
            {
                ProjectPosition position = projectLocation.GetProjectPosition(point);
                return new ProjectCoordinate(position.EastWest, position.NorthSouth);
            }
        }
    }
}
