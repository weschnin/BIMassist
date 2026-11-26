using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BIMassist.Ava.Core;

namespace BIMassist.Ava.Views;

/// <summary>Read-only LV presentation. IDs and source units are never rewritten.</summary>
internal static class LvPositionDisplay
{
    internal static string Heading(LvEntry entry) => entry.Ordinal + " " + entry.Text + " (" + entry.Unit + ")";

    internal static string Details(Catalog<LvEntry> catalog, LvEntry entry) => Heading(entry) + "\n\n" + LongText(catalog, entry);

    internal static string LongText(Catalog<LvEntry> catalog, LvEntry entry)
    {
        // Legacy JSON already stores plain text. Never strip '<...>' or decode entities
        // in that text: literal comparisons and escaped examples are valid LV content.
        var encoded = catalog.Source?.X82SnapshotBase64;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > ((CatalogReaders.MaxBytes + 2) / 3) * 4) return entry.Description;
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > CatalogReaders.MaxBytes || !Convert.ToHexString(SHA256.HashData(bytes))
                .Equals(catalog.Source!.Sha256, StringComparison.OrdinalIgnoreCase)) return entry.Description;
            using var stream = new MemoryStream(bytes, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = CatalogReaders.MaxBytes
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XNamespace ns = "http://www.gaeb.de/GAEB_DA_XML/DA82/3.3";
            if (document.Root?.Name != ns + "GAEB") return entry.Description;
            var items = document.Descendants(ns + "Item").Where(item => (string?)item.Attribute("ID") == entry.Id
                && (string?)item.Ancestors(ns + "BoQ").FirstOrDefault()?.Attribute("ID") == entry.BoqId).Take(2).ToArray();
            var detail = items.Length == 1 ? items[0].Descendants(ns + "DetailTxt").FirstOrDefault() : null;
            return detail == null ? entry.Description : PlainText(detail);
        }
        catch (Exception ex) when (ex is FormatException or XmlException or InvalidOperationException or ArgumentException)
        {
            return entry.Description;
        }
    }

    private static string PlainText(XElement element)
    {
        var text = new StringBuilder();
        void Boundary()
        {
            if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
        }
        void Read(XNode node)
        {
            if (node is XText literal)
            {
                // Ignore only XML indentation between blocks, not whitespace separating
                // inline spans, nor explicitly preserved source whitespace.
                bool preserve = literal.Ancestors().Select(parent => (string?)parent.Attribute(XNamespace.Xml + "space"))
                    .FirstOrDefault(value => value != null) == "preserve";
                if (!preserve && string.IsNullOrWhiteSpace(literal.Value) && literal.Value.Contains('\n')
                    && literal.Parent?.HasElements == true)
                {
                    // Pretty-printed p/span indentation is not LV text. A line break
                    // between two inline elements still separates their words.
                    if (literal.PreviousNode is XElement previous && previous.Name.LocalName is not ("p" or "div" or "br")
                        && literal.NextNode is XElement next && next.Name.LocalName is not ("p" or "div" or "br")
                        && text.Length > 0 && !char.IsWhiteSpace(text[^1])) text.Append(' ');
                    return;
                }
                text.Append(literal.Value); return;
            }
            if (node is not XElement child) return;
            if (child.Name.LocalName == "br") { text.Append('\n'); return; }
            bool block = child.Name.LocalName is "p" or "div";
            if (block) Boundary();
            if (block && !child.Nodes().Any()) text.Append('\n');
            foreach (var nested in child.Nodes()) Read(nested);
            if (block) Boundary();
        }
        foreach (var node in element.Nodes()) Read(node);
        return text.ToString().Trim('\r', '\n');
    }
}