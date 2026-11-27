namespace BIMassist.Ava.Core;

public enum ParameterScope { Instance, Type }
public sealed record GroupingParameterOption(string Name, ParameterScope Scope)
{
    public string DisplayLabel => Name + " (" + (Scope == ParameterScope.Instance ? "Instanz" : "Typ") + ")";
}
