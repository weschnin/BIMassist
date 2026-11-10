using Autodesk.Revit.DB;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;
using System.IO;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }
var document = new Document(); var instance = new FamilyInstance { Id = new(100), TypeId = new(200) }; var symbol = new FamilySymbol { Id = new(200) };
document.Elements.AddRange([instance, symbol]);
var id = Guid.NewGuid();
var source = new ParameterSource { Kind = ParameterKind.SharedGuid, SharedGuid = id, Name = "Länge", Spec = "LENGTH", Scope = ParameterScope.Instance };
instance.Parameters.Add(new("Länge", SpecTypeId.Length, 10d, StorageType.Double, id));
symbol.Parameters.Add(new("Länge", SpecTypeId.Length, 20d, StorageType.Double, id));
Check(RevitQuantityReader.Read(document, instance, source, "m").Number == 3.048m, "Instance metres conversion wrong");
Check(RevitQuantityReader.Read(document, instance, source, "mm").Number == 3048m, "Instance millimetres conversion wrong");
Check(RevitQuantityReader.Read(document, instance, source, "km").Number == 0.003048m, "Kilometres must convert via original Revit API metres");
source.Scope = ParameterScope.Type;
Check(RevitQuantityReader.Read(document, instance, source, "m").Number == 6.096m, "Type source silently read instance");
source.SharedGuid = Guid.NewGuid(); Reject(() => RevitQuantityReader.Read(document, instance, source, "m"), "Missing GUID fell back to name");
source.SharedGuid = id; source.Spec = "AREA"; Reject(() => RevitQuantityReader.Read(document, instance, source, "m2"), "Wrong spec accepted");
source.Spec = "LENGTH"; source.Name = "Wrong name"; Reject(() => RevitQuantityReader.Read(document, instance, source, "m"), "Wrong shared name accepted");
source.Name = "Länge"; Reject(() => RevitQuantityReader.Read(document, instance, source, "psch"), "Invalid read unit accepted");
var local = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "Länge", Spec = "LENGTH", Scope = ParameterScope.Instance };
Reject(() => RevitQuantityReader.Read(document, instance, local, "m"), "Legacy local source borrowed shared instance value");
var namedLength = new ParameterSource { Kind = ParameterKind.ByName, Name = "Länge", Spec = "LENGTH", Scope = ParameterScope.Instance };
Check(RevitQuantityReader.Read(document, instance, namedLength, "m").Number == 3.048m, "Exact-name source did not read the sole shared instance value");
instance.Parameters.Add(new("Länge", SpecTypeId.Length, 2d, StorageType.Double));
Check(RevitQuantityReader.Read(document, instance, local, "m").Number == 0.6096m, "Legacy local source lost its value to same-name shared parameter");
Reject(() => RevitQuantityReader.Read(document, instance, namedLength, "m"), "Same-name local/shared instances were resolved arbitrarily");
instance.Parameters.RemoveAll(p => p.Definition.Name == "Länge" && p.IsShared);
Check(RevitQuantityReader.Read(document, instance, local, "m").Number == 0.6096m, "Exact-name local family read wrong");
instance.Parameters.Add(new("Länge", SpecTypeId.Length, 3d, StorageType.Double));
Reject(() => RevitQuantityReader.Read(document, instance, local, "m"), "Ambiguous local parameters accepted");
var system = new Element { Id = new(300), TypeId = new(200) };
Reject(() => RevitQuantityReader.Read(document, system, local, "m"), "Missing named source accepted system instance");
system.Parameters.Add(new("Länge", SpecTypeId.Length, 1d, StorageType.Double));
Reject(() => RevitQuantityReader.Read(document, system, local, "m"), "Legacy local source accepted system instance");
Check(RevitQuantityReader.Read(document, system, namedLength, "m").Number == 0.3048m, "Exact name on system instance was not read");
var byNameVolume = new ParameterSource { Kind = ParameterKind.ByName, Name = "Volumen", Spec = "VOLUME", Scope = ParameterScope.Instance };
Check(byNameVolume.ToString().Contains("Name", StringComparison.Ordinal) && !byNameVolume.ToString().Contains("lokale Familie", StringComparison.Ordinal),
    "Existing template still describes its named source as strictly local");
instance.BuiltIns[BuiltInParameter.HOST_VOLUME_COMPUTED] = new("Volumen", SpecTypeId.Volume, 100d, StorageType.Double) { Id = new((long)BuiltInParameter.HOST_VOLUME_COMPUTED) };
Check(RevitQuantityReader.Read(document, instance, byNameVolume, "m3").Number == 2.8316846592m,
    "Exact-name VOLUME did not read a single built-in instance value");
instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 200d, StorageType.Double));
try { RevitQuantityReader.Read(document, instance, byNameVolume, "m3"); throw new Exception("Built-in/local duplicate volume was resolved arbitrarily"); }
catch (InvalidDataException ex)
{
    Check(ex.Message.Contains("HOST_VOLUME_COMPUTED", StringComparison.Ordinal)
        && ex.Message.Contains("2.8316846592", StringComparison.Ordinal)
        && ex.Message.Contains("5.6633693184", StringComparison.Ordinal)
        && ex.Message.Contains("ID 10", StringComparison.Ordinal), "Ambiguous source does not reveal both identities and model values");
}
instance.Parameters.RemoveAll(p => p.Definition.Name == "Volumen");
instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, double.NaN, StorageType.Double));
try { RevitQuantityReader.Read(document, instance, byNameVolume, "m3"); throw new Exception("Nonfinite duplicate volume accepted"); }
catch (InvalidDataException ex) { Check(ex.Message.Contains("HOST_VOLUME_COMPUTED") && ex.Message.Contains("nicht endlich"), "Nonfinite duplicate escaped ambiguity report"); }
instance.Parameters.RemoveAll(p => p.Definition.Name == "Volumen");
instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 1e-12, StorageType.Double));
try { RevitQuantityReader.Read(document, instance, byNameVolume, "m3"); throw new Exception("Tiny duplicate volume accepted"); }
catch (InvalidDataException ex) { Check(ex.Message.Contains("2.8316846592") && ex.Message.Contains("2.8316846592E-14"), "Tiny candidate rounded into an indistinguishable zero"); }
instance.Parameters.RemoveAll(p => p.Definition.Name == "Volumen");
instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 200d, StorageType.Double));
var volumeBuiltIn = new ParameterSource { Kind = ParameterKind.BuiltIn, Name = "Volumen", BuiltInId = "HOST_VOLUME_COMPUTED", Spec = "VOLUME", Scope = ParameterScope.Instance };
Check(RevitQuantityReader.Read(document, instance, volumeBuiltIn, "m3").Number == 2.8316846592m,
    "Explicit built-in volume was not read while a same-name local volume coexists");
instance.Parameters.RemoveAll(p => p.Definition.Name == "Volumen");
instance.Parameters.Add(new("Volumen", SpecTypeId.String.Text, "other", StorageType.String));
Check(RevitQuantityReader.Read(document, instance, byNameVolume, "m3").Number == 2.8316846592m,
    "Wrong-spec same-name parameter blocked the sole VOLUME source");
