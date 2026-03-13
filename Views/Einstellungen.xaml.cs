using System.Windows;

namespace BIMassist
{
    /// <summary>
    /// Interaktionslogik für Einstellungen.xaml
    /// </summary>
    public partial class Einstellungen : Window
    {
        public Einstellungen(MainViewModel datacntx)
        {
            InitializeComponent();
            DataContext = datacntx;
        }
    }
}
