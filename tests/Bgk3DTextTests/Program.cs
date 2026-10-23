using BIMassist.Core;
using System.Xml.Linq;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}

static void Reject(string key, string bgkName, string typeNumber, string typeName)
{
    if (Bgk3DTextSpec.TryCreate(key, bgkName, typeNumber, typeName, out _, out _))
        throw new Exception("Incomplete selection must be rejected.");
}

if (!Bgk3DTextSpec.TryCreate("KG-01", "Außenwand", "42", "WD-01", out var spec, out var error))
    throw new Exception(error);
if (spec is null) throw new Exception("No specification created.");
Equal("KG-01 Außenwand", spec.FamilyName);
Equal("42 WD-01", spec.TypeName);
Equal("KG-01 Außenwand\n42 WD-01", spec.Text);
Equal("KG-01 Außenwand", spec.AssemblyCode);
Equal("42", spec.TypeComments);
Equal("WD-01", spec.Description);
if (!Bgk3DTextSpec.TryCreate("KG-01", "Außenwand", "43", "WD-02", out var other, out error))
    throw new Exception(error);
if (!Bgk3DTextBatch.TryCreate(new[] { spec, other! }, out var batch, out error))
    throw new Exception(error);
Equal(2, batch!.Types.Count);
Equal("42 WD-01", batch.PlacementTypeName);
Equal("43 WD-02", string.Join(",", batch.MissingFrom(new[] { "42 WD-01" })));
Equal("43 WD-02", string.Join(",", batch.MissingFrom(new[] { "42 wd-01" })));
Equal("Vorlage", string.Join(",", Bgk3DTextBatch.ObsoleteTemplateTypes(
    new[] { "42 wd-01", "Vorlage" }, new[] { "42 WD-01" })));
Equal(0, batch.MissingFrom(new[] { "42 WD-01", "43 WD-02" }).Count);
Equal(true, batch.IsVisibleFor("42 WD-01", "42 WD-01"));
Equal(false, batch.IsVisibleFor("42 WD-01", "43 WD-02"));
string[] typeNames = { "42 WD-01", "43 WD-02" };
Equal(true, Bgk3DTextBatch.HasExclusiveVisibility(typeNames,
    (active, owner) => active == owner ? 1 : 0));
Equal(false, Bgk3DTextBatch.HasExclusiveVisibility(typeNames,
    (active, owner) => active == "42 WD-01" && owner == "43 WD-02" ? 1 : active == owner ? 1 : 0));
Equal(false, Bgk3DTextBatch.HasExclusiveVisibility(typeNames,
    (active, owner) => active == owner ? null : 0));
Equal("BGK sichtbar - 170 Abdeckplatte AP-M-S System Optadur - 21ACA760DBB444E55EDF48DD",
    Bgk3DTextBatch.VisibilityParameterName("170 Abdeckplatte AP-M-S System Optadur"));
string safeName = Bgk3DTextBatch.VisibilityParameterName("170 A/B\\C:{D}[E]|;<>?`~\r\n");
if (safeName.IndexOfAny("\\/:{}[]|;<>?`~\r\n\t".ToCharArray()) >= 0)
    throw new Exception($"Family parameter name contains prohibited characters: {safeName}");
if (Bgk3DTextBatch.VisibilityParameterName("170 A/B") == Bgk3DTextBatch.VisibilityParameterName("170 A:B"))
    throw new Exception("Different type names may not share a visibility parameter name.");
if (!string.Equals(Bgk3DTextBatch.VisibilityParameterName("170 A/B"),
    Bgk3DTextBatch.VisibilityParameterName("170 a/b"), StringComparison.OrdinalIgnoreCase))
    throw new Exception("Case-only variations must resolve to the same visibility parameter.");
if (Bgk3DTextBatch.VisibilityParameterName(new string('A', 300)).Length > 130)
    throw new Exception("Visibility parameter name is too long.");
