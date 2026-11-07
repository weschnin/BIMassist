using BIMassist;
using BIMassist.Core;
using System.Collections.ObjectModel;
using System.Xml.Serialization;
using System.Xml.Linq;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var root = new Baugruppe { Key = "Root" };
var child = new Baugruppe { Key = "Child", ParentNode = root };
var grandchild = new Baugruppe { Key = "Grandchild", ParentNode = child };
var sibling = new Baugruppe { Key = "Sibling", ParentNode = root };
root.Baugruppen.Add(child);
child.Baugruppen.Add(grandchild);
root.Baugruppen.Add(sibling);

BgkNodeProtection.ApplyToSubtree(child, true);
Assert(child.NodeProtection && grandchild.NodeProtection && !root.NodeProtection && !sibling.NodeProtection,
    "Protecting a branch must cover that branch only.");
Assert(!BgkNodeProtection.CanEdit(child) && !BgkNodeProtection.CanEdit(grandchild),
    "A locked node cannot be edited.");
Assert(!BgkNodeProtection.CanMoveOrDelete(child), "A locked branch cannot be removed or moved.");
BgkNodeProtection.ApplyToSubtree(child, false);
Assert(!child.NodeProtection && !grandchild.NodeProtection && BgkNodeProtection.CanMoveOrDelete(child),
    "Unlocking the branch must unlock all descendants.");

grandchild.NodeProtection = true;
Assert(!BgkNodeProtection.CanMoveOrDelete(child), "An unlocked parent must not remove a locked descendant.");
BgkNodeProtection.ApplyToSubtree(root, true);
Assert(root.NodeProtection && child.NodeProtection && grandchild.NodeProtection && sibling.NodeProtection,
    "Protecting a root must cover all depths and branches.");
BgkNodeProtection.ApplyToSubtree(child, false);
Assert(!BgkNodeProtection.CanEdit(child) && !BgkNodeProtection.CanEdit(grandchild),
    "A protected ancestor must still prevent edits to an unlocked descendant.");
Assert(child.IsEffectivelyProtected && grandchild.IsEffectivelyProtected,
    "UI fields must remain locked when an ancestor is protected.");
Assert(!BgkNodeProtection.CanChangeProtection(child) && BgkNodeProtection.CanChangeProtection(root),
    "Only unlocking the locked ancestor can restore editing.");
int inheritedNotifications = 0;
child.PropertyChanged += (_, args) =>
{
    if (args.PropertyName == nameof(Baugruppe.IsEffectivelyProtected)) inheritedNotifications++;
};
BgkNodeProtection.ApplyToSubtree(root, false);
Assert(BgkNodeProtection.CanEdit(grandchild) && BgkNodeProtection.CanMoveOrDelete(root),
    "Unlocking a root must restore all descendants.");
Assert(inheritedNotifications > 0 && !child.IsEffectivelyProtected,
    "Unlocking an ancestor must notify an already-unlocked child to refresh its UI.");
BgkNodeProtection.ApplyToSubtree(null, true);

// XML is the BGK manager's persistence format; protection must survive a save/load.
BgkNodeProtection.ApplyToSubtree(root, true);
var serializer = new XmlSerializer(typeof(Baugruppe));
using var serialized = new StringWriter();
serializer.Serialize(serialized, root);
using var reader = new StringReader(serialized.ToString());
var restored = (Baugruppe)serializer.Deserialize(reader)!;
Assert(restored.NodeProtection && restored.Baugruppen.All(n => n.NodeProtection)
    && restored.Baugruppen[0].Baugruppen[0].NodeProtection, "Protect flags must survive XML roundtrip.");
Assert(!serialized.ToString().Contains("IsEffectivelyProtected"),
    "Derived inherited protection must not alter the BGK XML format.");

// Verify the UI actually invokes the subtree command; a direct TwoWay binding used to lock only one node.
string? repo = AppContext.BaseDirectory;
while (repo != null && !File.Exists(Path.Combine(repo, "Views", "Hauptfenster.xaml")))
    repo = Directory.GetParent(repo)?.FullName;
if (repo == null) throw new Exception("Cannot locate BGK manager view.");
XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
var view = XDocument.Load(Path.Combine(repo, "Views", "Hauptfenster.xaml"));
var toggle = view.Descendants(ns + "ToggleButton")
    .Single(element => (string?)element.Attribute(x + "Name") == "ProtectedToggleButton");
Assert(((string?)toggle.Attribute("Command") ?? "").Contains("AllSubNodesProtectionCmd"),
    "The lock toggle must execute the subtree command.");
Assert(((string?)toggle.Attribute("CommandParameter") ?? "").Contains("SelectedNode"),
    "The lock toggle must pass the selected node to the command.");
Assert(((string?)toggle.Attribute("IsChecked") ?? "").Contains("Mode=OneWay"),
    "The lock state should display the selection and not write it before the command runs.");
var textBoxes = view.Descendants().Where(element => element.Name.LocalName == "TextBoxOwnControl")
    .Where(element => ((string?)element.Attribute("Text") ?? "").Contains("SelectedNode."))
    .ToArray();
Assert(textBoxes.Length >= 2 && textBoxes.All(element => ((string?)element.Attribute("IsReadOnly") ?? "")
    .Contains("SelectedNode.IsEffectivelyProtected")), "BGK key/description must respect inherited protection.");
var typeGrid = view.Descendants(ns + "DataGrid")
    .Single(element => (string?)element.Attribute(x + "Name") == "Typmarkierung");
Assert(typeGrid.Descendants(ns + "Binding").Any(element => (string?)element.Attribute("Path") ==
    "SelectedNode.IsEffectivelyProtected"), "BGK type editor must respect inherited protection.");
Assert(!view.Descendants(ns + "Button").Any(element => ((string?)element.Attribute("IsEnabled") ?? "")
    .Contains("SelectedNode.NodeProtection")), "Editing buttons must respect inherited protection.");
Console.WriteLine("BGK subtree protection and UI wiring PASS");
