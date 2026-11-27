namespace Autodesk.Revit.DB;
// Controlled API doubles for source-resolution/conversion policy, not proof of Revit-host execution.
public record ForgeTypeId(string Value) { public string TypeId => Value; }
public enum StorageType { None, Integer, Double, String, ElementId }
public enum BuiltInParameter { INVALID = -1, ASSEMBLY_CODE = -2, ALL_MODEL_TYPE_COMMENTS = -3, ALL_MODEL_DESCRIPTION = -4, HOST_VOLUME_COMPUTED = -5, RBS_PIPE_DIAMETER_PARAM = -6 }
public enum CategoryType { Model, Annotation }
public sealed class Category { public ElementId Id { get; set; } = new(-1); public CategoryType CategoryType { get; set; } = CategoryType.Model; public string Name { get; set; } = "Model"; }
public static class SpecTypeId
{
    public static ForgeTypeId Length = new("LENGTH"), PipeSize = new("PIPE_SIZE"), PipeDimension = new("PIPE_DIMENSION"), Area = new("AREA"), Volume = new("VOLUME"), Angle = new("ANGLE"), Number = new("NUMBER"), MassDensity = new("MASS_DENSITY");
    public static class String { public static ForgeTypeId Text = new("TEXT"); }
    public static class Boolean { public static ForgeTypeId YesNo = new("YESNO"); }
    public static class Int { public static ForgeTypeId Integer = new("INTEGER"); }
}
public static class UnitTypeId
{
    public static ForgeTypeId Millimeters = new("mm"), Centimeters = new("cm"), Meters = new("m"), Feet = new("ft"), SquareMillimeters = new("mm2"), SquareCentimeters = new("cm2"), SquareMeters = new("m2"), SquareFeet = new("ft2"), CubicCentimeters = new("cm3"), CubicMeters = new("m3"), CubicFeet = new("ft3"), Degrees = new("deg"), Radians = new("rad"), KilogramsPerCubicMeter = new("kg/m3");
}
public static class UnitUtils
{
    public static bool IsValidUnit(ForgeTypeId spec, ForgeTypeId unit) => spec.Value switch
    {
        "LENGTH" or "PIPE_SIZE" or "PIPE_DIMENSION" => unit.Value is "mm" or "cm" or "m" or "km" or "ft",
        "AREA" => unit.Value is "mm2" or "cm2" or "m2" or "ft2",
        "VOLUME" => unit.Value is "cm3" or "m3" or "ft3",
        "ANGLE" => unit.Value is "deg" or "rad",
        "MASS_DENSITY" => unit.Value == "kg/m3",
        _ => false
    };
    public static ISet<ForgeTypeId> GetAllUnits() => new HashSet<ForgeTypeId> { new("feet") };
    private static double ToInternal(double value, ForgeTypeId unit) => value / (unit.Value switch
    {
        "mm" => 304.8, "cm" => 30.48, "m" => 0.3048, "km" => 0.0003048, "ft" => 1,
        "mm2" => 92903.04, "cm2" => 929.0304, "m2" => 0.09290304, "ft2" => 1,
        "cm3" => 28316.846592, "m3" => 0.028316846592, "ft3" => 1,
        "deg" => 180 / Math.PI, "rad" => 1, "kg/m3" => 1, _ => throw new ArgumentException("Unsupported test unit")
    });
    public static double ConvertFromInternalUnits(double value, ForgeTypeId unit) => !double.IsFinite(value) ? throw new ArgumentException("Nonfinite test input") : value * (unit.Value switch
    {
        "mm" => 304.8, "cm" => 30.48, "m" => 0.3048, "km" => 0.0003048, "ft" => 1,
        "mm2" => 92903.04, "cm2" => 929.0304, "m2" => 0.09290304, "ft2" => 1,
        "cm3" => 28316.846592, "m3" => 0.028316846592, "ft3" => 1,
        "deg" => 180 / Math.PI, "rad" => 1, "kg/m3" => 1, _ => throw new ArgumentException("Unsupported test unit")
    });
    public static double Convert(double value, ForgeTypeId from, ForgeTypeId to) =>
        !double.IsFinite(value) ? throw new ArgumentException("Nonfinite test input") : ConvertFromInternalUnits(ToInternal(value, from), to);
}
public sealed class FormatOptions(ForgeTypeId unit) { public ForgeTypeId GetUnitTypeId() => unit; }
public sealed class Units
{
    public string? LastRequestedSpec { get; private set; }
    public Dictionary<string, ForgeTypeId> ProjectUnits { get; } = new()
    {
        ["LENGTH"] = UnitTypeId.Feet, ["AREA"] = UnitTypeId.SquareFeet, ["VOLUME"] = UnitTypeId.CubicFeet,
        ["ANGLE"] = UnitTypeId.Degrees, ["MASS_DENSITY"] = UnitTypeId.KilogramsPerCubicMeter
    };
    public FormatOptions GetFormatOptions(ForgeTypeId spec)
    {
        LastRequestedSpec = spec.Value;
        var key = spec.Value switch { "PIPE_SIZE" or "PIPE_DIMENSION" => "LENGTH", _ => spec.Value };
        return new(ProjectUnits.GetValueOrDefault(key) ?? throw new ArgumentException("No project format for spec"));
    }
}
public static class LabelUtils { public static string GetLabelForUnit(ForgeTypeId unit) => unit.Value == "feet" ? "Feet" : unit.Value; }
public sealed class Definition(string name, ForgeTypeId type)
{
    public string Name { get; } = name;
    public Exception? DataTypeError { get; set; }
    public ForgeTypeId GetDataType() => DataTypeError is { } error ? throw error : type;
}
public sealed class Parameter(string name, ForgeTypeId spec, object? value, StorageType storage, Guid guid = default)
{
    private ElementId id = new(10);
    private bool hasValue = value != null;
    public Exception? IdentityError { get; init; }
    public Exception? ValueStateError { get; init; }
    public Exception? ReadError { get; init; }
    public ElementId Id
    {
        get => IdentityError is { } error ? throw error : id;
        set => id = value;
    }
    public Definition Definition { get; } = new(name, spec);
    // Revit exposes HasValue separately from storage/spec and the default returned by AsDouble.
    public bool HasValue
    {
        get => ValueStateError is { } error ? throw error : hasValue;
        init => hasValue = value;
    }
    public bool IsShared => guid != Guid.Empty;
    public Guid GUID => guid;
    public StorageType StorageType { get; } = storage;
    public string? AsString() => ReadError is { } error ? throw error : (string?)value;
    public int AsInteger() => (int)value!;
    public double AsDouble() => ReadError is { } error ? throw error : (double)value!;
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
    public HashSet<long> VisibleInViews { get; } = [];
    public HashSet<long> HiddenInViews { get; } = [];
    public List<Parameter> Parameters { get; } = [];
    public Dictionary<BuiltInParameter, Parameter> BuiltIns { get; } = [];
    public Parameter? get_Parameter(Guid guid) => Parameters.SingleOrDefault(p => p.IsShared && p.GUID == guid);
    public Parameter? get_Parameter(BuiltInParameter id) => BuiltIns.GetValueOrDefault(id);
    public IList<Parameter> GetParameters(string name) => Parameters.Concat(BuiltIns.Values).Where(p => p.Definition.Name == name).ToList();
    public ElementId GetTypeId() => TypeId;
    public bool IsHidden(View view) => HiddenInViews.Contains(view.Id.Value);
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
    public bool IsModifiable { get; set; }
    public Units ProjectUnits { get; } = new();
    public Units GetUnits() => ProjectUnits;
    public int CollectionPasses { get; set; }
    public List<Element> Elements { get; } = [];
    public Element? GetElement(ElementId id) => Elements.SingleOrDefault(e => e.Id == id);
}
public sealed class View : Element
{
    public bool IsTemplate { get; set; }
    public bool TemporaryHideIsolateActive { get; set; }
    public HashSet<long> TemporaryVisibleElementIds { get; } = [];
    public HashSet<long> HiddenCategoryIds { get; } = [];
    public bool IsInTemporaryViewMode(TemporaryViewMode mode) =>
        mode == TemporaryViewMode.TemporaryHideIsolate && TemporaryHideIsolateActive;
    public bool GetCategoryHidden(ElementId categoryId) => HiddenCategoryIds.Contains(categoryId.Value);
    public bool IsElementVisibleInTemporaryViewMode(TemporaryViewMode mode, ElementId id) =>
        mode == TemporaryViewMode.TemporaryHideIsolate && TemporaryVisibleElementIds.Contains(id.Value);
}
public enum TemporaryViewMode { TemporaryHideIsolate }
public sealed class FilteredElementCollector : IEnumerable<Element>, IDisposable
{
    private readonly Document document;
    private readonly ElementId? viewId;
    public FilteredElementCollector(Document document) { this.document = document; }
    public FilteredElementCollector(Document document, ElementId viewId) { this.document = document; this.viewId = viewId; }
    public static bool IsViewValidForElementIteration(Document document, ElementId viewId) =>
        document.GetElement(viewId) is View { IsTemplate: false };
    public ElementId? ViewId => viewId;
    public FilteredElementCollector WhereElementIsNotElementType() => this;
    public IEnumerator<Element> GetEnumerator()
    {
        document.CollectionPasses++;
        return document.Elements.Where(e => e is not ElementType && (viewId == null || e.VisibleInViews.Contains(viewId.Value))).GetEnumerator();
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { }
}
