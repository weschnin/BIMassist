using System.ComponentModel;
using System.Windows.Data;

namespace BIMassist.Core
{
    internal static class SnapshotListSorting
    {
        public static void Apply(ICollectionView view, ListSortDirection direction)
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription("Name", direction));
            }
        }
    }
}