instance.BuiltIns.Remove(BuiltInParameter.HOST_VOLUME_COMPUTED);
Reject(() => RevitQuantityReader.Read(document, instance, byNameVolume, "m3"), "Wrong-spec name match supplied a volume");
instance.Parameters.RemoveAll(p => p.Definition.Name == "Volumen");
try { RevitQuantityReader.Read(document, instance, byNameVolume, "m3"); throw new Exception("Missing named volume accepted"); }
catch (InvalidDataException ex) { Check(!ex.Message.Contains("Namens-", StringComparison.Ordinal), "Name-based lookup reported no name fallback"); }
var text = new ParameterSource { Kind = ParameterKind.BuiltIn, BuiltInId = "ASSEMBLY_CODE", Name = "BGK", Spec = "TEXT", Scope = ParameterScope.Type };
Check(RevitQuantityReader.Read(document, instance, text, "", true).Text == "", "Missing identification must be unmarked, not fabricated");
Reject(() => RevitQuantityReader.Read(document, instance, text, ""), "Missing required text must not compare as empty");
symbol.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("Assembly code", SpecTypeId.String.Text, "01-10", StorageType.String);
Check(RevitQuantityReader.Read(document, instance, text, "").Text == "01-10", "Identification changed leading zeros");
var number = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "Number", Spec = "NUMBER", Scope = ParameterScope.Instance };
instance.Parameters.Add(new("Number", SpecTypeId.Number, double.NaN, StorageType.Double));
Reject(() => RevitQuantityReader.Read(document, instance, number, "1"), "NaN accepted");
instance.Parameters.RemoveAll(p => p.Definition.Name == "Number"); instance.Parameters.Add(new("Number", SpecTypeId.Number, double.MaxValue, StorageType.Double));
Reject(() => RevitQuantityReader.Read(document, instance, number, "1"), "Decimal overflow accepted");
instance.Parameters.RemoveAll(p => p.Definition.Name == "Number"); instance.Parameters.Add(new("Number", SpecTypeId.Number, 2.5d, StorageType.Double));
Check(RevitQuantityReader.Read(document, instance, number, "St").Number == 2.5m, "Dimensionless numeric parameter was reinterpreted");
instance.Parameters.Add(new("Gewerk", SpecTypeId.String.Text, "Rohbau", StorageType.String));
symbol.Parameters.Add(new("Baubereich", SpecTypeId.String.Text, "Erdgeschoss", StorageType.String));
var groupingOptions = RevitQuantityReader.GetGroupingParameterOptions(document);
Check(groupingOptions.Contains(new("Gewerk", ParameterScope.Instance)), "Instance text grouping parameter was not offered");
Check(groupingOptions.Contains(new("Baubereich", ParameterScope.Type)), "Type text grouping parameter was not offered");
Check(!groupingOptions.Any(x => x.Name == "Number"), "Numeric parameter was offered for text grouping");
Check(RevitQuantityReader.ReadGroupingLabels(document, ["100"], new("Gewerk", ParameterScope.Instance))["100"] == "Rohbau", "Instance grouping label changed");
Check(RevitQuantityReader.ReadGroupingLabels(document, ["100"], new("Baubereich", ParameterScope.Type))["100"] == "Erdgeschoss", "Type grouping label changed");
Reject(() => RevitQuantityReader.ReadGroupingLabels(document, ["100"], new("Absent", ParameterScope.Instance)), "Missing group label was guessed");
instance.Parameters.Add(new("BlankGroup", SpecTypeId.String.Text, " ", StorageType.String));
Reject(() => RevitQuantityReader.ReadGroupingLabels(document, ["100"], new("BlankGroup", ParameterScope.Instance)), "Blank group label was silently accepted");
var boolean = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "Flag", Spec = "YESNO", Scope = ParameterScope.Instance };
instance.Parameters.Add(new("Flag", SpecTypeId.Boolean.YesNo, 1, StorageType.Integer));
Check(RevitQuantityReader.Read(document, instance, boolean, "").Boolean == true, "YESNO read wrong");
var template = new AvaTemplate(); document.Elements.Add(new Element { Id = new(400), Category = new() { CategoryType = CategoryType.Annotation } }); document.Elements.Add(system);
Check(RevitQuantityReader.Elements(document, template).Select(e => e.Id).SequenceEqual(new[] { "100", "300" }), "Host model scope includes annotations or types");
// Exact-name resolution does not depend on definition IDs; legacy local resolution still does.
var zero = new ParameterSource { Kind = ParameterKind.ByName, Name = "Zero", Spec = "LENGTH", Scope = ParameterScope.Instance };
instance.Parameters.Add(new("Zero", SpecTypeId.Length, 1d, StorageType.Double) { Id = new(0) });
Check(RevitQuantityReader.Read(document, instance, zero, "m").Number == 0.3048m, "Name resolution rejected zero definition ID");
Reject(() => RevitQuantityReader.Read(document, instance, new() { Kind = ParameterKind.LocalFamily, Name = "Zero", Spec = "LENGTH", Scope = ParameterScope.Instance }, "m"), "Legacy local source accepted zero ID");
// Explicit scopes never borrow a same-name value from the opposite owner.
var scoped = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "OnlyInstance", Spec = "LENGTH", Scope = ParameterScope.Type };
instance.Parameters.Add(new("OnlyInstance", SpecTypeId.Length, 1d, StorageType.Double));
Reject(() => RevitQuantityReader.Read(document, instance, scoped, "m"), "Missing type parameter fell back to instance");
scoped.Name = "OnlyType"; scoped.Scope = ParameterScope.Instance;
symbol.Parameters.Add(new("OnlyType", SpecTypeId.Length, 2d, StorageType.Double));
Reject(() => RevitQuantityReader.Read(document, instance, scoped, "m"), "Missing instance parameter fell back to type");
var invalidBuiltin = new ParameterSource { Kind = ParameterKind.BuiltIn, Name = "OnlyInstance", BuiltInId = "INVALID", Spec = "LENGTH", Scope = ParameterScope.Instance };
Reject(() => RevitQuantityReader.Read(document, instance, invalidBuiltin, "m"), "Invalid built-in fell back to display name");
invalidBuiltin.BuiltInId = "ASSEMBLY_CODE";
Reject(() => RevitQuantityReader.Read(document, instance, invalidBuiltin, "m"), "Absent built-in fell back to local display name");
instance.Parameters.Add(new("Negative", SpecTypeId.Length, 1d, StorageType.Double) { Id = new(-2) });
Check(RevitQuantityReader.Read(document, instance, new() { Kind = ParameterKind.ByName, Name = "Negative", Spec = "LENGTH", Scope = ParameterScope.Instance }, "m").Number == 0.3048m,
    "Name resolution rejected negative definition ID");
