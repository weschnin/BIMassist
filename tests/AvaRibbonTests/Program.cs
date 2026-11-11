using BIMassist.Commands;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using System.Text.RegularExpressions;

var command = new AvaTemplateCommand();
var app = new UIApplication();
var data = new ExternalCommandData { Application = app };
string message = "";
var result = command.Execute(data, ref message, new Autodesk.Revit.DB.ElementSet());
if (result != Result.Succeeded || !ReferenceEquals(app, BIMassist.Views.WpfOwner.LastOwner)
    || BIMassist.Views.WpfOwner.LastWindow == null)
    throw new Exception("AVA command must show the template editor owned by this Revit application.");
BIMassist.Views.WpfOwner.ThrowOnShow = true;
result = command.Execute(data, ref message, new Autodesk.Revit.DB.ElementSet());
if (result != Result.Failed || !message.Contains("test window failure", StringComparison.Ordinal))
    throw new Exception("Window failure must return Failed with the real error, not false success.");
var attribute = (TransactionAttribute?)Attribute.GetCustomAttribute(typeof(AvaTemplateCommand), typeof(TransactionAttribute));
if (attribute?.Mode != TransactionMode.ReadOnly)
    throw new Exception("The file-based AVA editor must not permit model transactions.");

string? root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "App.cs"))) root = Directory.GetParent(root)?.FullName;
if (root == null) throw new Exception("Repository root not found.");
string ribbon = File.ReadAllText(Path.Combine(root, "App.cs"));
var bgk = Regex.Match(ribbon, "new\\s+PulldownButtonData\\(\\s*\"BGK\"\\s*,\\s*\"BGK\"\\s*\\)");
int info = ribbon.IndexOf("new PushButtonData(\"BIMassist Info\"", StringComparison.Ordinal);
if (!bgk.Success || info <= bgk.Index) throw new Exception("BGK group must precede Info.");
string segment = ribbon[bgk.Index..info];
if (segment.Contains("new PulldownButtonData(\"AVA\"", StringComparison.Ordinal)
    || !segment.Contains("BIMassist.Commands.AvaTemplateCommand", StringComparison.Ordinal)
    || !segment.Contains("BIMassist.Commands.AvaQuantityTakeoffCommand", StringComparison.Ordinal)
    || !segment.Contains("Vorlagen verwalten", StringComparison.Ordinal))
    throw new Exception("AVA template/takeoff commands must be inside BGK, with no obsolete AVA pulldown.");
Console.WriteLine("PASS BGK ribbon placement and read-only AVA template command wiring (test doubles, not Revit runtime)");
