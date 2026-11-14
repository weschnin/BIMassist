using Autodesk.Revit.DB;
using BIMassist.Ava.Core;
using BIMassist.Ava.Revit;
using System.IO;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }
var document = new Document(); var instance = new FamilyInstance { Id = new(100), TypeId = new(200) }; var symbol = new FamilySymbol { Id = new(200) };
document.Elements.AddRange([instance, symbol]);
var id = Guid.NewGuid();
// A missing source must reveal evidence, without borrowing the opposite scope's value.
var dnGuid = Guid.Parse("83af7484-32f1-4f7d-9b79-eab347958b56");
var dnSource = new ParameterSource { Kind = ParameterKind.SharedGuid, SharedGuid = dnGuid,
    Name = "AVA_DN", Spec = "LENGTH", Scope = ParameterScope.Type };
instance.Parameters.Add(new("AVA_DN", SpecTypeId.Length, 625d / 304.8d, StorageType.Double, dnGuid));
try { RevitQuantityReader.Read(document, instance, dnSource, "mm"); throw new Exception("Missing type DN borrowed the instance value"); }
catch (InvalidDataException ex)
{
    Check(ex.Message.Contains("Parameter ist im gewählten Geltungsbereich nicht vorhanden")
        && ex.Message.Contains("Instanz: Gemeinsam GUID " + dnGuid)
        && ex.Message.Contains("625 mm") && ex.Message.Contains("Typ: kein gleichnamiger Parameter")
        && ex.Message.Contains("nur Diagnose"), "Missing DN does not explain the scope mismatch with exact identity and unit value");
}
symbol.Parameters.Add(new("AVA_DN", SpecTypeId.Length, 0d, StorageType.Double, dnGuid) { HasValue = false });
try { RevitQuantityReader.Read(document, instance, dnSource, "mm"); throw new Exception("Empty type DN borrowed the instance value"); }
catch (InvalidDataException ex)
{
    Check(ex.Message.Contains("Parameter ist vorhanden, enthält aber keinen Wert") && ex.Message.Contains("kein Wert")
        && ex.Message.Contains("625 mm"), "Empty DN is not distinguished from an absent parameter");
}
instance.Parameters.RemoveAll(p => p.Definition.Name == "AVA_DN");
symbol.Parameters.RemoveAll(p => p.Definition.Name == "AVA_DN");
var choiceSettings = Path.Combine(Path.GetTempPath(), "BIMassist-AvaParameterChoices-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    var choiceKey = AvaParameterChoiceStore.Key(@"C:\models\host.rvt", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "type-uid", new ParameterSource { Kind = ParameterKind.ByName, Name = "Volumen", Spec = "VOLUME", Scope = ParameterScope.Instance });
    new AvaParameterChoiceStore(choiceSettings).Remember(choiceKey, "BuiltIn:HOST_VOLUME_COMPUTED");
    var reopenedChoices = new AvaParameterChoiceStore(choiceSettings);
    Check(reopenedChoices.Find(choiceKey) == "BuiltIn:HOST_VOLUME_COMPUTED",
        "Remembered parameter choice did not persist across store instances");
    var available = new[] { new AvaParameterCandidate("BuiltIn:HOST_VOLUME_COMPUTED", "BuiltIn volume", new(Number: 1m)) };
    Check(reopenedChoices.FindAvailable(choiceKey, available) == "BuiltIn:HOST_VOLUME_COMPUTED"
        && reopenedChoices.FindAvailable(choiceKey, [new("Id:42", "Replacement", new(Number: 1m))]) == null,
        "Remembered choices did not validate against the current parameter identities");
    Check(choiceKey != AvaParameterChoiceStore.Key(@"C:\models\other.rvt", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "type-uid", new ParameterSource { Kind = ParameterKind.ByName, Name = "Volumen", Spec = "VOLUME", Scope = ParameterScope.Instance }),
        "Parameter choice key is not bound to the active project");
}
finally { if (File.Exists(choiceSettings)) File.Delete(choiceSettings); }
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
Check(!byNameVolume.ToString().Contains("eindeutiger Treffer", StringComparison.Ordinal),
    "By-name source description claimed uniqueness before model-level candidates were checked");
instance.BuiltIns[BuiltInParameter.HOST_VOLUME_COMPUTED] = new("Volumen", SpecTypeId.Volume, 100d, StorageType.Double) { Id = new((long)BuiltInParameter.HOST_VOLUME_COMPUTED) };
Check(RevitQuantityReader.Read(document, instance, byNameVolume, "m3").Number == 2.8316846592m,
    "Exact-name VOLUME did not read a single built-in instance value");
instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 100d, StorageType.Double));
Check(RevitQuantityReader.Read(document, instance, byNameVolume, "m3").Number == 2.8316846592m,
    "Equal same-name values were not accepted without prompting");
