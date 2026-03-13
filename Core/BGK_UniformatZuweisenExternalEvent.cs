using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace BIMassist
{
    [Transaction(TransactionMode.Manual)]
    public class BGK_UniformatZuweisenExternalEvent : IExternalEventHandler
    {
        public string Dateipfad { get; set; }

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;

            ModelPath filepath = ModelPathUtils.ConvertUserVisiblePathToModelPath(Dateipfad);

            using (Transaction tx = new Transaction(doc))
            {
                tx.Start("BGK-Externalressource-Zuweisung");
                ExternalResourceReference exref = ExternalResourceReference.CreateLocalResource(doc,
                    ExternalResourceTypes.BuiltInExternalResourceTypes.AssemblyCodeTable, filepath, PathType.Absolute);
                KeyBasedTreeEntriesLoadResults reslt = null;
                AssemblyCodeTable tab = AssemblyCodeTable.GetAssemblyCodeTable(doc);
                tab.LoadFrom(exref, reslt);
                tx.Commit();
            }
            return;
        }

        public string GetName()
        {
            return "BGKUniformatZuweisung";
        }
    }
}
