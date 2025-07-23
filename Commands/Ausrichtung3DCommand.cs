using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BIMassist.Commands
{
    [Transaction(TransactionMode.Manual)]
    internal class Ausrichtung3DCommand : IExternalCommand
    {
        private ViewOrientation3D _originalOrientation;
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                View3D view3D = doc.ActiveView as View3D;
                if (view3D == null || view3D.IsTemplate)
                {
                    TaskDialog.Show("Fehler", "Die aktive Ansicht ist keine gültige 3D-Ansicht.");
                    return Result.Failed;
                }

                // Fläche auswählen
                Reference faceRef = uidoc.Selection.PickObject(ObjectType.Face, "Wähle eine Fläche zum Ausrichten");
                Element element = doc.GetElement(faceRef);
                Face face = element.GetGeometryObjectFromReference(faceRef) as Face;
                if (face == null) return Result.Failed;

                BoundingBoxUV bb = face.GetBoundingBox();
                UV uv = (bb.Min + bb.Max) * 0.5;
                XYZ faceNormal = face.ComputeNormal(uv).Normalize();

                if (view3D.IsSectionBoxActive)
                {
                    // ======= AUSRICHTUNG DER SCHNITTBOX (UNVERÄNDERT) =======
                    BoundingBoxXYZ sectionBox = view3D.GetSectionBox();
                    Transform boxTransform = sectionBox.Transform;
                    XYZ boxMin = sectionBox.Min;
                    XYZ boxMax = sectionBox.Max;
                    XYZ boxCenterLocal = (boxMin + boxMax) * 0.5;
                    XYZ boxCenterWorld = boxTransform.OfPoint(boxCenterLocal);

                    XYZ[] localFaceNormals = new XYZ[]
                    {
                    new XYZ(1, 0, 0),
                    new XYZ(-1, 0, 0),
                    new XYZ(0, 1, 0),
                    new XYZ(0, -1, 0),
                    new XYZ(0, 0, 1),
                    new XYZ(0, 0, -1),
                    };

                    XYZ[] worldNormals = new XYZ[6];
                    for (int i = 0; i < 6; i++)
                    {
                        worldNormals[i] = boxTransform.BasisX * localFaceNormals[i].X
                                        + boxTransform.BasisY * localFaceNormals[i].Y
                                        + boxTransform.BasisZ * localFaceNormals[i].Z;
                        worldNormals[i] = worldNormals[i].Normalize();
                    }

                    int bestIndex = 0;
                    double maxDot = -1;
                    for (int i = 0; i < 6; i++)
                    {
                        double dot = Math.Abs(worldNormals[i].DotProduct(faceNormal));
                        if (dot > maxDot)
                        {
                            maxDot = dot;
                            bestIndex = i;
                        }
                    }

                    XYZ sideNormal = worldNormals[bestIndex];
                    XYZ rotationAxis = sideNormal.CrossProduct(faceNormal);
                    double axisLength = rotationAxis.GetLength();

                    using (Transaction tx = new Transaction(doc, "Schnittbox ausrichten"))
                    {
                        tx.Start();

                        Transform rotation;
                        if (axisLength < 1e-9)
                        {
                            double angle = sideNormal.DotProduct(faceNormal) > 0 ? 0 : Math.PI;
                            rotation = Transform.CreateRotationAtPoint(sideNormal, angle, boxCenterWorld);
                        }
                        else
                        {
                            rotationAxis = rotationAxis.Normalize();
                            double angle = sideNormal.AngleTo(faceNormal);
                            rotation = Transform.CreateRotationAtPoint(rotationAxis, angle, boxCenterWorld);
                        }

                        Transform newTransform = rotation.Multiply(boxTransform);
                        sectionBox.Transform = newTransform;
                        view3D.SetSectionBox(sectionBox);

                        tx.Commit();
                    }
                }
                else
                {
                    // ======= ANSICHTSAUSRICHTUNG + ZENTRIERUNG + ZOOM =======

                    // Ansichtsmittelpunkt aus SectionBox oder Ursprung
                    BoundingBoxXYZ bbox = view3D.GetSectionBox();
                    XYZ viewCenter = bbox != null
                        ? bbox.Transform.OfPoint((bbox.Min + bbox.Max) * 0.5)
                        : XYZ.Zero;

                    XYZ viewDir = faceNormal.Negate();

                    XYZ approxUp = XYZ.BasisZ;
                    if (Math.Abs(viewDir.DotProduct(approxUp)) > 0.99)
                        approxUp = XYZ.BasisY;

                    XYZ right = viewDir.CrossProduct(approxUp).Normalize();
                    XYZ up = right.CrossProduct(viewDir).Normalize();

                    // Fläche analysieren
                    BoundingBoxUV faceBB = face.GetBoundingBox();
                    XYZ faceCenter = face.Evaluate((faceBB.Min + faceBB.Max) * 0.5);
                    double faceArea = (faceBB.Max.U - faceBB.Min.U) * (faceBB.Max.V - faceBB.Min.V);

                    // Abstand prüfen (ob Fläche im Bild)
                    XYZ toFace = faceCenter - viewCenter;
                    double offset = toFace.DotProduct(viewDir);
                    if (offset < -0.1)
                    {
                        // Fläche liegt hinter Kamera -> auf Fläche zentrieren
                        viewCenter = faceCenter;
                    }

                    // Fläche in Mitte bringen
                    viewCenter = faceCenter;

                    // Fläche grob messen (Diagonale in Weltkoordinaten)
                    EdgeArrayArray edgeLoops = face.EdgeLoops;
                    double maxDistance = 0;

                    foreach (EdgeArray loop in edgeLoops)
                    {
                        foreach (Edge edge in loop)
                        {
                            IList<XYZ> points = edge.Tessellate();
                            for (int i = 0; i < points.Count - 1; i++)
                            {
                                double dist = points[i].DistanceTo(points[i + 1]);
                                if (dist > maxDistance)
                                    maxDistance = dist;
                            }
                        }
                    }

                    // Berechne gewünschten Abstand mit fiktivem Sichtwinkel (Field of View)
                    double fieldOfViewDegrees = 45.0;
                    double fieldOfViewRadians = fieldOfViewDegrees * Math.PI / 180.0;
                    double requiredDistance = (maxDistance * 0.5) / Math.Tan(fieldOfViewRadians * 0.5);

                    // Kamera rückversetzen, damit Fläche ins Sichtfeld passt
                    viewCenter = faceCenter - viewDir * requiredDistance;

                    ViewOrientation3D orientation = new ViewOrientation3D(viewCenter, up, viewDir);

                    using (Transaction tx = new Transaction(doc, "3D-Ansicht ausrichten"))
                    {
                        tx.Start();
                        view3D.SetOrientation(orientation);
                        tx.Commit();
                    }
                }

                uidoc.RefreshActiveView();
                //System.Windows.Forms.Application.DoEvents(); // optional UI-Refresh

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Fehler", ex.ToString());
                return Result.Failed;
            }
        }
    }
}
