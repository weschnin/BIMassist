using Autodesk.Revit.UI;
using BIMassist.ViewModels;

namespace BIMassist.Handlers
{
    // Unlock (optional, falls Unlock tatsächlich API-Aufrufe braucht)
    public class UnlockMetadataHandler : IExternalEventHandler
    {
        public MetadataViewModel ViewModel { get; set; }
        public void Execute(UIApplication app)
        {
            ViewModel.Unlock();
        }
        public string GetName() => "UnlockMetadataHandler";
    }
}