instance.Parameters.RemoveAll(p => p.Definition.Name == "Volumen");
instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, 200d, StorageType.Double));
var promptedCandidates = 0;
var chosenValue = RevitQuantityReader.Read(document, instance, byNameVolume, "m3", false, candidates =>
{
    promptedCandidates = candidates.Count;
    Check(candidates.Select(candidate => candidate.Value.Number).SequenceEqual(new decimal?[] { 5.6633693184m, 2.8316846592m }),
        "Prompt candidate values were not converted to the requested unit");
    return candidates[1].Identity;
});
Check(promptedCandidates == 2 && chosenValue.Number == 2.8316846592m,
    "Differing duplicate values were not passed to the user's selected candidate");
try { RevitQuantityReader.Read(document, instance, byNameVolume, "m3"); throw new Exception("Differing duplicate volumes were resolved without a user choice"); }
catch (InvalidDataException ex)
{
    Check(ex.Message.Contains("unterschiedliche Werte", StringComparison.Ordinal)
        && ex.Message.Contains("HOST_VOLUME_COMPUTED", StringComparison.Ordinal)
        && ex.Message.Contains("2.8316846592", StringComparison.Ordinal)
        && ex.Message.Contains("5.6633693184", StringComparison.Ordinal)
        && ex.Message.Contains("ID 10", StringComparison.Ordinal), "Ambiguous source does not reveal both identities and model values");
}
instance.Parameters.RemoveAll(p => p.Definition.Name == "Volumen");
instance.Parameters.Add(new("Volumen", SpecTypeId.Volume, double.NaN, StorageType.Double));
try { RevitQuantityReader.Read(document, instance, byNameVolume, "m3"); throw new Exception("Nonfinite duplicate volume accepted"); }
catch (InvalidDataException ex) { Check(ex.Message.Contains("Parameterwert ist nicht endlich", StringComparison.Ordinal), "Nonfinite duplicate was not rejected as an invalid quantity"); }
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
instance.Parameters.Add(new("Number", SpecTypeId.Number, 4.5d, StorageType.Double));
Check(RevitQuantityReader.Read(document, instance, number, "1").Number == 4.5m, "unitless numeric parameter changed during raw-value read");
instance.Parameters.RemoveAll(p => p.Definition.Name == "Number");
var integer = new ParameterSource { Kind = ParameterKind.LocalFamily, Name = "Days", Spec = "INTEGER", Scope = ParameterScope.Instance };
instance.Parameters.Add(new("Days", SpecTypeId.Int.Integer, 12, StorageType.Integer));
Check(RevitQuantityReader.Read(document, instance, integer, "1").Number == 12m, "unitless integer parameter changed during raw-value read");
Reject(() => RevitQuantityReader.Read(document, instance, integer, "d"), "reader accepted custom duration unit instead of explicit unitless raw read");
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
symbol.Family.Name = "Rohrfamilie"; symbol.Name = "DN 20";
Check(RevitQuantityReader.ReadFamilyAndTypeLabels(document, ["100"])["100"] == "Rohrfamilie — DN 20",
    "Family/type group label did not preserve the exact Revit family and type names");
var wallInstance = new FamilyInstance { Id = new(401), TypeId = new(201), Category = new() { Name = "Wände" } };
var wallType = new ElementType { Id = new(201), Name = "Grundwand" };
var wallDocument = new Document(); wallDocument.Elements.AddRange([wallInstance, wallType]);
Check(RevitQuantityReader.ReadFamilyAndTypeLabels(wallDocument, ["401"])["401"] == "Wände — Grundwand",
    "Family/type grouping must support model element types without a FamilySymbol");
var escapedDocument = new Document();
var escapedPairs = new[]
{
    ("A — B", "C"),
    ("A", "B — C"),
    ("A\\—B", "C"),
    ("A", "B\\—C")
};
var escapedInstances = escapedPairs.Select((pair, index) => new FamilyInstance { Id = new(410 + index), TypeId = new(510 + index) }).ToArray();
for (var index = 0; index < escapedPairs.Length; index++)
{
    escapedInstances[index].Category = new() { Name = escapedPairs[index].Item1 };
    escapedDocument.Elements.Add(escapedInstances[index]);
    escapedDocument.Elements.Add(new ElementType { Id = new(510 + index), Name = escapedPairs[index].Item2 });
}
var escapedLabels = RevitQuantityReader.ReadFamilyAndTypeLabels(escapedDocument, escapedInstances.Select(x => x.Id.Value.ToString())).Values.ToArray();
Check(escapedLabels.Distinct(StringComparer.Ordinal).Count() == escapedPairs.Length,
    "Escaping of backslashes and em dashes did not keep distinct family/type pairs unique");
var untyped = new FamilyInstance { Id = new(402), TypeId = new(999), Name = "Freies Element", Category = new() { Name = "Modell" } };
var untypedDocument = new Document(); untypedDocument.Elements.Add(untyped);
Check(RevitQuantityReader.ReadFamilyAndTypeLabels(untypedDocument, ["402"])["402"] == "Modell — Freies Element",
    "Family/type grouping must fall back to the model element name when Revit exposes no ElementType");
