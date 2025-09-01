using Autodesk.Revit.UI;
using BIMassist.Models;
using BIMassist.ViewModels;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BIMassist.Views
{
    public partial class BGKManagerUserControl : UserControl
    {
        public BGKManagerUserControl()
        {
            InitializeComponent();
        }

        public void InitDataContext(UIApplication uiapp)
        {
            DataContext = new BGKManagerViewModel(uiapp);
        }

        // TreeView-Selection: unterscheide Kategorie vs. Typ
        private void BaugruppenTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is not BGKManagerViewModel vm) return;

            if (e.NewValue is Typmarkierung typ)
            {
                vm.SelectedTypmarkierung = typ;
                // Elternknoten ermitteln (für Kategorie → ASSEMBLY_CODE)
                vm.SelectedBaugruppe = vm.Baugruppen?.FirstOrDefault(b => b.Typmarkierungen.Contains(typ));
            }
            else if (e.NewValue is Baugruppe bg)
            {
                vm.SelectedBaugruppe = bg;
                vm.SelectedTypmarkierung = null;
            }
            else
            {
                vm.SelectedBaugruppe = null;
                vm.SelectedTypmarkierung = null;
            }
        }
    }
}