using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;
using BIMassist.ViewModels;

namespace BIMassist.Views
{
    /// <summary>
    /// Interaktionslogik für BGKManagerUserControl.xaml
    /// </summary>
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
    }
}
