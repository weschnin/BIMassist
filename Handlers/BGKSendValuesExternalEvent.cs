using Autodesk.Revit.UI;

namespace BIMassist.Revit
{
    /// Führt SendValues im Revit-API-Kontext aus.
    public sealed class BGKSendValuesExternalEvent : IExternalEventHandler
    {
        public BIMassist.ViewModels.BGKManagerViewModel ViewModel { get; set; }

        public void Execute(UIApplication app)
        {
            // delegiert die eigentliche Arbeit an das ViewModel – JETZT im API-Kontext
            ViewModel?.ExecuteSendValuesInApiContext(app);
        }

        public string GetName() => "BGK Manager – Werte übertragen";
    }
}