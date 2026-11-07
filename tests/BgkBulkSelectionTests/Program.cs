using BIMassist.Core;
using System.Windows.Controls;
using System.Windows;
using System.Xml.Linq;
using System.IO;

Exception? failure = null;
var thread = new Thread(() =>
{
    try
    {
        var grid = new DataGrid { SelectionMode = DataGridSelectionMode.Extended,
            SelectionUnit = DataGridSelectionUnit.FullRow, ItemsSource = new[] { "A", "B", "C" } };
        grid.Measure(new Size(500, 400));
        grid.Arrange(new Rect(0, 0, 500, 400));
        grid.UpdateLayout();
        grid.SelectedItems.Add("B");
        BgkTypeBulkSelection.SelectAll(grid);

        if (grid.SelectedItems.Count != 3 || !grid.SelectedItems.Contains("A")
            || !grid.SelectedItems.Contains("B") || !grid.SelectedItems.Contains("C"))
            throw new Exception("Select all must include every type of the current BGK, not only visible rows.");

        BgkTypeBulkSelection.Clear(grid);
        if (grid.SelectedItems.Count != 0)
            throw new Exception("Deselect all must leave no types selected.");

        grid.ItemsSource = Enumerable.Range(0, 300).Select(n => $"Type-{n}").ToArray();
        grid.UpdateLayout();
        BgkTypeBulkSelection.SelectAll(grid);
        if (grid.SelectedItems.Count != 300 || !grid.SelectedItems.Contains("Type-299"))
            throw new Exception("Bulk selection must include virtualized rows beyond the viewport.");
        BgkTypeBulkSelection.Clear(grid);

        grid.ItemsSource = new[] { "D" };
        BgkTypeBulkSelection.SelectAll(grid);
        if (grid.SelectedItems.Count != 1 || !grid.SelectedItems.Contains("D"))
            throw new Exception("Selection must use the currently displayed BGK only.");
        grid.ItemsSource = Array.Empty<string>();
        BgkTypeBulkSelection.SelectAll(grid);
        BgkTypeBulkSelection.Clear(grid);
        if (grid.SelectedItems.Count != 0)
            throw new Exception("Empty BGK type lists must remain empty.");
    }
    catch (Exception ex) { failure = ex; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (failure != null) throw failure;
string? repo = AppContext.BaseDirectory;
while (repo != null && !File.Exists(Path.Combine(repo, "Views", "Hauptfenster.xaml")))
    repo = Directory.GetParent(repo)?.FullName;
if (repo == null) throw new Exception("BGK manager XAML not found.");
XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
var view = XDocument.Load(Path.Combine(repo, "Views", "Hauptfenster.xaml"));
var buttons = view.Descendants(xaml + "Button");
foreach (var (name, handler) in new[] { ("SelectAllBgkTypes", "SelectAllBgkTypes_Click"),
    ("ClearBgkTypes", "ClearBgkTypes_Click") })
{
    var button = buttons.SingleOrDefault(element => (string?)element.Attribute(names + "Name") == name);
    if ((string?)button?.Attribute("Click") != handler)
        throw new Exception($"Missing BGK type bulk-selection button or handler: {name}");
}
string codeBehind = File.ReadAllText(Path.Combine(repo, "Views", "Hauptfenster.xaml.cs"));
if (!codeBehind.Contains("BgkTypeBulkSelection.SelectAll(Typmarkierung)")
    || !codeBehind.Contains("BgkTypeBulkSelection.Clear(Typmarkierung)"))
    throw new Exception("BGK type bulk-selection buttons must operate on the current DataGrid.");
Console.WriteLine("BGK type bulk selection PASS");
