using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BIMassist.Ava.Core;
using BIMassist.Ava.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Even a direct executable invocation must never use the real workspace.
            var scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "ava-lv-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            Environment.SetEnvironmentVariable("BIMASSIST_AVA_WORKSPACE_SETTINGS", Path.Combine(scratch, "workspace.json"));
            Environment.SetEnvironmentVariable("BIMASSIST_AVA_PARAMETER_CHOICES", Path.Combine(scratch, "parameter-choices.json"));
            TopLevelAddUsesPositionEditor();
            Console.WriteLine("PASS Zuordnungen add/edit share PositionEditorWindow, quantities, cancel and repeated actions");
            PositionDetailsUsePlainHeadingAndFullText();
            Console.WriteLine("PASS initial and changed position details use OZ Kurztext (Einheit), blank line, full plain text, no ID");
            CatalogLabelsAppendExactUnit();
            Console.WriteLine("PASS source-snapshot and legacy LV labels append exact source unit once");
            SnapshotMarkupDisplaysFullPlainText();
            Console.WriteLine("PASS source markup preserves paragraph/br boundaries, inline spacing, literal symbols and entire long text");
            CatalogDoubleClickOpensSeparateFullDetails();
            Console.WriteLine("PASS catalog double-click opens separate full plain details without closing or mutating parent");
            SnapshotKeepsInlineSpacesAndBlankParagraphs();
            Console.WriteLine("PASS inline-only spacing, blank paragraphs and explicit repeated line breaks survive plain display");
            PrettyPrintedParagraphsHaveNoXmlIndentation();
            Console.WriteLine("PASS pretty-printed source span indentation is not displayed as LV formatting");
            ParameterNameLabelExplainsSharedParametersWithoutChangingBinding();
            Console.WriteLine("PASS explicit parameter-name label and unchanged read-only source identity/scope");
            UnsafeOrUnmatchedSnapshotsFallBackToStoredPlainText();
            Console.WriteLine("PASS malformed/hash-mismatched/DTD/duplicate/missing/wrong-BoQ snapshots fall back to unchanged stored plain text");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static AvaTemplate Fixture()
    {
        var t = new AvaTemplate { ReviewScope = "LV UI regression" };
        t.BgkCatalog.Entries.Add(new("01-10", "BGK", "", [new("100", "Type")]));
        t.LvCatalog.Entries.AddRange([
            new("first", "boq", ["01"], "01", "First", "First source text", "St"),
            new("second", "boq", ["02"], "02", "Second", "Full source\n  preserved text < 5 & > 1", "m²"),
            new("third", "boq", ["03"], "03", "Third", "Third source text", "m")]);
        t.Mappings.Add(new() { BgkKey = "01-10", TypeNumber = "100", Positions = [new() { LvItemId = "first", Quantity = new() { Kind = QuantityKind.Constant, Source = null, ConstantValue = 2, Unit = "St" } }] });
        return t;
    }

    private static AvaTemplate SnapshotFixture(string detail = "<p>Source long text</p>")
    {
        var t = Fixture();
        var xml = "<GAEB xmlns=\"http://www.gaeb.de/GAEB_DA_XML/DA82/3.3\"><BoQ ID=\"boq\"><BoQInfo><Name>LV</Name></BoQInfo><BoQBody><Itemlist><Item ID=\"second\" RNoPart=\"02\"><QU>m²</QU><Description><OutlineText><p>Second</p></OutlineText><DetailTxt>" + detail + "</DetailTxt></Description></Item></Itemlist></BoQBody></BoQ></GAEB>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        t.LvCatalog = CatalogReaders.ReadX82(stream, "source.x82");
        t.Mappings[0].Positions[0].LvItemId = "second";
        t.Mappings[0].Positions[0].Quantity = null;
        return t;
    }

    private static void UnsafeOrUnmatchedSnapshotsFallBackToStoredPlainText()
    {
        var t = SnapshotFixture();
        var catalog = t.LvCatalog;
        var entry = catalog.Entries.Single() with { Description = "  literal <p>not markup</p> &lt;example&gt;\n\n  last  " };
        var xml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(catalog.Source.X82SnapshotBase64!));
        SourceStamp Stamp(string content)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            return catalog.Source with { X82SnapshotBase64 = Convert.ToBase64String(bytes), Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) };
        }
        var item = System.Xml.Linq.XDocument.Parse(xml).Descendants().Single(element => element.Name.LocalName == "Item").ToString();
        foreach (var source in new[]
        {
            catalog.Source with { X82SnapshotBase64 = null },
            catalog.Source with { X82SnapshotBase64 = "not base64" },
            catalog.Source with { Sha256 = new string('0', 64) },
            Stamp("<malformed"),
            Stamp("<!DOCTYPE GAEB [<!ENTITY unsafe 'not allowed'>]>" + xml),
            Stamp(xml.Replace("ID=\"second\"", "ID=\"other\"", StringComparison.Ordinal)),
            Stamp(xml.Replace("ID=\"boq\"", "ID=\"other-boq\"", StringComparison.Ordinal)),
            Stamp(xml.Replace("</Itemlist>", item + "</Itemlist>", StringComparison.Ordinal)),
            Stamp(xml.Replace("DetailTxt", "OtherText", StringComparison.Ordinal))
        })
        {
            var candidate = new Catalog<LvEntry>(source, [entry]);
            Check(LvPositionDisplay.LongText(candidate, entry) == entry.Description, "Unsafe or unmatched snapshot must leave literal legacy text and whitespace intact");
        }
    }

    private static void ParameterNameLabelExplainsSharedParametersWithoutChangingBinding()
    {
        var source = new ParameterSource { Kind = ParameterKind.ByName, Name = "AVA_Volumen", Spec = "VOLUME", Scope = ParameterScope.Type };
        var editor = new ParameterSourceEditor([], source);
        var nameChoice = Field<ComboBox>(editor, "kind").Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, ParameterKind.ByName));
        Check(Equals(nameChoice.Content, "Parameter nach Name – Familie, Systemfamilie oder gemeinsam genutzte Parameter"), "By-name source label must explicitly say gemeinsam genutzte Parameter");
        var result = editor.ReadSource();
        Check(result.Kind == ParameterKind.ByName && result.Name == source.Name && result.Spec == source.Spec && result.Scope == source.Scope && result.SharedGuid == source.SharedGuid,
            "Label change must not alter source lookup or create model parameters");
    }

    private static void SnapshotKeepsInlineSpacesAndBlankParagraphs()
    {
        var t = SnapshotFixture("<div>\n  <p><span> A</span> <span>B </span></p>\n  <p/>\n  <p>C<br/><br/>D</p>\n</div>");
        Check(LvPositionDisplay.LongText(t.LvCatalog, t.LvCatalog.Entries.Single()) == " A B \n\nC\n\nD",
            "Plain text must preserve inline-only spaces, empty paragraphs and repeated br without XML indentation");
    }

    private static void PrettyPrintedParagraphsHaveNoXmlIndentation()
    {
        var t = SnapshotFixture("\n  <Text>\n    <p>\n      <span>Messtrupp einsetzen.</span>\n    </p>\n    <p/>\n    <p>\n      <span>Gerätekosten</span>\n      <span>einrechnen.</span>\n    </p>\n  </Text>\n");
        Check(LvPositionDisplay.LongText(t.LvCatalog, t.LvCatalog.Entries.Single()) == "Messtrupp einsetzen.\n\nGerätekosten einrechnen.",
            "XML indentation within p/span must not appear in the plain LV long text");
    }

    private static void CatalogDoubleClickOpensSeparateFullDetails()
    {
        var window = new AvaTemplateWindow { PendingPrompt = () => MessageBoxResult.No };
        var editor = Field<TemplateEditor>(window, "editor");
        editor.ImportJson(TemplateJson.Serialize(SnapshotFixture("<p>First<br/>next</p><div>Last</div>")));
        typeof(AvaTemplateWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000;
        window.Show();
        try
        {
            var before = TemplateJson.Serialize(editor.Document);
            var tree = (TreeView)window.FindName("LvCatalogTree");
            var row = Row(tree, "item:second");
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            var detail = window.OwnedWindows.OfType<Window>().SingleOrDefault();
            Check(detail != null && detail.IsVisible, "Catalog double-click must open a separate details window");
            try
            {
                var text = ((DockPanel)detail!.Content).Children.OfType<TextBox>().Single();
                Check(text.IsReadOnly && text.Text == "02 Second (m²)\n\nFirst\nnext\nLast", "Catalog details must show heading/blank/full plain long text, not an ID or markup");
                Check(window.IsVisible && TemplateJson.Serialize(editor.Document) == before, "Opening catalog details closed parent or changed data");
            }
            finally { detail!.Close(); }
        }
        finally { window.Close(); }
    }

    private static void SnapshotMarkupDisplaysFullPlainText()
    {
        var t = SnapshotFixture("<div>Before<p>Alpha <span font-weight=\"bold\">bold</span><br/>next</p><div>Beta &lt; 5 &amp; &gt; 1</div><p xml:space=\"preserve\">  keep  spaces</p>After &amp;lt;literal&amp;gt;</div>");
        var before = TemplateJson.Serialize(t);
        var window = new PositionEditorWindow(t, t.Mappings[0].Positions[0]);
        try
        {
            Check(Field<TextBox>(window, "lvDetail").Text == "02 Second (m²)\n\nBefore\nAlpha bold\nnext\nBeta < 5 & > 1\n  keep  spaces\nAfter &lt;literal&gt;",
                "Full source long text must retain br/div/p boundaries and inline spaces without formatting or double-decoding literals");
            Check(TemplateJson.Serialize(t) == before, "Source snapshot or entries were rewritten for display");
        }
        finally { window.Close(); }
    }

    private static void CatalogLabelsAppendExactUnit()
    {
        foreach (var t in new[] { Fixture(), SnapshotFixture() })
        {
            var tree = CatalogTreeModel.BuildLv(t);
            var node = AllNodes(tree).Single(node => node.Key == "item:second");
            Check(node.Label == "02 Second (m²)" && node.SourceId == "second", "LV tree must append (unit) after short text, preserving ID");
            var picker = new LvPositionPickerWindow(t);
            try
            {
                var row = Row(Field<TreeView>(picker, "tree"), "item:second");
                var label = ((StackPanel)row.Header).Children.OfType<TextBlock>().Single().Text;
                Check(label == node.Label, "Picker must not append duplicate unit after common tree label");
            }
            finally { picker.Close(); }
        }
    }

    private static IEnumerable<CatalogTreeNode> AllNodes(IEnumerable<CatalogTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Children)) yield return child;
        }
    }

    private static void PositionDetailsUsePlainHeadingAndFullText()
    {
        var t = Fixture();
        var before = TemplateJson.Serialize(t);
        var window = new PositionEditorWindow(t, t.Mappings[0].Positions[0]);
        try
        {
            var detail = Field<TextBox>(window, "lvDetail");
            Check(detail.IsReadOnly && detail.Text == "01 First (St)\n\nFirst source text", "Initial details must show heading, blank line and plain long text without internal ID");
            window.SelectLvEntry("second");
            Check(detail.Text == "02 Second (m²)\n\nFull source\n  preserved text < 5 & > 1", "Changed details lost source whitespace, literal symbols or full long text");
            Check(TemplateJson.Serialize(t) == before, "Display mutated catalog or mapping");
        }
        finally { window.Close(); }
    }

    private static void TopLevelAddUsesPositionEditor()
    {
        var window = new AvaTemplateWindow { PendingPrompt = () => MessageBoxResult.No };
        var editor = Field<TemplateEditor>(window, "editor");
        editor.ImportJson(TemplateJson.Serialize(Fixture()));
        window.RefreshMappings();
        window.ShowInTaskbar = false; window.Left = -10000; window.Top = -10000;
        window.Show();
        try
        {
            var tree = (TreeView)window.FindName("MappingsTree");
            Row(tree, "position:" + editor.Document.Mappings[0].Id + ":" + editor.Document.Mappings[0].Positions[0].Id).IsSelected = true;
            var sibling = System.Text.Json.JsonSerializer.Serialize(editor.Document.Mappings[0].Positions[0]);
            OpenChild(window, "Leistungsposition hinzufügen…", position =>
            {
                position.SelectLvEntry("second");
                var tabs = ((DockPanel)position.Content).Children.OfType<TabControl>().Single();
                Check(tabs.Items.Count == 2, "Add has no quantity tab"); tabs.SelectedIndex = 1;
                Field<CheckBox>(position, "hasQuantity").IsChecked = true;
                Field<TextBox>(position, "constantValue").Text = "3";
                Click(position, "Position übernehmen");
            });
            Check(window.IsVisible && editor.Document.Mappings[0].Positions.Count == 2, "Add closed parent or did not add position");
            var added = editor.Document.Mappings[0].Positions[1];
            Check(added.LvItemId == "second" && added.Quantity?.Unit == "m²" && added.Quantity.ConstantValue == 3, "Add lost LV identity or quantity");
            Check(System.Text.Json.JsonSerializer.Serialize(editor.Document.Mappings[0].Positions[0]) == sibling, "Add changed sibling IDs/quantity");
            var addedSnapshot = System.Text.Json.JsonSerializer.Serialize(added);
            OpenChild(window, "Leistungsposition hinzufügen…", position => { position.SelectLvEntry("second"); Click(position, "Position übernehmen"); });
            Check(editor.Document.Mappings[0].Positions.Count == 2 && System.Text.Json.JsonSerializer.Serialize(editor.Document.Mappings[0].Positions[1]) == addedSnapshot,
                "Repeated add duplicated existing LV identity or replaced its independent quantity/ID");
            OpenChild(window, "Leistungsposition hinzufügen…", position => { position.SelectLvEntry("third"); Click(position, "Abbrechen"); });
            Check(window.IsVisible && editor.Document.Mappings[0].Positions.Count == 2, "Cancel changed mappings or closed parent");
            Row(tree, "position:" + editor.Document.Mappings[0].Id + ":" + added.Id).IsSelected = true;
            OpenChild(window, "Ausgewählte bearbeiten…", position =>
            {
                Check(position.BuildPosition().Id == added.Id && position.BuildPosition().LvItemId == "second", "Edit changed position identity");
                Click(position, "Abbrechen");
            });
        }
        finally { window.Close(); }
    }

    private static void OpenChild(Window parent, string label, Action<PositionEditorWindow> action)
    {
        Exception? failure = null; bool seen = false;
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var child = parent.OwnedWindows.OfType<Window>().FirstOrDefault();
            if (child == null && DateTime.UtcNow - start < TimeSpan.FromSeconds(5)) return;
            timer.Stop();
            try
            {
                Check(child is PositionEditorWindow, "Add must open PositionEditorWindow directly, not " + child?.GetType().Name);
                seen = true; action((PositionEditorWindow)child!);
            }
            catch (Exception ex) { failure = ex; child?.Close(); }
        };
        try { timer.Start(); Click(parent, label); }
        finally { timer.Stop(); }
        if (failure != null) throw failure;
        Check(seen, "No position editor opened");
    }

    private static TreeViewItem Row(ItemsControl parent, string key)
    {
        TreeViewItem? Find(ItemsControl control)
        {
            foreach (TreeViewItem item in control.Items)
            {
                string? candidate = item.Tag switch { MappingTreeNode mapping => mapping.Key, CatalogTreeNode catalog => catalog.Key, _ => null };
                if (candidate == key) return item;
                if (Find(item) is { } nested) return nested;
            }
            return null;
        }
        return Find(parent) ?? throw new Exception("Missing row " + key);
    }

    private static void Click(Window window, string label)
    {
        var root = (DockPanel)window.Content;
        var panels = root.Children.OfType<WrapPanel>().Concat(root.Children.OfType<TabControl>()
            .SelectMany(tabs => tabs.Items.OfType<TabItem>()).SelectMany(tab => (tab.Content as DockPanel)?.Children.OfType<WrapPanel>() ?? []));
        panels.SelectMany(panel => panel.Children.OfType<Button>()).Single(button => Equals(button.Content, label))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}