Equal(true, Bgk3DTextBatch.IsLegacyType("KG-01 Außenwand", "42 WD-01", "KG-01 Außenwand", "42", "WD-01"));
Equal(false, Bgk3DTextBatch.IsLegacyType("KG-01 Außenwand", "42 WD-01", "Fremde Familie", "42", "WD-01"));
Equal(false, Bgk3DTextBatch.IsLegacyType("KG-01 Außenwand", "42 WD-01", "KG-01 Außenwand", "42", "Fremd"));
Equal(false, BgkTypeSelectionRules.CanTransfer(0));
Equal(true, BgkTypeSelectionRules.CanTransfer(1));
Equal(false, BgkTypeSelectionRules.CanTransfer(2));
Equal(true, BgkTypeSelectionRules.CanGenerate(2));
Equal(false, BgkTypeSelectionRules.CanGenerate(0));
string? root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "Views", "Hauptfenster.xaml")))
    root = Directory.GetParent(root)?.FullName;
if (root == null) throw new Exception("BGK view was not found for selection contract tests.");
XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
XElement view = XDocument.Load(Path.Combine(root, "Views", "Hauptfenster.xaml")).Root!;
XElement grid = view.Descendants(xaml + "DataGrid")
    .Single(element => (string?)element.Attribute(names + "Name") == "Typmarkierung");
Equal("Extended", (string?)grid.Attribute("SelectionMode"));
Equal("FullRow", (string?)grid.Attribute("SelectionUnit"));
XElement selector = grid.Descendants(xaml + "CheckBox")
    .Single(element => (string?)element.Attribute(names + "Name") == "SelectBgkType");
Equal("SelectBgkType_PreviewMouseLeftButtonDown", (string?)selector.Attribute("PreviewMouseLeftButtonDown"));
Equal("SelectBgkType_PreviewKeyDown", (string?)selector.Attribute("PreviewKeyDown"));
if (!((string?)selector.Attribute("IsChecked") ?? "").Contains("IsSelected"))
    throw new Exception("Selection checkbox must reflect DataGridRow.IsSelected.");
if (!view.Descendants(xaml + "TextBlock").Any(element => ((string?)element.Attribute("Text") ?? "")
    .Contains("SelectedTypCount")))
    throw new Exception("BGK view must show the selected type count.");
string placementHandler = File.ReadAllText(Path.Combine(root, "Core", "Bgk3DTextExternalEventHandler.cs"));
string builder = File.ReadAllText(Path.Combine(root, "Services", "Bgk3DTextMultiFamilyBuilder.cs"));
int projectReload = builder.IndexOf("familyDoc.LoadFamily(project,", StringComparison.Ordinal);
int familyResult = builder.IndexOf("return new Bgk3DTextBuildResult(placedSymbol!", projectReload, StringComparison.Ordinal);
if (projectReload < 0 || familyResult < projectReload
    || !builder[..projectReload].Contains("existingFamilyId", StringComparison.Ordinal)
    || builder[projectReload..familyResult].Contains("existing!.Id", StringComparison.Ordinal))
    throw new Exception("Project family identity must be captured before reload; the old Family wrapper may be invalidated.");
if (!builder[projectReload..familyResult].Contains("Bgk3DTextBatch.IsSafeFamilyReload(", StringComparison.Ordinal)
    || !builder[projectReload..familyResult].Contains("matchingFamilyIds", StringComparison.Ordinal))
    throw new Exception("After project reload, compare the returned family against the sole same-name project family; a Revit reload may replace its ElementId.");
if (!builder.Contains("loadObservations", StringComparison.Ordinal)
    || !builder.Contains("OnFamilyFound: inUse=", StringComparison.Ordinal)
    || !builder.Contains("Projekt nach Load", StringComparison.Ordinal)
    || !builder.Contains("Familie nach Load", StringComparison.Ordinal)
    || !builder.Contains("CaptureReloadObservation(loadObservations,", StringComparison.Ordinal))
    throw new Exception("Before changing family reload semantics, log load callback and exact project/family matrix values at both boundaries.");
