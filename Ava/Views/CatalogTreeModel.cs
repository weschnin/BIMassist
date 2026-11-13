using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Views;

internal sealed class CatalogTreeNode(string key, string label, string sourceId = "")
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string SourceId { get; } = sourceId;
    public List<CatalogTreeNode> Children { get; } = [];
}

internal static class CatalogTreeModel
{
    private static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";

    public static List<CatalogTreeNode> BuildBgk(AvaTemplate template, string filter = "")
    {
        var entries = template.BgkCatalog.Entries
            .Where(bgk => bgk.Key != "///")
            .ToDictionary(bgk => bgk.Key, StringComparer.Ordinal);
        var nodes = entries.Values.ToDictionary(bgk => bgk.Key, bgk =>
        {
            var root = new CatalogTreeNode("bgk:" + bgk.Key, bgk.Key + " " + bgk.Description, bgk.Key);
            foreach (var type in bgk.Types)
                root.Children.Add(new("typ:" + bgk.Key + ":" + type.Number, "Typ " + type.Number + " — " + type.Description, type.Number));
            return root;
        }, StringComparer.Ordinal);
        var roots = new List<CatalogTreeNode>();
        foreach (var bgk in entries.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var separator = bgk.Key.LastIndexOf('-');
            string parentKey = separator > 0 ? bgk.Key[..separator] : "";
            if (nodes.TryGetValue(parentKey, out var parent)) parent.Children.Add(nodes[bgk.Key]);
            else roots.Add(nodes[bgk.Key]);
        }
        roots = roots.OrderBy(node => node.Key, StringComparer.Ordinal).ToList();
        return Filter(roots, filter);
    }

    public static List<CatalogTreeNode> BuildLv(AvaTemplate template, string filter = "")
    {
        var entries = template.LvCatalog.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var roots = TryBuildSnapshot(template, entries) ?? BuildFromEntries(entries.Values);
        return Filter(roots, filter);
    }

