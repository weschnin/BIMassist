using System.Text.RegularExpressions;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string path = Path.Combine(root, "Services", "SurfaceEnclosureFamilyBuilder.cs");
Check(File.Exists(path), "Positive-solid family builder is missing.");
string source = File.ReadAllText(path);
void Has(string needle) => Check(source.Contains(needle, StringComparison.Ordinal), $"Missing safety wiring: {needle}");
void Before(string first, string second)
{
    int a = source.IndexOf(first, StringComparison.Ordinal);
    int b = source.IndexOf(second, StringComparison.Ordinal);
    Check(a >= 0 && b > a, $"Expected {first} before {second}.");
}
static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
}

Has("CreateFamily(UIApplication uiapp, Document project,");
Check(Regex.IsMatch(source, @"Solid\??\s+solid,\s*string\s+familyName"), "Builder must accept one solid and a family name.");
Has("project.IsFamilyDocument");
Has("project.IsReadOnly");
Has("project.IsModifiable");
Has("solid.Volume <= 0");
Has("Path.GetInvalidFileNameChars()");
Has("FilteredElementCollector(project)");
Has("PfadVorlageAllgemeineFamilie");
Has("picker.ShowDialog() != true");
Has("throw new System.OperationCanceledException(\"Vorlagenauswahl abgebrochen");
Before("project.IsFamilyDocument", "NewFamilyDocument(");
Before("solid.Volume", "NewFamilyDocument(");
Before("picker.ShowDialog() != true", "NewFamilyDocument(");
Has("BuiltInCategory.OST_GenericModel");
Has("FreeFormElement.Create(familyDoc, solid)");
Has("form.IsSolid");
Has("forms.Count != 1");
Has("form.get_Geometry(");
Has("item.Volume > 0");
Has("SaveAs(path)");
Has("OpenDocumentFile(path)");
Has("LoadFamily(project,");
Has("project.EditFamily(loaded)");
Has("TransactionGroup(project,");
Has("group.RollBack()");
Has("group.Assimilate()");
Has("project.Create.NewFamilyInstance(");
Has("familyDoc.Close(false)");
Has("Directory.Delete(folder, true)");
Check(!source.Contains("FamilyGeometryTools.CreateNewFamily", StringComparison.Ordinal), "Do not call the silently-cancellable family helper.");
Check(!source.Contains("ELEMENT_IS_CUTTING", StringComparison.Ordinal)
    && !source.Contains("FAMILY_ALLOW_CUT_WITH_VOIDS", StringComparison.Ordinal), "Positive solids must not set void/cutting parameters.");
int verify = source.IndexOf("VerifySolidFamily(familyDoc, solid.Volume, solid.SurfaceArea, expectedLoopCounts)", StringComparison.Ordinal);
int save = source.IndexOf("SaveAs(path)", StringComparison.Ordinal);
int reopen = source.IndexOf("OpenDocumentFile(path)", StringComparison.Ordinal);
int load = source.IndexOf("LoadFamily(project,", StringComparison.Ordinal);
int edit = source.IndexOf("project.EditFamily(loaded)", StringComparison.Ordinal);
int placement = source.IndexOf("project.Create.NewFamilyInstance(", StringComparison.Ordinal);
Check(verify >= 0 && verify < save && reopen < load && edit < placement,
    "Verify initial geometry and saved family before load, then inspect project family before placement.");
Check(Regex.Matches(source, @"VerifySolidFamily\(familyDoc,\s*solid.Volume,\s*solid.SurfaceArea,\s*expectedLoopCounts\)").Count >= 3,
    "Check positive geometry in original, reopened, and loaded family documents.");
Has("expectedVolume");
Has("expectedArea");
Has("SolidUtils.ComputeIsGeometricallyClosed(");
Check(Regex.Matches(source, @"VerifySolidFamily\(familyDoc,\s*solid.Volume,\s*solid.SurfaceArea,\s*expectedLoopCounts\)").Count >= 3,
    "Check that the original, reopened and loaded family retain the input solid's volume and surface area.");
Has("GetEdgesAsCurveLoops().Count");
Has("expectedLoopCounts");
Check(Regex.IsMatch(source, @"SequenceEqual\(expectedLoopCounts\)"),
    "Every loaded family face must retain the source solid's boundary-loop topology (including its opening).");
Console.WriteLine("Surface enclosure: static lifecycle and cancellation checks passed (Revit runtime not exercised).");