if (!builder.Contains("Sichtbarkeitsmatrix weicht ab: Familientyp", StringComparison.Ordinal))
    throw new Exception("Family visibility failure must identify the exact type, flag and actual value rather than alleging a manual edit.");
if (!builder.Contains("AvaName = \"AVA_Anzahl\"", StringComparison.Ordinal)
    || !builder.Contains("new ExternalDefinitionCreationOptions(AvaName, SpecTypeId.Number)", StringComparison.Ordinal)
    || !builder.Contains("manager.AddParameter(definition, GroupTypeId.Data, true)", StringComparison.Ordinal)
    || !builder.Contains("missing.Count == 0 && !avaMissing", StringComparison.Ordinal)
    || !builder.Contains("EnsureAvaParameter(app, manager)", StringComparison.Ordinal))
    throw new Exception("Every generated BGK family must contain AVA_Anzahl as a shared, unitless instance number, including existing families without new types.");
if (!builder.Contains("OfClass(typeof(ParameterElement))", StringComparison.Ordinal))
    throw new Exception("Non-shared project parameters named AVA_Anzahl must be detected before family reload.");
Equal(true, Bgk3DTextBatch.ShouldInitializeAvaDefault(true, true));
Equal(true, Bgk3DTextBatch.ShouldInitializeAvaDefault(false, false));
Equal(false, Bgk3DTextBatch.ShouldInitializeAvaDefault(false, true));
if (!builder.Contains("!avaDefaultsMissing", StringComparison.Ordinal)
    || !builder.Contains("type.HasValue(avaParameter)", StringComparison.Ordinal)
    || !builder.Contains("manager.Set(avaParameter, 1.0)", StringComparison.Ordinal)
    || !builder.Contains("VerifyAvaDefaults(manager, avaDefaultedTypes)", StringComparison.Ordinal))
    throw new Exception("Set AVA_Anzahl to 1 for new or unassigned types, preserve existing assigned values and verify family readback.");
if (!builder.Contains("p.Id.Value > 0 || !p.IsReadOnly", StringComparison.Ordinal))
    throw new Exception("Relevant read-only user instance parameters must be included in preservation snapshots.");
if (!builder.Contains("string reloadPhase", StringComparison.Ordinal)
    || !builder.Contains("SaveReloadFailureTrace(reloadPhase, error, loadObservations)", StringComparison.Ordinal))
    throw new Exception("Reload failures must report their phase and preserve the exception stack for diagnosis.");
int placementDialog = placementHandler.IndexOf("TaskDialog dialog = new TaskDialog(\"BGK-3DText platzieren\")", StringComparison.Ordinal);
int firstPlacementLink = placementHandler.IndexOf("dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1", placementDialog, StringComparison.Ordinal);
int secondPlacementLink = placementHandler.IndexOf("dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2", placementDialog, StringComparison.Ordinal);
int noPlacementLink = placementHandler.IndexOf("dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3", placementDialog, StringComparison.Ordinal);
int placementDefault = placementHandler.IndexOf("dialog.DefaultButton = TaskDialogResult.CommandLink1", placementDialog, StringComparison.Ordinal);
int placementShow = placementHandler.IndexOf("dialog.Show()", placementDialog, StringComparison.Ordinal);
int noPlacementChoice = placementHandler.IndexOf("choice == TaskDialogResult.CommandLink3", placementShow, StringComparison.Ordinal);
int placementStart = placementHandler.IndexOf("if (choice == TaskDialogResult.CommandLink2)", placementShow, StringComparison.Ordinal);
int activation = placementHandler.IndexOf("if (!symbol.IsActive)", placementDialog, StringComparison.Ordinal);
if (placementDialog < 0 || firstPlacementLink < placementDialog || secondPlacementLink < firstPlacementLink
    || noPlacementLink < secondPlacementLink || placementDefault < noPlacementLink || placementShow < placementDefault
    || noPlacementChoice < placementShow || placementStart < noPlacementChoice)
    throw new Exception("BGK placement dialog must include a no-placement action before assigning its default and placing an instance.");
