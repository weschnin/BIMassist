using System.Text.Json.Serialization;

namespace BIMassist.Ava.Core;

public enum ParameterKind { SharedGuid, LocalFamily, BuiltIn }
public enum ParameterScope { Instance, Type }
public enum ConditionOperator { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual }
public enum OperandKind { Numeric, Boolean, Text }
public enum QuantityKind { Parameter, Constant }

public sealed class ParameterSource
{
    [JsonRequired] public ParameterKind Kind { get; set; } = ParameterKind.SharedGuid;
    [JsonRequired] public Guid SharedGuid { get; set; }
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Spec { get; set; } = "TEXT";
    [JsonRequired] public ParameterScope Scope { get; set; } = ParameterScope.Type;
    [JsonRequired] public string BuiltInId { get; set; } = "";
    public override string ToString() => $"{Name} | {Spec} | {Scope} | " + (Kind == ParameterKind.SharedGuid ? SharedGuid.ToString() : Kind == ParameterKind.BuiltIn ? BuiltInId : "lokale Familie (kein GUID-Fallback)");
}
public sealed class RuleCondition
{
    [JsonRequired] public ParameterSource Source { get; set; } = new();
    [JsonRequired] public ConditionOperator Operator { get; set; }
    [JsonRequired] public OperandKind Operand { get; set; }
    [JsonRequired] public decimal Number { get; set; }
    [JsonRequired] public bool Boolean { get; set; }
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public string Unit { get; set; } = "";
    public override string ToString() => $"{Source.Name} {Operator} " + (Operand == OperandKind.Numeric ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Unit : Operand == OperandKind.Boolean ? Boolean.ToString() : Text);
}
public sealed class QuantitySource
{
    [JsonRequired] public QuantityKind Kind { get; set; } = QuantityKind.Parameter;
    [JsonRequired] public ParameterSource? Source { get; set; } = new();
    [JsonRequired] public decimal? ConstantValue { get; set; } = null;
    [JsonRequired] public string Unit { get; set; } = "";
    public override string ToString() => Kind == QuantityKind.Constant
        ? $"Konstante {ConstantValue?.ToString(System.Globalization.CultureInfo.GetCultureInfo("de-DE")) ?? "nicht definiert"} {Unit} je passendem Element"
        : Source?.ToString() ?? "nicht definiert";
}
public sealed class MappingRule
{
    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired] public string BgkKey { get; set; } = "";
    [JsonRequired] public string TypeNumber { get; set; } = "";
    [JsonRequired] public string LvItemId { get; set; } = "";
    [JsonRequired] public List<RuleCondition> Conditions { get; set; } = [];
    [JsonRequired] public QuantitySource? Quantity { get; set; }
    public override string ToString() => $"{BgkKey} / {TypeNumber} → {LvItemId} | UND: {Conditions?.Count ?? 0} | Menge: {Quantity?.ToString() ?? "nicht definiert"}";
}
public sealed class AvaTemplate
{
    [JsonRequired] public int SchemaVersion { get; set; } = 2;
    [JsonRequired] public Guid ProfileId { get; set; } = Guid.NewGuid();
    [JsonRequired] public string Name { get; set; } = "Neue AVA-Vorlage";
    [JsonRequired] public string Version { get; set; } = "1.0";
    // Independent scope is deliberately NOT inferred from existing identification values.
    [JsonRequired] public string ReviewScope { get; set; } = "";
    [JsonRequired] public ParameterSource BgkIdentification { get; set; } = new() { Kind = ParameterKind.BuiltIn, Name = "Baugruppenkennzeichen", BuiltInId = "ASSEMBLY_CODE", Spec = "TEXT", Scope = ParameterScope.Type };
    [JsonRequired] public ParameterSource TypeIdentification { get; set; } = new() { Kind = ParameterKind.BuiltIn, Name = "Typnummer", BuiltInId = "ALL_MODEL_TYPE_COMMENTS", Spec = "TEXT", Scope = ParameterScope.Type };
    [JsonRequired] public Catalog<BgkEntry> BgkCatalog { get; set; } = new(new("", "", ""), []);
    [JsonRequired] public Catalog<LvEntry> LvCatalog { get; set; } = new(new("", "", ""), []);
    [JsonRequired] public Catalog<SharedParameter> ParameterCatalog { get; set; } = new(new("", "", ""), []);
    [JsonRequired] public List<MappingRule> Mappings { get; set; } = [];
}