    private static List<CatalogTreeNode>? TryBuildSnapshot(AvaTemplate template, IReadOnlyDictionary<string, LvEntry> entries)
    {
        var encoded = template.LvCatalog.Source?.X82SnapshotBase64;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > ((CatalogReaders.MaxBytes + 2) / 3) * 4) return null;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > CatalogReaders.MaxBytes) return null;
            var expectedHash = template.LvCatalog.Source!.Sha256;
            if (expectedHash.Length != 64 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = new MemoryStream(bytes, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = CatalogReaders.MaxBytes });
            var document = XDocument.Load(reader);
            if (document.Root?.Name != Ns + "GAEB") return null;
            var result = new List<CatalogTreeNode>();
            foreach (var boq in document.Descendants(Ns + "BoQ"))
            {
                var boqId = (string?)boq.Attribute("ID") ?? "";
                var title = ReadText(boq.Element(Ns + "BoQInfo")?.Element(Ns + "Name"));
                var root = new CatalogTreeNode("boq:" + boqId, "LV " + (string.IsNullOrWhiteSpace(title) ? boqId : title), boqId);
                foreach (var body in boq.Elements(Ns + "BoQBody")) AddBody(body, root, entries);
                result.Add(root);
            }
            return result;
        }
        catch (Exception ex) when (ex is FormatException or XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static void AddBody(XElement body, CatalogTreeNode parent, IReadOnlyDictionary<string, LvEntry> entries)
    {
        foreach (var category in body.Elements(Ns + "BoQCtgy"))
        {
            var part = (string?)category.Attribute("RNoPart") ?? "?";
            var title = category.Elements().Where(element => element.Name == Ns + "LblTx" || element.Name == Ns + "OutlineText" || element.Name == Ns + "DetailTxt")
                .Select(ReadText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";
            var node = new CatalogTreeNode("category:" + category.Attribute("ID")?.Value,
                part + (title.Length == 0 ? "" : " " + title), (string?)category.Attribute("ID") ?? "");
            parent.Children.Add(node);
            foreach (var child in category.Elements(Ns + "BoQBody")) AddBody(child, node, entries);
        }
        foreach (var itemlist in body.Elements(Ns + "Itemlist"))
        foreach (var item in itemlist.Elements(Ns + "Item"))
        {
            var id = (string?)item.Attribute("ID") ?? "";
            if (!entries.TryGetValue(id, out var entry)) continue;
            parent.Children.Add(new("item:" + id, entry.Ordinal + " " + entry.Text, id));
        }
    }

    private static List<CatalogTreeNode> BuildFromEntries(IEnumerable<LvEntry> values)
    {
        var roots = new List<CatalogTreeNode>();
        var boqNodes = new Dictionary<string, CatalogTreeNode>(StringComparer.Ordinal);
        var pathNodes = new Dictionary<string, CatalogTreeNode>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            if (!boqNodes.TryGetValue(entry.BoqId, out var boq))
            {
                boq = new("boq:" + entry.BoqId, "LV " + entry.BoqId, entry.BoqId);
                boqNodes.Add(entry.BoqId, boq); roots.Add(boq);
            }
            CatalogTreeNode parent = boq;
            var sectionCount = Math.Max(1, entry.NumberParts.Count - 1);
            for (var i = 0; i < sectionCount; i++)
            {
                var parts = entry.NumberParts.Take(i + 1).ToArray();
                if (parts.Length == 0) parts = [entry.Ordinal];
                var sectionNumber = string.Join(".", parts);
                var path = entry.BoqId + "\0" + sectionNumber;
                if (!pathNodes.TryGetValue(path, out var section))
                {
                    section = new("section:" + path, sectionNumber);
                    pathNodes.Add(path, section); parent.Children.Add(section);
                }
                parent = section;
            }
            parent.Children.Add(new("item:" + entry.Id, entry.Ordinal + " " + entry.Text, entry.Id));
        }
        return roots;
    }

    public static IReadOnlyList<string> GetPositionIds(CatalogTreeNode node)
    {
        var ids = new List<string>();
        void Visit(CatalogTreeNode current)
        {
            if (current.Key.StartsWith("item:", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(current.SourceId)) ids.Add(current.SourceId);
                return;
            }
            foreach (var child in current.Children) Visit(child);
        }
        Visit(node);
        return ids.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static List<CatalogTreeNode> Filter(IEnumerable<CatalogTreeNode> nodes, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return nodes.ToList();
        var terms = filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Filter(nodes, terms, new HashSet<int>());
    }

    private static List<CatalogTreeNode> Filter(IEnumerable<CatalogTreeNode> nodes, string[] terms, HashSet<int> inheritedMatches)
    {
        var result = new List<CatalogTreeNode>();
        foreach (var source in nodes)
        {
            var matches = new HashSet<int>(inheritedMatches);
            for (var index = 0; index < terms.Length; index++)
                if (source.Label.Contains(terms[index], StringComparison.OrdinalIgnoreCase)) matches.Add(index);
            if (matches.Count == terms.Length)
            {
                result.Add(Clone(source));
                continue;
            }
            var children = Filter(source.Children, terms, matches);
            if (children.Count == 0) continue;
            var copy = new CatalogTreeNode(source.Key, source.Label, source.SourceId);
            copy.Children.AddRange(children);
            result.Add(copy);
        }
        return result;
    }

    private static CatalogTreeNode Clone(CatalogTreeNode node)
    {
        var copy = new CatalogTreeNode(node.Key, node.Label, node.SourceId);
        foreach (var child in node.Children) copy.Children.Add(Clone(child));
        return copy;
    }

    private static string ReadText(XElement? element)
    {
        if (element == null) return "";
        string ReadNode(XNode node) => node switch
        {
            XText text => text.Value,
            XElement child when child.Name.LocalName == "br" => " ",
            XElement child =>
                (child.Name.LocalName is "p" or "div" ? " " : "") +
                string.Concat(child.Nodes().Select(ReadNode)) +
                (child.Name.LocalName is "p" or "div" ? " " : ""),
            _ => ""
        };
        var raw = string.Concat(element.Nodes().Select(ReadNode));
        return string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
