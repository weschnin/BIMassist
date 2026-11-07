using WpfDataGrid = System.Windows.Controls.DataGrid;

namespace BIMassist.Core
{
    internal static class BgkTypeBulkSelection
    {
        internal static void SelectAll(WpfDataGrid grid) => grid.SelectAll();

        internal static void Clear(WpfDataGrid grid) => grid.UnselectAll();
    }
}
