// These separate namespaces mirror the real API type boundaries; Zone is not Space/Room.
namespace Autodesk.Revit.DB.Mechanical
{
    public sealed class Zone : Autodesk.Revit.DB.Element { }
    public sealed class Space : Autodesk.Revit.DB.SpatialElement { }
}
namespace Autodesk.Revit.DB.Architecture
{
    public sealed class Room : Autodesk.Revit.DB.SpatialElement { }
}