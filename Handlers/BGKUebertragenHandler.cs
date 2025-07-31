using Autodesk.Revit.UI;

namespace BIMassist.Handlers
{
    public class BGKUebertragenHandler : IExternalEventHandler
    {
        public ViewModels.BGKManagerViewModel ViewModel { get; set; }

        public void Execute(UIApplication app)
        {
            ViewModel?.ExecuteUebertragen(app);
        }

        public string GetName() => "BGK Übertragen Handler";
    }
}
