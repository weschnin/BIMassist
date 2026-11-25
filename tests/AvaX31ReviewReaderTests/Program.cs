using System.Text;
using System.Xml.Linq;
using BIMassist.Ava.Core;

static class Program
{
    private const string Gaeb = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";

    private static X31ReviewDocument Review(string remark, string? explanation = null)
    {
        // Fixed REB columns: method 91 at 29..30, numeric remark at 13..21.
        var row = (new string(' ', 13) + remark.PadRight(9) + new string(' ', 7) + "91" + "1=".PadRight(38)).PadRight(80);
        var item = new XElement(XName.Get("QDetermItem", Gaeb),
            new XElement(XName.Get("QTakeoff", Gaeb), new XAttribute("Row", row)));
        if (explanation is not null) item.Add(new XElement(XName.Get("Explanation", "BVBS"), explanation));
        var xml = new XDocument(new XElement(XName.Get("GAEB", Gaeb),
            new XElement(XName.Get("GAEBInfo", Gaeb),
                new XElement(XName.Get("Version", Gaeb), "3.3"),
                new XElement(XName.Get("VersDate", Gaeb), "2023-01")),
            new XElement(XName.Get("BoQ", Gaeb),
                new XElement(XName.Get("Item", Gaeb), new XAttribute("ID", "item"),
                    new XElement(XName.Get("QtyDeterm", Gaeb), item)))));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
        return X31ReviewReader.Read(input);
    }

    private static void Test(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    public static int Main()
    {
        Test("numeric remark without a labelled ID explanation is not inferred as a Revit ElementId", () =>
        {
            var review = Review("429496729");
            var quantity = review.Rows.Single();
            if (quantity.ElementId != null || quantity.ElementIdsForSelection.Count != 0 ||
                X31ReviewReader.FindMissingElementIds(review, _ => false).Count != 0 ||
                !review.Warnings.Any(warning => warning.Contains("keine gültige Revit-ElementID", StringComparison.Ordinal)))
                throw new Exception("Ordinary numeric REB text was fabricated into a model ElementId.");
        });
        Test("numeric remark conflicting with BVBS explanation rejects the entire review", () =>
        {
            try
            {
                Review("42", "Revit-ElementID: 43");
                throw new Exception("A contradictory ID was silently accepted.");
            }
            catch (InvalidDataException ex)
            {
                if (!ex.Message.Contains("42", StringComparison.Ordinal) ||
                    !ex.Message.Contains("43", StringComparison.Ordinal) ||
                    !ex.Message.Contains("Zeile 1", StringComparison.Ordinal))
                    throw new Exception("The conflict diagnostic must identify the row and both IDs.", ex);
            }
        });
        Test("matching numeric remark and explanation yield one included ID", () =>
        {
            var review = Review("42", "Revit-ElementID: 42");
            if (review.Rows.Single().ElementId != "42" || review.Warnings.Count != 0 ||
                !review.Rows.Single().ElementIdsForSelection.SequenceEqual(["42"]))
                throw new Exception("Matching ID sources were not reconciled.");
        });
        Test("leading-zero remark is compared numerically and exposed canonically", () =>
        {
            var review = Review("00042", "Revit-ElementID: 42");
            if (review.Rows.Single().ElementId != "42")
                throw new Exception("Equivalent numeric IDs were not reconciled.");
        });
        Test("legacy nonnumeric remark retains BVBS explanation ID", () =>
        {
            var review = Review("legacy", "Revit-ElementID: 42");
            if (review.Rows.Single().ElementId != "42")
                throw new Exception("Legacy explanation-only identity was lost.");
        });
        Test("malformed explanation cannot override numeric remark", () =>
        {
            try { Review("42", "Revit-ElementID: invalid"); }
            catch (InvalidDataException ex) when (ex.Message.Contains("42", StringComparison.Ordinal)) { return; }
            throw new Exception("Malformed explanation did not fail closed.");
        });
        return 0;
    }
}