if (activation < noPlacementChoice || activation > placementStart)
    throw new Exception("No-placement choice must leave the family type inactive and avoid a placement transaction.");
Equal("47,49", string.Join(",", Bgk3DTextBatch.NewInstanceIds(new long[] { 41, 43 }, new long[] { 41, 43, 47, 49 })));
Equal(0, Bgk3DTextBatch.NewInstanceIds(new long[] { 41 }, new long[] { 41 }).Count);
string[] oldValues = { "-4|Markierung|String|A", "10|Länge|Double|2" };
string[] withAva = { "-4|Markierung|String|A", "10|Länge|Double|2", "44|AVA_Anzahl|Double|0" };
Equal(true, Bgk3DTextBatch.InstanceValuesPreserved(oldValues, oldValues, "AVA_Anzahl", false));
Equal(true, Bgk3DTextBatch.InstanceValuesPreserved(oldValues, withAva, "AVA_Anzahl", true));
Equal(false, Bgk3DTextBatch.InstanceValuesPreserved(oldValues, withAva, "AVA_Anzahl", false));
Equal(false, Bgk3DTextBatch.InstanceValuesPreserved(oldValues,
    new[] { "-4|Markierung|String|B", "10|Länge|Double|2", "44|AVA_Anzahl|Double|0" }, "AVA_Anzahl", true));
Equal(false, Bgk3DTextBatch.InstanceValuesPreserved(oldValues,
    new[] { "-4|Markierung|String|A", "10|Länge|Double|2", "44|Fremd|Double|0" }, "AVA_Anzahl", true));
Equal(false, Bgk3DTextBatch.ShouldRebuildVisibility(0));
Equal(true, Bgk3DTextBatch.ShouldRebuildVisibility(1));
Equal(true, Bgk3DTextBatch.ShouldRebuildVisibility(2));
long[] beforeFamilyIds = { 11, 21 };
Equal(true, Bgk3DTextBatch.IsSafeFamilyReload("09-20", "09-20", 21, 21,
    beforeFamilyIds, new long[] { 21 }, true));
Equal(true, Bgk3DTextBatch.IsSafeFamilyReload("09-20", "09-20", 22, 21,
    beforeFamilyIds, new long[] { 22 }, false));
Equal(false, Bgk3DTextBatch.IsSafeFamilyReload("09-20", "09-20", 22, 21,
    beforeFamilyIds, new long[] { 21, 22 }, true));
Equal(false, Bgk3DTextBatch.IsSafeFamilyReload("09-20", "09-20", 11, 21,
    beforeFamilyIds, new long[] { 11 }, true));
Equal(false, Bgk3DTextBatch.IsSafeFamilyReload("09-20", "09-20 (1)", 22, 21,
    beforeFamilyIds, new long[] { 21 }, true));
Equal(true, Bgk3DTextBatch.IsSafeFamilyReload("09-20", "09-20", 22, null,
    beforeFamilyIds, new long[] { 22 }, false));
Equal(false, Bgk3DTextBatch.IsSafeFamilyReload("09-20", "09-20", 11, null,
    beforeFamilyIds, new long[] { 11 }, false));
var expectedOwners = new Dictionary<long, string> { [7] = "42 WD-01", [9] = "43 WD-02" };
Equal(true, Bgk3DTextBatch.HasCorrectAssociationOwners(expectedOwners, id => expectedOwners[id]));
Equal(false, Bgk3DTextBatch.HasCorrectAssociationOwners(expectedOwners,
    id => id == 7 ? "43 WD-02" : "42 WD-01"));
Equal(false, Bgk3DTextBatch.HasCorrectAssociationOwners(expectedOwners, _ => null));
var expectedFlags = new Dictionary<long, long> { [7] = 51, [9] = 52 };
Equal(true, Bgk3DTextBatch.HasExactAssociations(expectedFlags,
    id => id == 7 ? new long[] { 51 } : new long[] { 52 }, _ => null));
