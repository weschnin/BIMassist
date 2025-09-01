namespace BIMassist.Models
{
    // Ein Typ-Eintrag unterhalb einer Baugruppe
    public sealed class Typmarkierung
    {
        public string Typnummer { get; set; }
        public string Beschreibung { get; set; }   // vormals: Typbezeichnung
    }
}