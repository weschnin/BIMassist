using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace AWESBox.Services
{
    internal static class MaterialUtils
    {
        /// <summary>
        /// Sucht ein existentes Boden-Schicht-Material (ohne/mit Prefix "Bodenschicht - ")
        /// und legt es nur bei Bedarf neu an. Rückgabe: Material (für Eigenschaften)
        /// </summary>
        internal static Material FindOrCreateBoreLayerMaterial(Document doc, string rawName, string prefix = "Bodenschicht - ")
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var baseName = SanitizeNameForRevit(rawName);

            var allMats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();

            // 1) exakt ohne Prefix?
            var hit = allMats.FirstOrDefault(m => m.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            // 2) exakt mit Prefix?
            var prefixed = prefix + baseName;
            hit = allMats.FirstOrDefault(m => m.Name.Equals(prefixed, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            // 3) neu anlegen – IMMER mit Prefix (keine eigene Transaction öffnen!)
            var candidate = SanitizeNameForRevit(prefixed);
            int i = 2;
            while (allMats.Any(m => m.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                candidate = $"{prefixed}_{i++}";

            var id = Material.Create(doc, candidate);
            return (Material)doc.GetElement(id);
        }

        /// <summary>Convenience: direkt die ElementId des Materials.</summary>
        internal static ElementId FindOrCreateBoreLayerMaterialId(Document doc, string rawName, string prefix = "Bodenschicht - ")
            => FindOrCreateBoreLayerMaterial(doc, rawName, prefix).Id;

        /// <summary>
        /// Sanitize wie in RevitExtrusionService, damit Namen 1:1 matchen.
        /// </summary>
        internal static string SanitizeNameForRevit(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Unbenannt";
            var cleaned = Regex.Replace(s, @"[^\p{L}\p{Nd}\s._-]", "_");
            cleaned = Regex.Replace(cleaned, @"_+", "_");
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '_', '.');
            if (string.IsNullOrEmpty(cleaned)) cleaned = "Unbenannt";
            if (cleaned.Length > 240) cleaned = cleaned.Substring(0, 240);
            return cleaned;
        }
    }
}
