using System.Text;
using System.Xml.Linq;
using BIMassist.Ava.Core;

static class Program
{
    private const string Gaeb = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";

    private static X31ReviewDocument Review(string remark, string? explanation = null,
        IReadOnlyDictionary<string, X31ReviewPosition>? positions = null, string boqId = "")
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
            new XElement(XName.Get("BoQ", Gaeb), boqId.Length == 0 ? null : new XAttribute("ID", boqId),
                new XElement(XName.Get("Item", Gaeb), new XAttribute("ID", "item"),
                    new XElement(XName.Get("QtyDeterm", Gaeb), item)))));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
        return X31ReviewReader.Read(input, positions: positions);
    }

    private static void Test(string name, Action test)
    {
        test();
        Console.WriteLine("PASS " + name);
    }

    private static XElement Calculation(string approach, string remark = "", XElement? explanation = null, string method = "91") =>
        new(XName.Get("QDetermItem", Gaeb), new XElement(XName.Get("QTakeoff", Gaeb), new XAttribute("Row",
            (new string(' ', 13) + remark.PadRight(9) + new string(' ', 7) + method + approach.PadRight(38)).PadRight(80))), explanation);

    private static XElement Comment(string text) => new(XName.Get("QDetermItem", Gaeb),
        new XElement(XName.Get("QTakeoff", Gaeb), new XAttribute("Row", (new string(' ', 12) + "*" + text.PadRight(56)).PadRight(80))));

    private static XElement Explanation(string text, params (string Name, string Value)[] attributes) =>
        new(XName.Get("Explanation", "BVBS"), text, attributes.Select(a => new XAttribute(a.Name, a.Value)));

    private static X31ReviewDocument Rows(params XElement[] rows) => RowsWith(null, null, rows);

    private static X31ReviewDocument RowsWith(Func<string, decimal>? evaluator,
        IReadOnlyDictionary<string, X31ReviewPosition>? positions, params XElement[] rows)
    {
        var document = new XDocument(new XElement(XName.Get("GAEB", Gaeb),
            new XElement(XName.Get("GAEBInfo", Gaeb), new XElement(XName.Get("Version", Gaeb), "3.3"),
                new XElement(XName.Get("VersDate", Gaeb), "2023-01")),
            new XElement(XName.Get("BoQ", Gaeb), new XElement(XName.Get("Item", Gaeb), new XAttribute("ID", "item"),
                new XElement(XName.Get("QtyDeterm", Gaeb), rows)))));
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));
        return X31ReviewReader.Read(input, positions: positions, evaluateReb91: evaluator);
    }

    public static int Main()
    {
        Test("every independent labelled identity is reconciled and foreign Explanation namespaces are ignored", () =>
        {
            XElement[][] conflicts = [
                [Calculation("1=", "(ID: 42)", Explanation("(ID: 43)"))],
                [Comment("(ID: 42)"), Calculation("1=", "", Explanation("(ID: 43)"))],
                [Comment("Revit-ElementID: 42"), Calculation("1=", "(ID: 43)")],
                [Calculation("1=", "", Explanation("(ID: 42) (ID: 43)"))],
                [Calculation("1=", "(ID: 42)", Explanation("(ID: invalid)"))]
            ];
            foreach (var conflict in conflicts)
            {
                try { Rows(conflict); }
                catch (InvalidDataException ex) when (ex.Message.Contains("Zeile", StringComparison.Ordinal)) { continue; }
                throw new Exception("Conflicting or malformed labelled identity was silently accepted.");
            }
            var foreign = Rows(Calculation("1=", "", new XElement(XName.Get("Explanation", "urn:foreign"), "(ID: 42)")));
            if (foreign.Rows.Single().ElementId != null) throw new Exception("An unrelated extension supplied model identity.");
        });
        Test("explicit raw labels and immediately preceding ID comments retain only their originating selection", () =>
        {
            var review = Rows(Calculation("1=", "(ID: 42)"), Comment("(ID: 4294967296)"),
                Comment("Formel: " + new string('x', 48)), Comment(" + zweiter Parameter"), Calculation("2="),
                Calculation("3="), Comment("(ID: 99)"), Comment("unabhängiger Hinweis"), Calculation("4="),
                Comment("Formel: (ID: 88) ist nur symbolischer Text"), Calculation("5="));
            var calculations = review.Rows.Where(r => r.Kind == X31ReviewRowKind.Quantity).ToArray();
            if (calculations[0].ElementId != "42" || calculations[1].ElementId != "4294967296"
                || calculations.Skip(2).Any(r => r.ElementId != null)
                || review.Rows.Where(r => r.Comment.StartsWith("Formel:")).Any(r => r.Kind != X31ReviewRowKind.Comment))
                throw new Exception("Labelled identities were lost or leaked through unrelated comments or subsequent calculations.");
        });
        Test("unsupported REB functions remain visible with a warning and never borrow declared results", () =>
        {
            foreach (var method in new[] { "91", "12" })
            {
                var review = RowsWith(_ => throw new InvalidDataException("Nicht unterstützte Funktion"), null,
                    Calculation("SIN(30)=", "", Explanation("(ID: 42)", ("Result", "0.5")), method));
                var row = review.Rows.Single();
                if (row.Kind != X31ReviewRowKind.Quantity || row.FormulaNumber != method || row.Formula != "SIN(30)="
                    || row.Result != "" || row.ElementId != "42"
                    || !review.Warnings.Any(w => w.Contains("nicht unterstützt", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Unsupported REB calculation was guessed, hidden, or shown without a warning.");
            }
            var noEvaluator = Rows(Calculation("2*3=", "(ID: 42)"));
            if (noEvaluator.Rows.Single().Result != "" || noEvaluator.Warnings.Count == 0)
                throw new Exception("Missing evaluator silently fabricated arithmetic support.");
        });
        Test("logical continuation retains physical rows and selection but publishes a result only once", () =>
        {
            var review = Rows(Comment("Baubereich: EG"),
                Calculation("1", "", Explanation("(ID: 42)", ("Approach", "1="), ("Result", "1"))), Calculation("="));
            var quantities = review.Rows.Skip(1).ToArray();
            if (quantities.Length != 2 || quantities[0].Result != "1" || quantities[1].Result != ""
                || quantities[1].Kind.ToString() != "Continuation" || quantities[0].Formula != "1" || quantities[1].Formula != "="
                || quantities.Any(r => r.ElementId != "42" || !r.ElementIdsForSelection.SequenceEqual(["42"]) || r.GroupLabel != "EG")
                || review.Warnings.Count != 0 || !review.Rows[0].ElementIdsForSelection.SequenceEqual(["42"]))
                throw new Exception("Logical REB approach was not safely merged without duplicate continuation totals or lost selection.");
        });
        Test("continuation rows cannot bypass the 200,000-row review limit", () =>
        {
            const int rowCount = 200_001;
            var line = new string(' ', 13) + new string(' ', 9) + new string(' ', 7) + "91" + "1";
            var xml = new StringBuilder("<GAEB xmlns='" + Gaeb + "'><GAEBInfo><Version>3.3</Version><VersDate>2023-01</VersDate></GAEBInfo><BoQ><Item ID='item'><QtyDeterm>");
            var takeoff = "<QDetermItem><QTakeoff Row=\"" + line + "\"/></QDetermItem>";
            for (var i = 0; i < rowCount; i++) xml.Append(takeoff);
            xml.Append("</QtyDeterm></Item></BoQ></GAEB>");
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
            try { X31ReviewReader.Read(input); }
            catch (InvalidDataException ex) when (ex.Message.Contains("200.000", StringComparison.Ordinal)) { return; }
            throw new Exception("A long unterminated formula continuation exceeded the configured review row limit.");
        });
        Test("X31 hierarchy and original breakdown yield full padded OZ even without a template", () =>
        {
            var xml = "<GAEB xmlns='" + Gaeb + "'><GAEBInfo><Version>3.3</Version><VersDate>2023-01</VersDate></GAEBInfo>"
                + "<BoQ ID='boq'><BoQInfo><BoQBkdn><Type>BoQLevel</Type><Length>2</Length><Num>Yes</Num></BoQBkdn>"
                + "<BoQBkdn><Type>BoQLevel</Type><Length>2</Length><Num>Yes</Num></BoQBkdn>"
                + "<BoQBkdn><Type>Item</Type><Length>4</Length><Num>Yes</Num></BoQBkdn></BoQInfo>"
                + "<BoQBody><BoQCtgy RNoPart='13'><BoQBody><BoQCtgy RNoPart='7'><BoQBody><Itemlist>"
                + "<Item ID='item' RNoPart='410'><QtyDeterm><QDetermItem><QTakeoff Row='"
                + (new string(' ', 29) + "91" + "1=".PadRight(38)).PadRight(80)
                + "'/></QDetermItem></QtyDeterm></Item></Itemlist></BoQBody></BoQCtgy></BoQBody></BoQCtgy></BoQBody></BoQ></GAEB>";
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            if (X31ReviewReader.Read(input).Rows.Single().Position != "13.07.0410")
                throw new Exception("Hierarchy or source OZ padding was lost.");
        });
        Test("template complements exact position identity with OZ short text and unchanged unit", () =>
        {
            var review = Review("", "(ID: 42)", new Dictionary<string, X31ReviewPosition>
            {
                ["item"] = new("13.07.0410", "Schalung Unterzug dreiseitig", "m²")
            });
            var row = review.Rows.Single();
            if (row.Position != "13.07.0410" || row.ShortText != "Schalung Unterzug dreiseitig" || row.Unit != "m²"
                || row.FormulaNumber != "91" || row.Result != "1")
                throw new Exception("The full review fields are missing or assigned to the wrong item.");
        });
        Test("compact labelled IDs remain actionable", () =>
        {
            var review = Review("", "(ID: 19100416) Volumen/Schnittlänge");
            if (review.Rows.Single().ElementId != "19100416")
                throw new Exception("The compact labelled ElementId was lost.");
        });
        Test("formula remarks display the symbolic formula without the provenance ID", () =>
        {
            var review = RowsWith(_ => 4.3362m, null,
                Calculation("(((1,30086/14,454)**(0,5))*14,454)=", "", Explanation(
                    "(ID: 19100416) sqrt(Volumen/Schnittlänge)*Schnittlänge",
                    ("Formula", "sqrt(Volumen/Schnittlänge)*Schnittlänge"),
                    ("Approach", "(((1,30086/14,454)**(0,5))*14,454)="),
                    ("Result", "4.3362"), ("Unit", "m2"))));
            var row = review.Rows.Single();
            if (row.Comment != "sqrt(Volumen/Schnittlänge)*Schnittlänge" || row.Result != "4,3362"
                || row.LogicalApproach != "(((1,30086/14,454)**(0,5))*14,454)=")
                throw new Exception("The formula remark included identity text or the approach/result was not evaluated.");
        });
        Test("metadata from another BoQ never supplies a plausible short text or unit", () =>
        {
            var review = Review("", "(ID: 42)", new Dictionary<string, X31ReviewPosition>
            {
                ["item"] = new("13.07.0410", "Wrong catalog", "m²", "other-boq")
            });
            if (review.Rows.Single().ShortText.Length != 0 || review.Rows.Single().Unit.Length != 0
                || !review.Warnings.Any(w => w.Contains("anderen LV", StringComparison.Ordinal)))
                throw new Exception("A catalog from a different BoQ contaminated the review.");
        });
        Test("an unproven catalog BoQ identity never complements an identified X31 BoQ", () =>
        {
            var review = Review("", "(ID: 42)", new Dictionary<string, X31ReviewPosition>
            {
                ["item"] = new("13.07.0410", "Unverified catalog", "m²")
            }, "source-boq");
            if (review.Rows.Single().ShortText.Length != 0 || review.Rows.Single().Unit.Length != 0
                || !review.Warnings.Any(w => w.Contains("anderen LV", StringComparison.Ordinal)))
                throw new Exception("Catalog metadata without a matching BoQ identity was accepted.");
        });
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
