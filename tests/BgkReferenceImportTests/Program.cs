using System.Xml.Linq;
using System.IO;

string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "App.cs"))) root = Directory.GetParent(root)?.FullName ?? throw new Exception("Repository missing");
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var xaml = XDocument.Load(Path.Combine(root, "Views", "Hauptfenster.xaml"));
XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
var import = xaml.Descendants(wpf + "Button").SingleOrDefault(b => (string?)b.Attribute("Command") == "{Binding ImportBgkFromReference}");
Check(import != null, "BGK Manager must expose its own reference-import command button");
Check((string?)import!.Attribute("Content") == "BGK aus Referenzprojekt übernehmen", "Import button must have a readable label");
Check(import.Parent!.Descendants(wpf + "Button").Any(b => (string?)b.Attribute("Command") == "{Binding Generate3DText}"), "Import must sit beside/below 3DText");
Check(import.Parent.Name == wpf + "WrapPanel" && import.Parent.Attribute("Height") == null
    && import.Descendants(wpf + "TextBlock").Any(t => (string?)t.Attribute("TextWrapping") == "Wrap"),
    "A narrow BGK pane must wrap the toolbar and full reference-import label rather than clipping it");
var vm = File.ReadAllText(Path.Combine(root, "ViewModels", "MainViewModel.cs"));
Check(vm.Contains("ImportBgkFromReference = new RelayCommand") && vm.Contains("_referenceImport.Queue(doc)") && vm.Contains("_referenceImport.Close()"), "Manager must queue its captured document and clean up the session");
Check(!vm.Contains("new BgkTransferFromReferenceCommand"), "Never invoke an IExternalCommand from modeless UI");
Console.WriteLine("PASS BGK Manager reference import wiring (static; no live host claim)");
UiLayoutTests.Run(import);
WriterTests.Run();
WorkflowTests.Run();
SessionTests.Run();
BgkReferenceTransferTests.Run();