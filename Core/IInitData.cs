using Autodesk.Revit.UI;

namespace BIMassist.Core
{
    public interface IInitData
    {
        void InitDataContext(UIApplication uiapp, string str);
    }
}
