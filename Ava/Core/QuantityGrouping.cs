namespace BIMassist.Ava.Core;

public enum QuantityGroupingMode { Position, Trade, BuildingArea, FamilyAndType }

public sealed record GroupingParameterOption(string Name, ParameterScope Scope)
{
    public string DisplayLabel => Name + " (" + (Scope == ParameterScope.Instance ? "Instanz" : "Typ") + ")";
}

public sealed record QuantityGroupingSelection(QuantityGroupingMode Mode, GroupingParameterOption? Parameter = null)
{
    public bool IsGrouped => Mode != QuantityGroupingMode.Position;
    public string GroupKind => Mode switch
    {
        QuantityGroupingMode.Trade => "Gewerk",
        QuantityGroupingMode.BuildingArea => "Baubereich",
        QuantityGroupingMode.FamilyAndType => "Familie und Typ",
        _ => ""
    };
}
