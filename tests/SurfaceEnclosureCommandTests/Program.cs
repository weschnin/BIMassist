string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string ribbon = File.ReadAllText(Path.Combine(root, "App.cs"));
string commandClass = "BIMassist.Commands.SurfaceEnclosureFamilyCommand";
int geometryStart = ribbon.IndexOf("new PulldownButtonData(\"Geometrien\"", StringComparison.Ordinal);
int geometryEnd = ribbon.IndexOf("// Leitungs-Funktionen", geometryStart, StringComparison.Ordinal);
int newButton = ribbon.IndexOf(commandClass, StringComparison.Ordinal);
if (geometryStart < 0 || geometryEnd < 0 || newButton <= geometryStart || newButton >= geometryEnd)
    throw new Exception("Die neue Funktion muss in der Gruppe Geometrien registriert sein.");
if (!ribbon.Contains("Geometrie auf umschlossenen Flächen", StringComparison.Ordinal))
    throw new Exception("Die Funktion ist für Nutzer nicht beschriftet.");

string command = File.ReadAllText(Path.Combine(root, "Commands", "SurfaceEnclosureFamilyCommand.cs"));
foreach (string required in new[] { "ObjectType.Face", "PickObjects(", "ConvertToStableRepresentation(",
    "SurfaceEnclosureBuilder", "SurfaceEnclosureFamilyBuilder", "Result.Failed", "Result.Cancelled" })
    if (!command.Contains(required, StringComparison.Ordinal))
        throw new Exception("Auswahl, Berechnung, Familie oder ehrliche Fehlerbehandlung fehlen: " + required);
if (command.Contains("ConstructVolumeFromFacesCommand", StringComparison.Ordinal)
    || command.Contains("FamilyGeometryTools.CreateNewFamily(", StringComparison.Ordinal))
    throw new Exception("Nicht verifizierte DirectShape-/void-Familienpfade dürfen nicht als Familienerfolg gewertet werden.");
if (!command.Contains("catch (System.OperationCanceledException)", StringComparison.Ordinal))
    throw new Exception("Abbruch bei der Vorlagenauswahl muss als Cancelled und nicht als Fehler gelten.");
int validation = command.IndexOf("SurfaceEnclosureBuilder.TryBuild(", StringComparison.Ordinal);
int exactSource = command.IndexOf("SurfaceEnclosureBuilder.TryReuseCompleteSourceSolid(", StringComparison.Ordinal);
if (exactSource < 0 || exactSource >= validation)
    throw new Exception("Vollständig ausgewählte Quellkörper müssen vor einer Flächenrekonstruktion exakt übernommen werden.");
int naming = command.IndexOf("Microsoft.VisualBasic.Interaction.InputBox(", StringComparison.Ordinal);
if (validation < 0 || naming <= validation)
    throw new Exception("Vor der Familienbenennung muss die ausgewählte Geometrie geprüft werden.");
int invalidSelection = command.IndexOf("if (!SurfaceEnclosureBuilder.TryBuild(", StringComparison.Ordinal);
int familyCreation = command.IndexOf("SurfaceEnclosureFamilyBuilder.CreateFamily(", StringComparison.Ordinal);
if (invalidSelection < 0 || familyCreation < 0 || invalidSelection >= familyCreation)
    throw new Exception("Ungültige Geometrie darf keine Familie erzeugen.");
string validationBranch = command[invalidSelection..naming];
if (!validationBranch.Contains("TaskDialog.Show(", StringComparison.Ordinal)
    || !validationBranch.Contains("return Result.Cancelled;", StringComparison.Ordinal)
    || validationBranch.Contains("throw new InvalidOperationException(diagnostic)", StringComparison.Ordinal))
    throw new Exception("Abgewiesene Flächen müssen eine einzelne Diagnose ohne Revit-Fehlerdialog liefern.");
int exceptionHandler = command.IndexOf("catch (Exception error)", StringComparison.Ordinal);
if (exceptionHandler < 0 || command[exceptionHandler..].Contains("TaskDialog.Show(", StringComparison.Ordinal))
    throw new Exception("Bei unerwarteten Fehlern darf neben dem Revit-Fehlerdialog kein zweiter Dialog erscheinen.");
Console.WriteLine("Flächen-Familienfunktion: Ribbon-/Command-Verdrahtung erkannt (Revit-Laufzeit separat prüfen).");
