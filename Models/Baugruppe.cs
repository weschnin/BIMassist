using System.Collections.ObjectModel;
using System.Linq;
using System.Xml.Serialization;
using BIMassist.ViewModels;

namespace BIMassist
{
    // Richer node implemented in AWESBox; kept as non-sealed and observable
    public class Baugruppe : ViewModelBase
    {
        private string _Key;
        public string Key
        {
            get => _Key;
            set => Set(ref _Key, value);
        }

        private string _description;
        public string Description
        {
            get => _description;
            set => Set(ref _description, value);
        }

        private string _NodeComment;
        public string NodeComment
        {
            get => _NodeComment;
            set => Set(ref _NodeComment, value);
        }

        private bool _NodeProtection;
        public bool NodeProtection
        {
            get => _NodeProtection;
            set => Set(ref _NodeProtection, value);
        }

        private bool _rootNode;
        public bool RootNode
        {
            get => _rootNode;
            set => Set(ref _rootNode, value);
        }

        private bool _expandNode;
        public bool ExpandNode
        {
            get => _expandNode;
            set => Set(ref _expandNode, value);
        }

        private bool _isSelectedNode;
        public bool IsSelectedNode
        {
            get => _isSelectedNode;
            set => Set(ref _isSelectedNode, value);
        }

        private int _Level;
        public int Level
        {
            get =>_Level;
            set => Set(ref _Level, value);
        }

        private string _Category;
        public string Category
        {
            get => _Category;
            set => Set(ref _Category, value);
        }

        [XmlIgnore]
        private Baugruppe _ParentNode;
        [XmlIgnore]
        public Baugruppe ParentNode
        {
            get => _ParentNode;
            set => Set(ref _ParentNode, value);
        }

        private ObservableCollection<Typmarkierung> _Typenmarkierungen;
        public ObservableCollection<Typmarkierung> Typmarkierungen
        {
            get => _Typenmarkierungen;
            set => Set(ref _Typenmarkierungen, value);
        }

        private ObservableCollection<Baugruppe> _Labels;
        public ObservableCollection<Baugruppe> Baugruppen
        {
            get => _Labels ?? (_Labels = new ObservableCollection<Baugruppe>());
            set => Set(ref _Labels, value);
        }
    }
}
