using Autodesk.Revit.UI;
using BIMassist.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace BIMassist.Views
{
    /// <summary>
    /// Interaktionslogik für MetadataUserControl.xaml
    /// </summary>
    public partial class MetadataUserControl : System.Windows.Controls.UserControl
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
            DataContext = new MetadataViewModel(uiapp);
        }

        private void Unlock_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MetadataViewModel vm)
            {
                vm.PasswordInput = PasswordBox.Password;
                vm.UnlockCommand.Execute(null);
                PasswordBox.Password = string.Empty;
            }
        }
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MetadataViewModel vm)
            {
                vm.PasswordInput = PasswordBox.Password;
                vm.SaveCommand.Execute(null);
                PasswordBox.Password = string.Empty;
            }
        }

        private void SaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MetadataViewModel vm)
            {
                vm.SaveTemplate();
            }
        }

        private void LoadTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MetadataViewModel vm)
            {
                vm.LoadTemplate();
            }
        }
    }
}
