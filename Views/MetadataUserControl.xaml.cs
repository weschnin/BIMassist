using Autodesk.Revit.UI;
using BIMassist.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace BIMassist.Views
{
    /// <summary>
    /// Interaktionslogik für MetadataUserControl.xaml
    /// </summary>
    public partial class MetadataUserControl : UserControl
    {
        public MetadataUserControl()
        {
            InitializeComponent();
            this.Unloaded += MetadataUserControl_Unloaded;
        }

        private void MetadataUserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Events aufräumen!
            if (DataContext is MetadataViewModel vm)
            {
                vm.Dispose(); // Stopt Timer etc.
            }
        }

        public void InitDataContext(UIApplication uiapp, string str)
        {
            DataContext = new MetadataViewModel(uiapp.ActiveUIDocument.Document, uiapp.ActiveUIDocument);
        }

        private void Unlock_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MetadataViewModel vm)
            {
                vm.PasswordInput = PasswordBox.Password;
                vm.UnlockCommand.Execute(null);
            }
        }
    }
}
