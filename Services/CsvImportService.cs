using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BIMassist.Models;

namespace BIMassist.Services
{
    public class CsvImportService
    {
        // erkennt automatisch "," oder ";" als Trennzeichen und liest mit Dezimalpunkt
        private IEnumerable<string[]> ReadCsv(string path)
        {
            using (var sr = new StreamReader(path, System.Text.Encoding.GetEncoding("ISO-8859-1"), true))
            {
                string header = sr.ReadLine();
                if (header == null) yield break;

                char sep = header.Contains(";") && !header.Contains(",") ? ';' : ',';
                yield return SplitCsvLine(header, sep);

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    yield return SplitCsvLine(line, sep);
                }
            }
        }
        // einfache CSV-Zeilen-Parser mit Quote-Unterstützung
        private string[] SplitCsvLine(string line, char sep)
        {
            var result = new List<string>();
            bool inQ = false; var acc = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\"') { inQ = !inQ; continue; }
                if (c == sep && !inQ) { result.Add(acc.ToString()); acc.Clear(); }
                else acc.Append(c);
            }
            result.Add(acc.ToString());
            return result.ToArray();
        }
        public IEnumerable<Borehole> ReadHoles(string path)
        {
            string[] header = null; Func<string, int> idx = name => Array.FindIndex(header, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            foreach (var row in ReadCsv(path))
            {
                if (header == null) { header = row; continue; }
                if (row.Length == 0 || string.IsNullOrWhiteSpace(row[0])) continue;
                var bh = new Borehole
                {
                    LocationID = Get(row, idx("LocationID")),
                    LocationType = Get(row, idx("LocationType")),
                    Easting = ToDouble(Get(row, idx("Easting"))),
                    Northing = ToDouble(Get(row, idx("Northing"))),
                    GroundLevel = ToDouble(Get(row, idx("GroundLevel"))),
                    FinalDepth = ToDouble(Get(row, idx("FinalDepth"))),
                    Orientation = Get(row, idx("Orientation")),
                    Inclination = Get(row, idx("Inclination")),
                };
                yield return bh;
            }
        }
        public IEnumerable<GeologyLayer> ReadGeology(string path)
        {
            string[] header = null; Func<string, int> idx = name => Array.FindIndex(header, h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
            foreach (var row in ReadCsv(path))
            {
                if (header == null) { header = row; continue; }
                if (row.Length == 0 || string.IsNullOrWhiteSpace(row[0])) continue;
                var gl = new GeologyLayer
                {
                    LocationID = Get(row, idx("LocationID")),
                    DepthTop = ToDouble(Get(row, idx("DepthTop"))),
                    DepthBase = ToDouble(Get(row, idx("DepthBase"))),
                    Description = Get(row, idx("Description")),
                    Colour = Get(row, idx("Colour")),
                    Consistency = Get(row, idx("Consistency")),
                    Classification = Get(row, idx("Classification")),
                    GeologyCode = Get(row, idx("GeologyCode")),
                };
                yield return gl;
            }
        }
        private string Get(string[] row, int i)
        {
            return i >= 0 && i < row.Length ? (row[i] == null ? null : row[i].Trim(' ', ' ')) : null;
        }


        private double ToDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0.0;

            // Robuste Bereinigung: geschützte Leerzeichen -> normal, dann Leerzeichen löschen
            s = s.Trim()
                 .Replace('\u00A0', ' ')
                 .Replace(" ", "")
                 .Replace(',', '.'); // Dezimalkomma -> Dezimalpunkt

            double d;
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out d)
                   ? d : 0.0;
        }
    }
}