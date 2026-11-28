using System.IO;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using BIMassist.Ava.Core;

internal static class Program
{
    internal static readonly XNamespace Ns = "http://www.gaeb.de/GAEB_DA_XML/DA31/3.3";
    [STAThread]
    private static void Main()
    {
        var read = typeof(X31ReviewReader).GetMethod("ReadForFileViewer", BindingFlags.Public | BindingFlags.Static);
        Check(read != null, "Missing file-only X31 read path: model identities must not constrain file viewing.");
        var rows = new[] { Comment("(ID: BIMassist-record-abc)"), Calculation("2*3=", "abc"), Comment("Baubereich: EG"), Comment("Baubereich: EG") };
        using var input = Xml(rows);
        var parsed = (X31ReviewDocument)read!.Invoke(null, [input, "test.x31", null, (Func<string, decimal>)X31Writer.EvaluateReb91])!;
        Check(parsed.Rows.Count == rows.Length, "File viewing must preserve every physical comment, including repeated groups.");
        Check(parsed.Rows[0].Comment == "(ID: BIMassist-record-abc)" && parsed.Rows[1].Comment == "abc", "Raw remarks were hidden or replaced.");
        Check(parsed.Rows[1].Result == "6" && parsed.Rows.All(row => row.ElementIdsForSelection.Count == 0), "File-only results must not require model identity.");
        Console.WriteLine("PASS file-only X31 accepts BIMassist identities and preserves raw comments without model selection");
        ContinuationsArePhysicalAndResultsAreHonest();
        UninterpretedRebFieldsCannotProduceMisleadingResults();
        MetadataIsExactOrDisclosed();
        MalformedAndOversizedFilesAreRejected();
        OwnWriterOutputAndPhysicalRowLimit();
        FileViewerUiTests.TableDisplaysPhysicalRows();
        FileViewerUiTests.ViewerButtonsHaveIndependentRow();
        FileViewerUiTests.OwnedSessionsSurviveCancelAndFailure();
        FileViewerUiTests.MetadataComesFromCurrentSnapshot();
        FileViewerUiTests.DispatcherShutdownReleasesChildren();
        FileViewerUiTests.UnshownOwnerCanReleaseSession();
        FileViewerWiringTests.Run();
    }