Equal(true, Bgk3DTextBatch.HasExactAssociations(expectedFlags,
    id => id == 7 ? new long[] { 51 } : new long[] { 52 }, id => expectedFlags[id]));
Equal(false, Bgk3DTextBatch.HasExactAssociations(expectedFlags,
    id => id == 7 ? new long[] { 52 } : new long[] { 51 }, _ => null));
Equal(false, Bgk3DTextBatch.HasExactAssociations(expectedFlags,
    id => id == 7 ? new long[] { 51, 52 } : new long[] { 52 }, _ => null));
Equal(false, Bgk3DTextBatch.HasExactAssociations(expectedFlags,
    id => id == 7 ? Array.Empty<long>() : new long[] { 52 }, _ => null));
Equal(false, Bgk3DTextBatch.HasExactAssociations(expectedFlags,
    id => id == 7 ? new long[] { 51 } : new long[] { 52 }, _ => 99));
string childName = Bgk3DTextBatch.ChildFamilyName("KG-01 Außenwand", "42 A/B:C\\D");
if (childName.IndexOfAny("\\/:*?\"<>|{}[];`~\r\n\t".ToCharArray()) >= 0 || childName.Length > 130)
    throw new Exception("Nested child family name is unsafe.");
if (childName == Bgk3DTextBatch.ChildFamilyName("KG-01 Außenwand", "42 A:B:C\\D"))
    throw new Exception("Sanitized name collision was not prevented.");
Equal(true, string.Equals(childName,
    Bgk3DTextBatch.ChildFamilyName("kg-01 außenwand", "42 a/b:c\\d"), StringComparison.OrdinalIgnoreCase));
if (childName == Bgk3DTextBatch.ChildFamilyName("KG-02 Außenwand", "42 A/B:C\\D"))
    throw new Exception("Different outer families must have different child identities.");
string[] managed = { "42 WD-01", "43 WD-02" };
string child42 = Bgk3DTextBatch.ChildFamilyName("KG-01 Außenwand", managed[0]);
Equal<string?>(managed[0], Bgk3DTextBatch.FindChildOwner("KG-01 Außenwand", managed, managed[0], child42));
Equal<string?>(managed[0], Bgk3DTextBatch.FindChildOwner("KG-01 Außenwand", managed, null, child42));
Equal<string?>(null, Bgk3DTextBatch.FindChildOwner("KG-01 Außenwand", managed, managed[1], child42));
Equal<string?>(null, Bgk3DTextBatch.FindChildOwner("KG-01 Außenwand", managed, null, "Unbekannte Familie"));
Equal<string?>(null, Bgk3DTextBatch.FindChildOwner("KG-02 Außenwand", managed, null, child42));
Equal<string?>(null, Bgk3DTextBatch.ChildLoadFailure(childName, childName, true));
Equal("Revit hat das Laden abgelehnt (LoadFamily=false).",
    Bgk3DTextBatch.ChildLoadFailure(childName, null, false));
Equal("Revit hat keine Kindfamilie zurückgegeben.",
    Bgk3DTextBatch.ChildLoadFailure(childName, null, true));
Equal($"Revit hat stattdessen '{childName} - fremd' geladen.",
    Bgk3DTextBatch.ChildLoadFailure(childName, childName + " - fremd", true));
Equal("direkt: Instanz 42, Sichtbar-ID -77, Flag-ID 8, zuordenbar ja, vorwärts keine, rückwärts nein (0)",
    Bgk3DTextBatch.BindingObservation("direkt", 42, -77, 8, true, null, false, 0));
Equal("nach Commit: Instanz 42, Sichtbar-ID -77, Flag-ID 8, zuordenbar ja, vorwärts 8, rückwärts ja (1)",
    Bgk3DTextBatch.BindingObservation("nach Commit", 42, -77, 8, true, 8, true, 1));
Equal("direkt: Instanz 42, Sichtbar-ID -77, Flag-ID 8, zuordenbar ja, vorwärts keine, rückwärts ja (1), vorwärts über Rückwärtsobjekt 8",
    Bgk3DTextBatch.BindingProbeObservation("direkt", 42, -77, 8, true, null, 8, true, 1));
