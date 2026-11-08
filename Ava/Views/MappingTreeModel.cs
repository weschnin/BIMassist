using BIMassist.Ava.Core;

namespace BIMassist.Ava.Views;

/// <summary>Display-only hierarchy; never combines or rewrites independent mapping rules.</summary>
internal sealed class MappingTreeNode(string key, string kind, string label, MappingRule? rule = null, MappingPosition? position = null)
{
    public string Key { get; } = key;
    public string Kind { get; } = kind;
    public string Label { get; } = label;
    public MappingRule? Rule { get; } = rule;
    public MappingPosition? Position { get; } = position;
    public List<MappingTreeNode> Children { get; } = [];
}

internal static class MappingTreeModel
{
    public static List<MappingTreeNode> Build(AvaTemplate template)
    {
        var result = new List<MappingTreeNode>();
        var lvById = template.LvCatalog.Entries.ToDictionary(l => l.Id, StringComparer.Ordinal);
        foreach (var bgkGroup in template.Mappings.GroupBy(r => r.BgkKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var bgk = template.BgkCatalog.Entries.FirstOrDefault(b => b.Key == bgkGroup.Key);
            var bgkNode = new MappingTreeNode("bgk:" + bgkGroup.Key, "BGK", bgkGroup.Key + " — " + bgk?.Description);
            result.Add(bgkNode);
            foreach (var typeGroup in bgkGroup.GroupBy(r => r.TypeNumber).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var type = bgk?.Types.FirstOrDefault(t => t.Number == typeGroup.Key);
                var typeNode = new MappingTreeNode(bgkNode.Key + "/typ:" + typeGroup.Key, "Typ", "Typ " + typeGroup.Key + " — " + type?.Description);
                bgkNode.Children.Add(typeNode);
                foreach (var rule in typeGroup)
                {
                    var conditions = rule.Conditions.Count == 0 ? "Ohne Bedingung" : string.Join(" UND ", rule.Conditions);
                    var ruleNode = new MappingTreeNode("rule:" + rule.Id, "Zuordnung", conditions + $"  ({rule.Positions.Count} Leistungspositionen)", rule);
                    typeNode.Children.Add(ruleNode);
                    foreach (var position in rule.Positions)
                    {
                        lvById.TryGetValue(position.LvItemId, out var item);
                        ruleNode.Children.Add(new("position:" + rule.Id + ":" + position.Id, "Position",
                            (item?.Ordinal ?? position.LvItemId) + " — " + item?.Text + "\nMenge: " + (position.Quantity?.ToString() ?? "noch offen"), rule, position));
                    }
                }
            }
        }
        return result;
    }
}
