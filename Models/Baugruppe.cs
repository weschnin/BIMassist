using System.Collections.ObjectModel;

namespace BIMassist.Models
{
    // Schlanker, read-only genutzter Knoten für die Kategorie
    public sealed class Baugruppe
    {
        public string Key { get; set; }
        public ObservableCollection<Typmarkierung> Typmarkierungen { get; set; } = new();
    }
}