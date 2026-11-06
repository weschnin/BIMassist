using BIMassist.Core;

var sections = new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.OrdinalIgnoreCase)
{
    ["Schnitt A"] = new long[] { 501 }
};
var otherSheet = new HashSet<long> { 400 };
if (SheetSectionVisibilityPolicy.Decide("Schnitt A", sections, otherSheet, isHidden: false)
    != MarkerUpdate.Hide)
    throw new Exception("A visible marker for a section on another sheet must be hidden.");
Console.WriteLine("PASS foreign sheet marker hidden");
if (SheetSectionVisibilityPolicy.Decide("Schnitt A", sections, new HashSet<long> { 501 }, isHidden: true)
    != MarkerUpdate.Unhide)
    throw new Exception("A hidden marker for a section on this sheet must be unhidden.");
if (SheetSectionVisibilityPolicy.Decide("Schnitt A", sections, otherSheet, isHidden: true)
    != MarkerUpdate.None)
    throw new Exception("An already hidden foreign marker must remain unchanged.");
if (SheetSectionVisibilityPolicy.Decide("Nicht zugeordnet", sections, otherSheet, isHidden: false)
    != MarkerUpdate.None)
    throw new Exception("Non-section viewers must not be changed.");
sections["Schnitt X"] = new long[] { 501, 502 };
if (SheetSectionVisibilityPolicy.Decide("Schnitt X", sections, new HashSet<long> { 501 }, isHidden: false)
    != MarkerUpdate.Ambiguous)
    throw new Exception("Ambiguous section names must never be guessed from one placed view.");
Console.WriteLine("PASS own sheet, hidden, unknown and ambiguous markers");

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string ribbon = File.ReadAllText(Path.Combine(root, "App.cs"));
if (!ribbon.Contains("new PulldownButtonData(\"Pläne\", \"Pläne\")")
    || !ribbon.Contains("BIMassist.Commands.CleanSectionsOnSheetsCommand"))
    throw new Exception("The sheet-section command is not registered in the Pläne ribbon group.");
string command = File.ReadAllText(Path.Combine(root, "Commands", "CleanSectionsOnSheetsCommand.cs"));
if (!command.Contains("SheetSectionVisibilityPolicy.Decide"))
    throw new Exception("The command does not use the verified visibility policy.");
if (!command.Contains("update == MarkerUpdate.Hide && !marker.CanBeHidden(view)"))
    throw new Exception("Unhiding must not be blocked by a hide-only capability check.");
if (!command.Contains("view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryViewProperties)"))
    throw new Exception("Temporary View Properties must be rejected before visibility changes.");
Console.WriteLine("PASS sheet-section ribbon registration and policy wiring");