// Numeric identity remains exact Int64; document/UniqueId are separate export provenance.
instance.Id = new(4294967296); instance.UniqueId = "unique-100";
document.PathName = @"C:\models\host.rvt";
var readElement = RevitQuantityReader.Elements(document, template).First();
Check(readElement.Id == "4294967296", "Int64 element identifier changed");
Check(readElement.SourceIdentity == @"Dokument: C:\models\host.rvt; UniqueId: unique-100", "Saved document provenance missing");
document.PathName = ""; document.Title = "Ungespeichertes Projekt";
Check(RevitQuantityReader.Elements(document, template).First().SourceIdentity == "Dokument: Ungespeichertes Projekt; UniqueId: unique-100", "Unsaved document title fallback missing");
var absenceFailures = 0;
void AbsenceTest(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { absenceFailures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
AbsenceTest("valueless identification with wrong spec is fatal, not unmarked", () =>
{
    symbol.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.Length, 0d, StorageType.Double) { HasValue = false };
    Reject(() => RevitQuantityReader.Read(document, instance, text, "", true), "HasValue=false bypassed TEXT spec validation");
});
AbsenceTest("valueless identification with wrong storage is fatal, not unmarked", () =>
{
    symbol.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, 0d, StorageType.Double) { HasValue = false };
    Reject(() => RevitQuantityReader.Read(document, instance, text, "", true), "HasValue=false bypassed TEXT storage validation");
});
AbsenceTest("only correctly typed valueless identification is unmarked; required text remains fatal", () =>
{
    symbol.BuiltIns[BuiltInParameter.ASSEMBLY_CODE] = new("BGK", SpecTypeId.String.Text, null, StorageType.String);
    Check(RevitQuantityReader.Read(document, instance, text, "", true).Text == "", "Correctly typed missing identification must be empty");
    Reject(() => RevitQuantityReader.Read(document, instance, text, ""), "Required missing text accepted");
    Reject(() => RevitQuantityReader.Read(document, instance, text, "m", true), "Valueless TEXT accepted an invalid unit");
});
AbsenceTest("missing numeric quantity or condition stays fatal even with allowMissingText", () =>
{
    instance.Parameters.Add(new("AbsentNumber", SpecTypeId.Number, 0d, StorageType.Double) { HasValue = false });
    var absent = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "AbsentNumber", Spec = "NUMBER", Scope = ParameterScope.Instance };
    Reject(() => RevitQuantityReader.Read(document, instance, absent, "1", true), "Missing numeric parameter accepted");
});
AbsenceTest("missing instance VOLUME does not fall back to a same-name type value", () =>
{
    symbol.Parameters.Add(new("Volumen", SpecTypeId.Volume, 12d, StorageType.Double));
    var absent = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "Volumen", Spec = "VOLUME", Scope = ParameterScope.Instance };
    Reject(() => RevitQuantityReader.Read(document, instance, absent, "m3"), "Missing instance VOLUME fell back to the same-name type parameter");
});
AbsenceTest("valueless instance VOLUME stays fatal despite a same-name type value", () =>
{
    instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 0d, StorageType.Double) { HasValue = false });
    var absent = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "Volumen", Spec = "VOLUME", Scope = ParameterScope.Instance };
    Reject(() => RevitQuantityReader.Read(document, instance, absent, "m3"), "Valueless instance VOLUME fell back to the same-name type parameter");
});
AbsenceTest("host scope excludes model-category Revit and linked CAD containers but keeps imported copies", () =>
{
    var host = new Document();
    host.Elements.AddRange([new RevitLinkInstance { Id = new(501) }, new ImportInstance { Id = new(502), IsLinked = true },
        new ImportInstance { Id = new(503), IsLinked = false }, new Element { Id = new(504) }]);
    Check(RevitQuantityReader.Elements(host, template).Select(e => e.Id).SequenceEqual(new[] { "503", "504" }),
        "Linked container counted as a host model element or imported copy wrongly excluded");
});
if (absenceFailures != 0) Environment.ExitCode = 1;
Console.WriteLine($"Reader absence/scope regressions: {7 - absenceFailures} passed; {absenceFailures} failed");
Console.WriteLine("PASS Revit adapter policy with API doubles: scope, strict GUID and unique exact-name resolution, typed input units, absence, ambiguity and nonfinite/overflow guards (not a Revit-host test)");
