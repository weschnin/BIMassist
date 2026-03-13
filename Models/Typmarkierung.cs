using System;
using System.Xml.Serialization;
using BIMassist.ViewModels;

namespace BIMassist
{
    public class Typmarkierung : ViewModelBase
    {
        public Typmarkierung(Baugruppe nd)
        {
            ParentNode = nd;
        }
        public Typmarkierung()
        {

        }
        private int _typnumber;
        public int Typnummer
        {
            get => _typnumber;
            set
            {
                try
                {
                    Set(ref _typnumber, Convert.ToInt32(value));
                }
                catch (System.Exception)
                {
                }

            }
        }

        private string _typlabel;
        public string Typbezeichnung
        {
            get => _typlabel;
            set => Set(ref _typlabel, value);
        }

        private string _typKommentar;
        public string Typkommentar
        {
            get => _typKommentar;
            set => Set(ref _typKommentar, value);
        }

        [XmlIgnore]
        private Baugruppe _parentNode;
        [XmlIgnore]
        public Baugruppe ParentNode
        {
            get { return _parentNode; }
            set { Set(ref _parentNode, value); }
        }
    }
}
