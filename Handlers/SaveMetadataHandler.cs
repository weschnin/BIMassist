using Autodesk.Revit.UI;
using BIMassist.ViewModels;

namespace BIMassist.Handlers
{
    public class SaveMetadataHandler : IExternalEventHandler
    {
        public MetadataViewModel ViewModel { get; set; }
        public void Execute(UIApplication app)
        {
            try
            {
                ViewModel.ExecuteSave(app);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Fehler beim Speichern", ex.Message);
            }
        }
        public string GetName() => "SaveMetadataHandler";
    }
}
