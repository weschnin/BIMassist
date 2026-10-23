using System.Text.RegularExpressions;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string ribbon = File.ReadAllText(Path.Combine(root, "App.cs"));
string existingName = "BIMassist.Commands.SolidsToNewFamilyCommand";
string newName = "BIMassist.Commands.SolidsToVoidFamilyCommand";
string intersectionName = "BIMassist.Commands.SolidsIntersectionToNewFamilyCommand";
int first = ribbon.IndexOf(existingName, StringComparison.Ordinal);
int second = ribbon.IndexOf(newName, StringComparison.Ordinal);
int third = ribbon.IndexOf(intersectionName, StringComparison.Ordinal);
if (first < 0 || second <= first || third <= second)
    throw new Exception("The void-family command must be second in the Geometrien pulldown.");
string command = File.ReadAllText(Path.Combine(root, "Commands", "SolidsToVoidFamilyCommand.cs"));
if (!command.Contains("GetAllGeometryObjects(", StringComparison.Ordinal)
    || !command.Contains("ObjectType.Element", StringComparison.Ordinal)
    || !command.Contains("CreateVoidFamily(", StringComparison.Ordinal)
    || !command.Contains("Result.Failed", StringComparison.Ordinal)
    || !command.Contains("Result.Cancelled", StringComparison.Ordinal))
    throw new Exception("Selection, geometry extraction, cancellation and honest failure must match the existing geometry command.");
if (!command.Contains("foreach (ElementId elementId in selected", StringComparison.Ordinal)
    || !command.Contains("new Options { DetailLevel = ViewDetailLevel.Fine }", StringComparison.Ordinal)
    || !command.Contains("item is Mesh", StringComparison.Ordinal)
    || !command.Contains("item is not Solid", StringComparison.Ordinal))
    throw new Exception("Read every selected element without view filtering; reject meshes and incomplete solids rather than silently omitting geometry.");
string builder = File.ReadAllText(Path.Combine(root, "Services", "VoidGeometryFamilyBuilder.cs"));
foreach (string required in new[] { "ELEMENT_IS_CUTTING", "FAMILY_ALLOW_CUT_WITH_VOIDS",
    "IsSolid", "IsVoidInstanceCuttingElement", "SaveAs(", "OpenDocumentFile(",
    "LoadFamily(project,", "TransactionGroup", "RollBack(", "MinVoidVolume" })
    if (!builder.Contains(required, StringComparison.Ordinal))
        throw new Exception($"Missing void-family safety check: {required}");
if (!Regex.IsMatch(builder, @"\.Any\(.*is not Solid", RegexOptions.Singleline))
    throw new Exception("Non-solid mesh fallback must not be reported as a usable cutting void.");
int placement = builder.IndexOf("project.Create.NewFamilyInstance(", StringComparison.Ordinal);
int afterPlacementRegen = builder.IndexOf("project.Regenerate();", placement, StringComparison.Ordinal);
int instanceCutCheck = builder.IndexOf("InstanceVoidCutUtils.IsVoidInstanceCuttingElement(instance)", placement, StringComparison.Ordinal);
if (placement < 0 || afterPlacementRegen <= placement || instanceCutCheck <= afterPlacementRegen)
    throw new Exception("After placing the void instance, regenerate before checking its cutting eligibility.");
Console.WriteLine("Void-Familie: statische Integrationsprüfungen bestanden (Revit-Laufzeit separat prüfen).");
