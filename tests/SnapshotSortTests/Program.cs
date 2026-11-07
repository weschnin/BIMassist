using BIMassist.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml.Linq;

Exception? failure = null;
var thread = new Thread(() =>
{
    try
    {
        var items = new ObservableCollection<SnapshotRow>
        {
            new("Zulu"), new("alpha"), new("Beta")
        };
        var view = CollectionViewSource.GetDefaultView(items);
        var list = new ListBox { ItemsSource = view, DisplayMemberPath = "Name" };
        var selected = items[0];
        list.SelectedItem = selected;

        SnapshotListSorting.Apply(view, ListSortDirection.Ascending);
        AssertOrder(list, "alpha", "Beta", "Zulu");
        if (!ReferenceEquals(list.SelectedItem, selected))
            throw new Exception("Sorting A-Z must preserve the selected snapshot.");

        SnapshotListSorting.Apply(view, ListSortDirection.Descending);
        AssertOrder(list, "Zulu", "Beta", "alpha");
        if (!ReferenceEquals(list.SelectedItem, selected))
            throw new Exception("Sorting Z-A must preserve the selected snapshot.");

        items.Add(new SnapshotRow("Echo"));
        AssertOrder(list, "Zulu", "Echo", "Beta", "alpha");
        items.Clear();
        AssertOrder(list);
        items.Add(new SnapshotRow("Alpha"));
        items.Add(new SnapshotRow("Omega"));
        AssertOrder(list, "Omega", "Alpha");
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (failure != null) throw failure;

string? repo = AppContext.BaseDirectory;
while (repo != null && !File.Exists(Path.Combine(repo, "Views", "SnapshotManagerWindow.xaml")))
    repo = Directory.GetParent(repo)?.FullName;
if (repo == null) throw new Exception("Snapshot manager XAML not found.");
XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
var xdoc = XDocument.Load(Path.Combine(repo, "Views", "SnapshotManagerWindow.xaml"));
var selector = xdoc.Descendants(xaml + "ComboBox")
    .SingleOrDefault(element => (string?)element.Attribute(names + "Name") == "SortOrder");
if ((string?)selector?.Attribute("SelectionChanged") != "SortOrder_SelectionChanged"
    || selector.Elements(xaml + "ComboBoxItem").Count() != 2)
    throw new Exception("Snapshot manager must offer two wired sort choices.");
string code = File.ReadAllText(Path.Combine(repo, "Views", "SnapshotManagerWindow.xaml.cs"));
if (!code.Contains("List.ItemsSource = _itemsView")
    || !code.Contains("SnapshotListSorting.Apply(_itemsView")
    || !code.Contains("SortOrder_SelectionChanged"))
    throw new Exception("Snapshot manager must apply sorting to the list's collection view.");
Console.WriteLine("Snapshot list sorting PASS");

static void AssertOrder(ListBox list, params string[] names)
{
    string[] actual = list.Items.Cast<SnapshotRow>().Select(row => row.Name).ToArray();
    if (!actual.SequenceEqual(names))
        throw new Exception($"Expected [{string.Join(", ", names)}], got [{string.Join(", ", actual)}].");
}

internal sealed record SnapshotRow(string Name);
