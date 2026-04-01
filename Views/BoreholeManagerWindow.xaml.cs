using BIMassist.Models;
using BIMassist.ViewModels;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace BIMassist.Views
{
    public partial class BoreholeManagerWindow : Window
    {
        public BoreholeManagerWindow()
        {
            InitializeComponent();

            HolesGrid.SelectionChanged += HolesGrid_SelectionChanged;
            LayersGrid.SelectionChanged += LayersGrid_SelectionChanged;
        }

        private void HolesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not BoreholeManagerViewModel vm) return;

            var list = new List<Borehole>();
            foreach (var item in HolesGrid.SelectedItems)
                if (item is Borehole bh) list.Add(bh);

            vm.SelectedBoreholes = list;
            vm.SelectedBorehole = HolesGrid.SelectedItem as Borehole;
        }

        private void LayersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not BoreholeManagerViewModel vm) return;

            var list = new List<GeologyLayer>();
            foreach (var item in LayersGrid.SelectedItems)
                if (item is GeologyLayer gl) list.Add(gl);

            vm.SelectedLayers = list;
        }

        private void HolesGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit && DataContext is BoreholeManagerViewModel vm)
                vm.PersistAfterEdit();
        }

        private void LayersGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit && DataContext is BoreholeManagerViewModel vm)
                vm.PersistAfterEdit();
        }
    }
}