    private static void ContinuationsArePhysicalAndResultsAreHonest()
    {
        using var input = Xml([Comment("Formel: Rohkommentar"), Calculation("2+", "record-1"), Calculation(" 3="),
            Calculation("SIN(30)="), Calculation("1=", method: "12"), Calculation("7+")]);
        var parsed = X31ReviewReader.ReadForFileViewer(input, evaluateReb91: X31Writer.EvaluateReb91);
        Check(parsed.Rows.Count == 6 && parsed.Rows[2].Kind == X31ReviewRowKind.Continuation,
            "Continuation must stay a separate physical row, not a second calculation.");
        Check(parsed.Rows[1].Result == "5" && parsed.Rows[2].Result == "" && parsed.Rows[2].Formula == " 3=",
            "Logical result must appear once, preserving approach spacing on raw rows.");
        Check(parsed.Rows.Skip(3).All(row => row.Result == "") && parsed.Warnings.Count >= 3,
            "Unsupported functions/methods and incomplete approaches must disclose unavailable results, never guess.");
        Console.WriteLine("PASS continuation rows remain visible and unsupported/incomplete results are disclosed");
    }
    private static void UninterpretedRebFieldsCannotProduceMisleadingResults()
    {
        var factor = Calculation("2=");
        var attribute = factor.Element(Ns + "QTakeoff")!.Attribute("Row")!;
        attribute.Value = attribute.Value[..22] + "2".PadLeft(7) + attribute.Value[29..];
        var flag = Calculation("2=");
        var flagAttribute = flag.Element(Ns + "QTakeoff")!.Attribute("Row")!;
        flagAttribute.Value = flagAttribute.Value[..12] + "-" + flagAttribute.Value[13..];
        using var input = Xml([factor, flag]);
        var parsed = X31ReviewReader.ReadForFileViewer(input, evaluateReb91: X31Writer.EvaluateReb91);
        Check(parsed.Rows.Count == 2 && parsed.Rows.All(row => row.Result.Length == 0)
            && parsed.Warnings.Any(w => w.Contains("Faktor") || w.Contains("Kennzeichen")),
            "REB factors/flags outside the evaluated subset must stay visible but must not silently publish an unfactored result.");
        Console.WriteLine("PASS uninterpreted REB factors/flags retain raw rows and disclose unavailable results");
    }
    private static void MetadataIsExactOrDisclosed()
    {
        using var inline = Xml([Calculation("1=")], metadata: true);
        var own = X31ReviewReader.ReadForFileViewer(inline);
        Check(own.Rows[0].ShortText == "Wand" && own.Rows[0].Unit == "m2" && own.Rows[0].Position == "01.0020", "Inline file metadata and full OZ must be displayed.");
        using var missing = Xml([Calculation("1=")]);
        var absent = X31ReviewReader.ReadForFileViewer(missing);
        Check(absent.Rows[0].ShortText == "" && absent.Rows[0].Unit == "" && absent.Warnings.Any(w => w.Contains("Kurztext") && w.Contains("Einheit")),
            "Unavailable metadata must be disclosed, never invented.");
        using var exact = Xml([Calculation("1=")]);
        var complemented = X31ReviewReader.ReadForFileViewer(exact, positions: new Dictionary<string, X31ReviewPosition> { ["item-exact"] = new("01.0020", "Exakte Position", "m", "lv-exact") });
        Check(complemented.Rows[0].ShortText == "Exakte Position" && complemented.Rows[0].Unit == "m", "Exact item AND LV identity may complement missing metadata.");
        using var wrongLv = Xml([Calculation("1=")]);
        var rejected = X31ReviewReader.ReadForFileViewer(wrongLv, positions: new Dictionary<string, X31ReviewPosition> { ["item-exact"] = new("WRONG", "Falsch", "kg", "other-lv") });
        Check(rejected.Rows[0].ShortText == "" && rejected.Rows[0].Unit == "" && rejected.Rows[0].Position == "01.0020", "Different LV identity must never leak metadata or OZ.");
        Console.WriteLine("PASS exact inline/catalog metadata; missing and mismatched metadata disclosed without invention");
    }
    private static void MalformedAndOversizedFilesAreRejected()
    {
        foreach (var xml in new[] { "<broken>", "<!DOCTYPE GAEB [<!ENTITY x SYSTEM 'file:///secret'>]><GAEB>&x;</GAEB>",
            "<GAEB xmlns='urn:wrong'/>", $"<GAEB xmlns='{Ns}'><GAEBInfo><Version>3.3</Version><VersDate>2023-01</VersDate></GAEBInfo></GAEB>" })
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            Reject<InvalidDataException>(() => X31ReviewReader.ReadForFileViewer(input));
        }
        using var malformedRow = Xml([new XElement(Ns + "QDetermItem", new XElement(Ns + "QTakeoff", new XAttribute("Row", "short")))]);
        Reject<InvalidDataException>(() => X31ReviewReader.ReadForFileViewer(malformedRow));
        using var duplicate = Xml([Calculation("1=")]);
        var document = XDocument.Load(duplicate);
        var item = document.Descendants(Ns + "Item").Single();
        item.AddAfterSelf(new XElement(item));
        using var duplicateInput = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));
        Reject<InvalidDataException>(() => X31ReviewReader.ReadForFileViewer(duplicateInput));
        using var oversized = new MemoryStream(new byte[32 * 1024 * 1024 + 1]);
        Reject<InvalidDataException>(() => X31ReviewReader.ReadForFileViewer(oversized));
        Console.WriteLine("PASS malformed XML, DTDs, unsupported format, empty LV, short rows, duplicate IDs and oversized input are rejected");
    }
    private static void OwnWriterOutputAndPhysicalRowLimit()
    {
        const string x82 = """
            <GAEB xmlns="http://www.gaeb.de/GAEB_DA_XML/DA82/3.3"><GAEBInfo><Version>3.3</Version><VersDate>2021-05</VersDate><Date>2026-01-01</Date></GAEBInfo><Award><DP>82</DP><BoQ ID="lv-exact">
            <BoQInfo><Name>LV</Name><LblBoQ>LV</LblBoQ><Date>2026-01-01</Date><OutlCompl>AllTxt</OutlCompl><BoQBkdn><Type>Item</Type><Length>4</Length><Num>Yes</Num></BoQBkdn></BoQInfo>
            <BoQBody><Itemlist><Item ID="item-exact" RNoPart="0020"><Qty>1</Qty><QU>m2</QU></Item></Itemlist></BoQBody></BoQ></Award></GAEB>
            """;
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(x82));
        var template = new AvaTemplate { LvCatalog = CatalogReaders.ReadX82(source, "in-memory.x82") };
        var serialized = X31Writer.Serialize(template, [new("item-exact", 1.25m, "m2", "") { RemarkIdentity = "B0000001" },
            new("item-exact", 2.5m, "m2", "", "9223372036854775806")]);
        using var exported = new MemoryStream(Encoding.UTF8.GetBytes(serialized));
        var parsed = X31ReviewReader.ReadForFileViewer(exported, evaluateReb91: X31Writer.EvaluateReb91);
        Check(parsed.Rows.Any(row => row.Comment.Contains("B0000001")) && parsed.Rows.Where(row => row.Kind == X31ReviewRowKind.Quantity)
            .Select(row => row.Result).SequenceEqual(["1,25", "2,5"]), "Actual writer output must be readable for both BIMassistID and full 64-bit Revit identities without model validation.");
        using var minimal = Xml([]);
        var document = XDocument.Load(minimal);
        var quantity = document.Descendants(Ns + "QtyDeterm").Single();
        quantity.Add(Enumerable.Range(0, 200001).Select(_ => Comment("raw comment")));
        using var tooMany = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        Reject<InvalidDataException>(() => X31ReviewReader.ReadForFileViewer(tooMany));
        Console.WriteLine("PASS actual BIMassist X31 writer identities/results and file-viewer 200,000 physical-row limit");
    }
    internal static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static XElement Calculation(string approach, string remark = "", string method = "91") => new(Ns + "QDetermItem",
        new XElement(Ns + "QTakeoff", new XAttribute("Row", (new string(' ', 13) + remark.PadRight(9) + new string(' ', 7) + method + approach.PadRight(38)).PadRight(80))));
    internal static XElement Comment(string value) => new(Ns + "QDetermItem", new XElement(Ns + "QTakeoff",
        new XAttribute("Row", (new string(' ', 12) + "*" + value.PadRight(56)).PadRight(80))));
    internal static MemoryStream Xml(IEnumerable<XElement> rows, bool metadata = false) => new(Encoding.UTF8.GetBytes(new XDocument(
        new XElement(Ns + "GAEB", new XElement(Ns + "GAEBInfo", new XElement(Ns + "Version", "3.3"), new XElement(Ns + "VersDate", "2023-01")),
            new XElement(Ns + "BoQ", new XAttribute("ID", "lv-exact"), new XElement(Ns + "BoQBody",
                new XElement(Ns + "BoQCtgy", new XAttribute("RNoPart", "01"), new XElement(Ns + "BoQBody", new XElement(Ns + "Itemlist",
                    new XElement(Ns + "Item", new XAttribute("ID", "item-exact"), new XAttribute("RNoPart", "0020"),
                        metadata ? new XElement(Ns + "Description", new XElement(Ns + "CompleteText", new XElement(Ns + "OutlineText", new XElement(Ns + "p", "Wand")))) : null,
                        metadata ? new XElement(Ns + "QU", "m2") : null, new XElement(Ns + "QtyDeterm", rows))))))))).ToString()));
}