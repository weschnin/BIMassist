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
        var lvById = template.LvCatalog.Entries.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var entries = template.BgkCatalog.Entries.Where(entry => entry.Key != "///")
            .ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var nodes = entries.Values.ToDictionary(bgk => bgk.Key, bgk =>
        {
            var bgkNode = new MappingTreeNode("bgk:" + bgk.Key, "BGK", bgk.Key + " " + bgk.Description);
            foreach (var type in bgk.Types.OrderBy(type => type.Number, StringComparer.Ordinal))
            {
                var typeNode = new MappingTreeNode(bgkNode.Key + "/typ:" + type.Number, "Typ", "Typ " + type.Number + " — " + type.Description);
                bgkNode.Children.Add(typeNode);
                foreach (var rule in template.Mappings.Where(rule => rule.BgkKey == bgk.Key && rule.TypeNumber == type.Number))
                {
                    var conditions = rule.Conditions.Count == 0 ? "Ohne Bedingung" : string.Join(" UND ", rule.Conditions);
                    var ruleNode = new MappingTreeNode("rule:" + rule.Id, "Zuordnung", conditions + $"  ({rule.Positions.Count} Leistungspositionen)", rule);
                    typeNode.Children.Add(ruleNode);
                    foreach (var position in rule.Positions)
                    {
                        lvById.TryGetValue(position.LvItemId, out var item);
                        ruleNode.Children.Add(new("position:" + rule.Id + ":" + position.Id, "Position",
                            (item?.Ordinal ?? position.LvItemId) + " " + item?.Text + "\nMenge: " + (position.Quantity?.ToString() ?? "noch offen"), rule, position));
                    }
                }
            }
            return bgkNode;
        }, StringComparer.Ordinal);
        var result = new List<MappingTreeNode>();
        foreach (var bgk in entries.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var separator = bgk.Key.LastIndexOf('-');
            string parentKey = separator > 0 ? bgk.Key[..separator] : "";
            if (nodes.TryGetValue(parentKey, out var parent)) parent.Children.Add(nodes[bgk.Key]);
            else result.Add(nodes[bgk.Key]);
        }
        return result.OrderBy(node => node.Key, StringComparer.Ordinal).ToList();
    }
}