Equal("nach Commit: Instanz 42, Sichtbar-ID -77, Flag-ID 8, zuordenbar ja, vorwärts keine, rückwärts ja (1), vorwärts über Rückwärtsobjekt keine",
    Bgk3DTextBatch.BindingProbeObservation("nach Commit", 42, -77, 8, true, null, null, true, 1));
Equal("nach Commit: Instanz 42, Sichtbar-ID -77, Flag-ID 8, zuordenbar ja, vorwärts keine, rückwärts ja (1), vorwärts über Rückwärtsobjekt Fehler InvalidOperationException",
    Bgk3DTextBatch.BindingProbeObservation("nach Commit", 42, -77, 8, true, null, null, true, 1,
        "InvalidOperationException"));
Equal("BGK-Familientest-20261022-093000-00000000000000000000000000000001.rfa",
    Bgk3DTextBatch.DiagnosticFamilyFileName(new DateTime(2026, 10, 22, 9, 30, 0, DateTimeKind.Utc),
        Guid.Parse("00000000-0000-0000-0000-000000000001")));
string templateRoot = Path.Combine("C:", "Autodesk", "Templates");
string englishTemplate = Path.Combine(templateRoot, "English", "Metric Generic Model.rft");
Equal(englishTemplate, Bgk3DTextBatch.FindGenericModelTemplate("", "C:/MissingTemplates", templateRoot,
    path => path == englishTemplate));
Equal<string?>(null, Bgk3DTextBatch.FindGenericModelTemplate("C:/user/missing.rft",
    "C:/MissingTemplates", templateRoot, path => path == englishTemplate));
Equal("C:/user/custom.rft", Bgk3DTextBatch.FindGenericModelTemplate("C:/user/custom.rft",
    "C:/MissingTemplates", templateRoot, path => path == englishTemplate || path == "C:/user/custom.rft"));
if (Bgk3DTextBatch.TryCreate(new[] { spec, spec }, out _, out _))
    throw new Exception("Duplicate type must be rejected.");
if (Bgk3DTextSpec.TryCreate("KG-02", "Außenwand", "44", "WD-03", out var unrelated, out error)
    && Bgk3DTextBatch.TryCreate(new[] { spec, unrelated! }, out _, out _))
    throw new Exception("Mixed BGK families must be rejected.");
Reject(" ", "Außenwand", "42", "WD-01");
Reject("KG-01", " ", "42", "WD-01");
Reject("KG-01", "Außenwand", "42", " ");
Reject("KG-01", "Außenwand", " ", "WD-01");
if (Bgk3DTextSpec.TrySize("0", "2", out _, out _, out _)) throw new Exception("Zero text height accepted.");
if (Bgk3DTextSpec.TrySize("100", "-1", out _, out _, out _)) throw new Exception("Negative extrusion depth accepted.");
if (!Bgk3DTextSpec.TrySize("100,5", "4,5", out var height, out var depth, out error)) throw new Exception(error);
Equal(100.5, height);
Equal(4.5, depth);
var selection = new BgkSelectionSnapshot<object, long>();
object project = new();
selection.Update(project, new long[] { 17, 19 }, paneHasFocus: false);
Equal("17,19", string.Join(",", selection.Resolve(project, Array.Empty<long>())));
Equal("23", string.Join(",", selection.Resolve(project, new long[] { 23 })));
selection.Update(project, Array.Empty<long>(), paneHasFocus: true);
Equal("17,19", string.Join(",", selection.Resolve(project, Array.Empty<long>())));
selection.Update(project, Array.Empty<long>(), paneHasFocus: false);
Equal(0, selection.Resolve(project, Array.Empty<long>()).Count);
selection.Update(project, new long[] { 17 }, paneHasFocus: false);
Equal(0, selection.Resolve(new object(), Array.Empty<long>()).Count);
Console.WriteLine("BGK-3D-Text-Spezifikation: alle Prüfungen bestanden.");
