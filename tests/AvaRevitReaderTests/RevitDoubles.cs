namespace Autodesk.Revit.DB;
// Controlled API doubles for source-resolution/conversion policy, not proof of Revit-host execution.
public record ForgeTypeId(string Value);
public enum StorageType { None, Integer, Double, String, ElementId }
public enum BuiltInParameter { INVALID = -1, ASSEMBLY_CODE = -2, ALL_MODEL_TYPE_COMMENTS = -3, ALL_MODEL_DESCRIPTION = -4, HOST_VOLUME_COMPUTED = -5 }
public enum CategoryType { Model, Annotation }
public sealed class Category { public CategoryType CategoryType { get; set; } = CategoryType.Model; }
public static class SpecTypeId
{
    public static ForgeTypeId Length = new("LENGTH"), Area = new("AREA"), Volume = new("VOLUME"), Angle = new("ANGLE"), Number = new("NUMBER"), MassDensity = new("MASS_DENSITY");
    public static class String { public static ForgeTypeId Text = new("TEXT"); }
    public static class Boolean { public static ForgeTypeId YesNo = new("YESNO"); }
    public static class Int { public static ForgeTypeId Integer = new("INTEGER"); }
}
public static class UnitTypeId
{
    public static ForgeTypeId Millimeters = new("mm"), Centimeters = new("cm"), Meters = new("m"), SquareMillimeters = new("mm2"), SquareCentimeters = new("cm2"), SquareMeters = new("m2"), CubicCentimeters = new("cm3"), CubicMeters = new("m3"), Degrees = new("deg"), Radians = new("rad"), KilogramsPerCubicMeter = new("kg/m3");
}
public static class UnitUtils
{
    public static double ConvertFromInternalUnits(double value, ForgeTypeId unit) => !double.IsFinite(value) ? throw new ArgumentException("Nonfinite test input") : value * (unit.Value switch
    {
        "mm" => 304.8, "cm" => 30.48, "m" => 0.3048, "km" => 0.0003048,
        "mm2" => 92903.04, "cm2" => 929.0304, "m2" => 0.09290304,
        "cm3" => 28316.846592, "m3" => 0.028316846592,
        "deg" => 180 / Math.PI, "rad" => 1, "kg/m3" => 1, _ => throw new ArgumentException("Unsupported test unit")
    });
}
public sealed class Definition(string name, ForgeTypeId type)
{
    public string Name { get; } = name;
    public ForgeTypeId GetDataType() => type;
}
public sealed class Parameter(string name, ForgeTypeId spec, object? value, StorageType storage, Guid guid = default)
{
    public ElementId Id { get; set; } = new(10);
    public Definition Definition { get; } = new(name, spec);
    // Revit exposes HasValue separately from storage/spec and the default returned by AsDouble.
    public bool HasValue { get; init; } = value != null;
    public bool IsShared => guid != Guid.Empty;
    public Guid GUID => guid;
    public StorageType StorageType { get; } = storage;
    public string? AsString() => (string?)value;
    public int AsInteger() => (int)value!;
    public double AsDouble() => (double)value!;
}
public sealed record ElementId(long Value);
public sealed record ModelPath(string UserVisiblePath);
public static class ModelPathUtils
{
    public static string ConvertModelPathToUserVisiblePath(ModelPath path) => path.UserVisiblePath;
}
public class Element
{
    public ElementId Id { get; set; } = new(1);
    public string UniqueId { get; set; } = "test-unique-id";
    public string Name { get; set; } = "TestType";
    public Category? Category { get; set; } = new();
    public ElementId TypeId { get; set; } = new(2);
    public List<Parameter> Parameters { get; } = [];
    public Dictionary<BuiltInParameter, Parameter> BuiltIns { get; } = [];
    public Parameter? get_Parameter(Guid guid) => Parameters.SingleOrDefault(p => p.IsShared && p.GUID == guid);
    public Parameter? get_Parameter(BuiltInParameter id) => BuiltIns.GetValueOrDefault(id);
    public IList<Parameter> GetParameters(string name) => Parameters.Concat(BuiltIns.Values).Where(p => p.Definition.Name == name).ToList();
    public ElementId GetTypeId() => TypeId;
}
public sealed class FamilyInstance : Element { }
public sealed class RevitLinkInstance : Element { }
public sealed class ImportInstance : Element { public bool IsLinked { get; init; } }
public class ElementType : Element { }
public sealed class Family { public string Name { get; set; } = "TestFamily"; }
public sealed class FamilySymbol : ElementType { public Family Family { get; set; } = new(); }
public sealed class Document
{
    public string PathName { get; set; } = "";
    public string Title { get; set; } = "Unsaved project";
    public bool IsFamilyDocument { get; set; }
    public List<Element> Elements { get; } = [];
    public Element? GetElement(ElementId id) => Elements.SingleOrDefault(e => e.Id == id);
}
public sealed class FilteredElementCollector(Document document) : IEnumerable<Element>, IDisposable
{
    public FilteredElementCollector WhereElementIsNotElementType() => this;
    public IEnumerator<Element> GetEnumerator() => document.Elements.Where(e => e is not ElementType).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }
}