var collisionType = new FamilySymbol { Id = new(202), Family = new() { Name = "A — B" }, Name = "C" };
var collisionInstance = new FamilyInstance { Id = new(403), TypeId = collisionType.Id, Category = new() { CategoryType = CategoryType.Model } };
var collisionType2 = new FamilySymbol { Id = new(203), Family = new() { Name = "A" }, Name = "B — C" };
var collisionInstance2 = new FamilyInstance { Id = new(404), TypeId = collisionType2.Id, Category = new() { CategoryType = CategoryType.Model } };
var collisionDocument = new Document(); collisionDocument.Elements.AddRange([collisionInstance, collisionType, collisionInstance2, collisionType2]);
var collisionLabels = RevitQuantityReader.ReadFamilyAndTypeLabels(collisionDocument, ["403", "404"]);
Check(collisionLabels["403"] != collisionLabels["404"], "Distinct family/type pairs collided in the flattened group label");
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
var absenceRuns = 0;
void AbsenceTest(string name, Action action)
{
    absenceRuns++;
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
AbsenceTest("missing shared DN reports a same-name different GUID but never uses it", () =>
{
    var host = new Document();
    var item = new FamilyInstance { Id = new(67941), TypeId = new(70000) };
    var itemType = new FamilySymbol { Id = item.TypeId };
    host.Elements.AddRange([item, itemType]);
    var otherGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    itemType.Parameters.Add(new("AVA_DN", SpecTypeId.Length, 625d / 304.8d, StorageType.Double, otherGuid));
    try { RevitQuantityReader.Read(host, item, dnSource, "mm"); throw new Exception("Different GUID was used"); }
    catch (InvalidDataException ex)
    {
        Check(ex.Message.Contains(otherGuid.ToString()) && ex.Message.Contains("625 mm")
            && ex.Message.Contains("nicht vorhanden"), "Diagnostic hid the different shared identity");
    }
});
AbsenceTest("nonfinite opposite-scope DN does not hide the original missing-source error", () =>
{
    var host = new Document();
    var item = new FamilyInstance { Id = new(67941), TypeId = new(70000) };
    host.Elements.AddRange([item, new FamilySymbol { Id = item.TypeId }]);
    item.Parameters.Add(new("AVA_DN", SpecTypeId.Length, double.NaN, StorageType.Double, dnGuid));
    try { RevitQuantityReader.Read(host, item, dnSource, "mm"); throw new Exception("Nonfinite DN accepted"); }
    catch (InvalidDataException ex)
    {
        Check(ex.Message.Contains("nicht vorhanden") && ex.Message.Contains("Parameterwert ist nicht endlich"),
            "Diagnostic replaced the original error or tried to convert nonfinite DN");
    }
});
AbsenceTest("same-name diagnostics cap candidates per scope and disclose omitted count", () =>
{
    var host = new Document();
    var item = new FamilyInstance { Id = new(67941), TypeId = new(70000) };
    host.Elements.AddRange([item, new FamilySymbol { Id = item.TypeId }]);
    for (var index = 0; index < 8; index++)
        item.Parameters.Add(new("AVA_DN", SpecTypeId.Length, 1d, StorageType.Double) { Id = new(1000 + index) });
    try { RevitQuantityReader.Read(host, item, dnSource, "mm"); throw new Exception("Missing type DN accepted"); }
    catch (InvalidDataException ex)
    {
        Check(ex.Message.Contains("weitere 2 gleichnamige Parameter") && ex.Message.Contains("Parameter-ID 1005")
            && !ex.Message.Contains("Parameter-ID 1006"), "Diagnostic bound or omission count is wrong");
    }
});
AbsenceTest("missing volume never describes a same-name length as a cubic-metre value", () =>
{
    var host = new Document();
    var item = new FamilyInstance { Id = new(67941), TypeId = new(70000) };
    var itemType = new FamilySymbol { Id = item.TypeId };
    host.Elements.AddRange([item, itemType]);
    itemType.Parameters.Add(new("Mixed", SpecTypeId.Length, 1d, StorageType.Double));
    var volume = new ParameterSource { Kind = ParameterKind.SharedGuid, SharedGuid = dnGuid,
        Name = "Mixed", Spec = "VOLUME", Scope = ParameterScope.Type };
    try { RevitQuantityReader.Read(host, item, volume, "m3"); throw new Exception("Missing volume accepted a length"); }
    catch (InvalidDataException ex)
    {
        Check(ex.Message.Contains("nicht vorhanden") && ex.Message.Contains("Parameterspezifikation")
            && !ex.Message.Contains("m3"), "Diagnostic fabricated a volume from a LENGTH parameter: " + ex.Message);
    }
});
if (absenceFailures != 0) Environment.ExitCode = 1;
Console.WriteLine($"Reader absence/scope regressions: {absenceRuns - absenceFailures} passed; {absenceFailures} failed");
Console.WriteLine("PASS Revit adapter policy with API doubles: scope, strict GUID and unique exact-name resolution, typed input units, absence, ambiguity and nonfinite/overflow guards (not a Revit-host test)");
