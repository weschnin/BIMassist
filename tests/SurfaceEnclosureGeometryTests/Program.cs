string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string path = Path.Combine(root, "Services", "SurfaceEnclosureBuilder.cs");
if (!File.Exists(path))
    throw new Exception("Die Flächen-Rekonstruktion fehlt.");
string code = File.ReadAllText(path);
foreach (string required in new[] { "FaceIntersectionMath", "TryBuild(", "Triangulate(",
    "CutWithHalfSpace(", "face.Project(", "face.IsInside(", "shortCurveTolerance", "Solid",
    "GetEdgesAsCurveLoops()", "VerifyIntersectionEdges(", "DistanceToSegment(",
    "NearExistingBoundary(", "IsConvexPlanarBoundary(" })
    if (!code.Contains(required, StringComparison.Ordinal))
        throw new Exception("Erkennung von Schnittlinien, begrenzten Flächen oder Abschlussprüfung fehlt: " + required);
int intersect = code.IndexOf("FaceIntersectionMath", StringComparison.Ordinal);
int create = code.IndexOf("CutWithHalfSpace(", StringComparison.Ordinal);
if (intersect >= create)
    throw new Exception("Schnittlinien müssen vor der Volumenrekonstruktion ermittelt werden.");
if (code.Contains("TryBuildPrimaryCylinderCutBySelectedCylinders", StringComparison.Ordinal)
    || code.Contains("CreateSeedBox(points)", StringComparison.Ordinal))
    throw new Exception("Der alte heuristische Volumenkörperpfad darf keine Flächen außerhalb der Auswahl erfinden.");
if (!code.Contains("faces[sourceIndex] is PlanarFace", StringComparison.Ordinal))
    throw new Exception("Pro planarer Fläche genügt eine Ebene; unnötige Boolean-Schnitte sind zu vermeiden.");
if (!code.Contains("meshes.Count + 1", StringComparison.Ordinal)
    || !code.Contains("face.GetType().Name", StringComparison.Ordinal)
    || !code.Contains("boundaryLoops", StringComparison.Ordinal))
    throw new Exception("Abgewiesene Mehrfach-Randflächen müssen mit Flächennummer, Art und Loop-Anzahl diagnostiziert werden.");
int closedShell = code.IndexOf("TryBuildClosedSelectedShell(", StringComparison.Ordinal);
int multiLoopGuard = code.IndexOf("boundaryLoops == 0 || boundaryLoops > 1", StringComparison.Ordinal);
if (closedShell < 0 || multiLoopGuard < 0 || closedShell >= multiLoopGuard)
    throw new Exception("Mehrfach-Randflächen müssen vor der Halbraum-Konstruktion als geschlossene Auswahl geprüft werden.");
if (!code.Contains("ClosedTriangleShell.Validate(", StringComparison.Ordinal)
    || !code.Contains("TessellatedShapeBuilderTarget.Solid", StringComparison.Ordinal)
    || !code.Contains("TessellatedShapeBuilderFallback.Abort", StringComparison.Ordinal))
    throw new Exception("Die ausgewählten Dreiecke müssen topologisch geschlossen sein; Mesh-Fallback darf keine Kappen erfinden.");
if (!code.Contains("ExactSelectedSolidFaces.Match(", StringComparison.Ordinal)
    || !code.Contains("SolidUtils.Clone(", StringComparison.Ordinal))
    throw new Exception("Ein vollständiger bestehender Solid muss nur bei exaktem Abgleich aller ausgewählten Flächen geklont werden.");
int cutLimit = code.IndexOf("requestedCuts > MaxCutPlanes", StringComparison.Ordinal);
int geometryCreation = code.IndexOf("GeometryCreationUtilities.CreateExtrusionGeometry(", StringComparison.Ordinal);
if (cutLimit < 0 || geometryCreation < 0 || cutLimit >= geometryCreation)
    throw new Exception("Überkomplexe Flächen müssen vor teuren Boolean-Operationen abgewiesen werden.");
Console.WriteLine("Flächen-Hüllen: statische Integrationsprüfung bestanden (Geometrie in Revit separat prüfen).");
