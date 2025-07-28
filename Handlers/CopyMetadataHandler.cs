using Autodesk.Revit.UI;
using BIMassist.ViewModels;

namespace BIMassist.Handlers
{
    // Copy
    public class CopyMetadataHandler : IExternalEventHandler
    {
        public MetadataViewModel ViewModel { get; set; }
        public void Execute(UIApplication app)
        {
            try
            {
                ViewModel.ExecuteCopy(app);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler beim Kopieren", ex.Message);
            }
        }
        public string GetName() => "CopyMetadataHandler";
    }
}
